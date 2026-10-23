using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Models;

namespace Api.Services;

/// <summary>
/// Canonical request parsing for every JSON body. All validation rules live here so the
/// error codes and messages are deterministic and can be mirrored by the Go and Rust
/// implementations. Field validation follows the contract schema order:
/// eventId → occurredAt → metricId → value → status. See docs/api-contract.md.
/// </summary>
public static partial class RequestReader
{
    public sealed record ParseError(int Status, string Code, string Message);

    public sealed record TokenBody(string? ClientId, string? ClientSecret);

    public sealed record RefreshBody(string? RefreshToken);

    // RFC 3339 "date-time": the offset (Z or ±hh:mm) is mandatory so that the three
    // implementations accept exactly the same inputs (k6 sends RFC 3339 UTC).
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+-]\d{2}:\d{2})$")]
    private static partial Regex Rfc3339Regex();

    public static async Task<(TokenBody? Body, ParseError? Error)> ReadTokenRequestAsync(HttpRequest request)
    {
        using var doc = await TryParseAsync(request);
        if (doc is null)
        {
            return (null, InvalidJson);
        }

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (null, MissingTokenFields);
        }

        return (new TokenBody(OptionalString(root, "clientId"), OptionalString(root, "clientSecret")), null);
    }

    public static async Task<(RefreshBody? Body, ParseError? Error)> ReadRefreshRequestAsync(HttpRequest request)
    {
        using var doc = await TryParseAsync(request);
        if (doc is null)
        {
            return (null, InvalidJson);
        }

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (null, MissingRefreshToken);
        }

        return (new RefreshBody(OptionalString(root, "refreshToken")), null);
    }

    public static async Task<(List<TelemetryEventDto>? Events, ParseError? Error)> ReadBatchAsync(HttpRequest request)
    {
        using var doc = await TryParseAsync(request);
        if (doc is null)
        {
            return (null, InvalidJson);
        }

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("events", out var eventsElement)
            || eventsElement.ValueKind != JsonValueKind.Array
            || eventsElement.GetArrayLength() == 0)
        {
            return (null, EmptyBatch);
        }

        var events = new List<TelemetryEventDto>(eventsElement.GetArrayLength());
        var index = 0;
        foreach (var element in eventsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return (null, new ParseError(400, "VALIDATION_FAILED", $"events[{index}] must be a JSON object"));
            }

            // eventId
            Guid eventId;
            if (!element.TryGetProperty("eventId", out var idElement)
                || idElement.ValueKind != JsonValueKind.String)
            {
                return (null, EventFieldError(index, "eventId must be a valid UUID"));
            }

            if (!Guid.TryParseExact(idElement.GetString(), "D", out eventId))
            {
                return (null, EventFieldError(index, "eventId must be a valid UUID"));
            }

            // occurredAt (RFC 3339 date-time with mandatory offset, normalized to UTC)
            if (!element.TryGetProperty("occurredAt", out var atElement)
                || atElement.ValueKind != JsonValueKind.String)
            {
                return (null, EventFieldError(index, "occurredAt must be a valid RFC 3339 timestamp"));
            }

            var occurredText = atElement.GetString()!;
            if (!Rfc3339Regex().IsMatch(occurredText))
            {
                return (null, EventFieldError(index, "occurredAt must be a valid RFC 3339 timestamp"));
            }

            DateTimeOffset occurredAt;
            if (!DateTimeOffset.TryParse(occurredText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out occurredAt))
            {
                return (null, EventFieldError(index, "occurredAt must be a valid RFC 3339 timestamp"));
            }

            occurredAt = occurredAt.ToOffset(TimeSpan.Zero);

            // metricId
            if (!element.TryGetProperty("metricId", out var metricElement)
                || metricElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(metricElement.GetString()))
            {
                return (null, EventFieldError(index, "metricId must not be empty"));
            }

            // value (finite number)
            if (!element.TryGetProperty("value", out var valueElement)
                || valueElement.ValueKind != JsonValueKind.Number
                || !double.IsFinite(valueElement.GetDouble()))
            {
                return (null, EventFieldError(index, "value must be a finite number"));
            }

            // status
            if (!element.TryGetProperty("status", out var statusElement)
                || statusElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(statusElement.GetString()))
            {
                return (null, EventFieldError(index, "status must not be empty"));
            }

            events.Add(new TelemetryEventDto(
                eventId,
                occurredAt,
                metricElement.GetString()!,
                valueElement.GetDouble(),
                statusElement.GetString()!));
            index++;
        }

        return (events, null);
    }

    private static readonly ParseError InvalidJson = new(400, "INVALID_JSON", "request body is not valid JSON");
    private static readonly ParseError MissingTokenFields = new(400, "INVALID_REQUEST", "clientId and clientSecret are required");
    private static readonly ParseError MissingRefreshToken = new(400, "INVALID_REQUEST", "refreshToken is required");
    private static readonly ParseError EmptyBatch = new(400, "VALIDATION_FAILED", "events must be a non-empty array");

    private static ParseError EventFieldError(int index, string rule) =>
        new(400, "VALIDATION_FAILED", $"events[{index}].{rule}");

    private static async Task<JsonDocument?> TryParseAsync(HttpRequest request)
    {
        // Buffer first: a single bounded read keeps the parse fail fast and avoids
        // dependencies on stream async error semantics.
        await using var buffered = new MemoryStream();
        await request.Body.CopyToAsync(buffered);
        buffered.Position = 0;

        try
        {
            return await JsonDocument.ParseAsync(buffered);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

namespace Api.Models;

public record TokenRequest(string ClientId, string ClientSecret);

public record TokenResponse(string AccessToken, string RefreshToken, int ExpiresIn);

public record RefreshRequest(string RefreshToken);

public record TelemetryEventDto(Guid EventId, DateTimeOffset OccurredAt, string MetricId, double Value, string Status);

public record TelemetryBatchRequest(List<TelemetryEventDto> Events);

public record DeviceSnapshotDto(Guid DeviceId, TelemetryEventDto LatestEvent, DateTimeOffset LastUpdated);
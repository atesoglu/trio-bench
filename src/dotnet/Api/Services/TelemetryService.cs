using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using StackExchange.Redis;

namespace Api.Services;

public class TelemetryService(AppDbContext db, IConnectionMultiplexer redis, ILogger<TelemetryService> logger)
{
    // Atomic snapshot update: a candidate only overwrites the cached value when the
    // cached occurredAt ("t" field, epoch milliseconds) is strictly older. Concurrency
    // between batches is therefore serialized inside Redis and the cache never regresses.
    private const string CasScript = """
        local cur = redis.call('GET', KEYS[1])
        if cur and #cur > 0 then
          local ok, snapshot = pcall(cjson.decode, cur)
          if ok and snapshot and snapshot.t then
            if tonumber(snapshot.t) >= tonumber(ARGV[1]) then
              return 0
            end
          end
        end
        redis.call('SET', KEYS[1], ARGV[2])
        return 1
        """;

    private static readonly JsonSerializerOptions CacheOptions = new(JsonSerializerDefaults.Web);

    public async Task ProcessBatchAsync(Guid deviceId, List<TelemetryEventDto> events)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            // Idempotent, race-safe device auto-registration. The previous check-then-insert
            // let two concurrent batches for an unknown device collide on devices_pkey.
            await using (var device = new NpgsqlCommand(
                "INSERT INTO devices (device_id) VALUES (@device_id) ON CONFLICT (device_id) DO NOTHING",
                conn, (NpgsqlTransaction)tx.GetDbTransaction()))
            {
                device.Parameters.AddWithValue("@device_id", deviceId);
                await device.ExecuteNonQueryAsync();
            }

            // Single idempotent batch insert: the conflict clause skips duplicates inside
            // the batch and against already-persisted event ids in one round trip, and
            // RETURNING reports exactly which rows are new.
            var inserted = new List<TelemetryEventDto>();
            await using (var insert = new NpgsqlCommand(
                """
                INSERT INTO telemetry_events (event_id, device_id, occurred_at, metric_id, value, status)
                SELECT u.event_id, @device_id, u.occurred_at, u.metric_id, u.value, u.status
                FROM unnest(@event_ids, @occurred_at, @metric_ids, @values, @statuses)
                     AS u(event_id, occurred_at, metric_id, value, status)
                ON CONFLICT (event_id) DO NOTHING
                RETURNING event_id, occurred_at, metric_id, value, status
                """,
                conn, (NpgsqlTransaction)tx.GetDbTransaction()))
            {
                insert.Parameters.AddWithValue("@device_id", deviceId);
                insert.Parameters.Add(new NpgsqlParameter("event_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                    { Value = events.Select(e => e.EventId).ToArray() });
                insert.Parameters.Add(new NpgsqlParameter("occurred_at", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
                    { Value = events.Select(e => e.OccurredAt.UtcDateTime).ToArray() });
                insert.Parameters.Add(new NpgsqlParameter("metric_ids", NpgsqlDbType.Array | NpgsqlDbType.Text)
                    { Value = events.Select(e => e.MetricId).ToArray() });
                insert.Parameters.Add(new NpgsqlParameter("values", NpgsqlDbType.Array | NpgsqlDbType.Double)
                    { Value = events.Select(e => e.Value).ToArray() });
                insert.Parameters.Add(new NpgsqlParameter("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text)
                    { Value = events.Select(e => e.Status).ToArray() });

                await using var reader = await insert.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    inserted.Add(new TelemetryEventDto(
                        reader.GetGuid(0),
                        (DateTimeOffset)DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                        reader.GetString(2),
                        reader.GetDouble(3),
                        reader.GetString(4)));
                }
            }

            await tx.CommitAsync();

            // Cache updates happen strictly after the batch is durably committed, and only
            // for freshly inserted events. Among the new events the one with the greatest
            // occurredAt wins; ties keep the first in submission order (same observable
            // end state as evaluating events one by one, in order).
            var best = BestEvent(events, inserted);
            if (best is not null)
            {
                await UpdateCacheAsync(deviceId, best);
            }
        }
        catch
        {
            try
            {
                await tx.RollbackAsync();
            }
            catch
            {
                // The transaction was already terminated; disposal handles cleanup.
            }

            throw;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<DeviceSnapshotDto?> GetLatestAsync(Guid deviceId)
    {
        var key = $"device:latest:{deviceId}";
        var cachedJson = await redis.GetDatabase().StringGetAsync(key);
        if (cachedJson.HasValue
            && JsonSerializer.Deserialize<CachedSnapshot>(cachedJson.ToString(), CacheOptions) is { } cached)
        {
            return new DeviceSnapshotDto(
                deviceId,
                new TelemetryEventDto(cached.EventId, cached.OccurredAt, cached.MetricId, cached.Value, cached.Status),
                DateTimeOffset.UtcNow);
        }

        // Fallback to the source of truth. Ties on occurred_at resolve to the most
        // recently stored row (deterministic tie-break, mirrored by the other stacks).
        var latest = await db.TelemetryEvents
            .Where(e => e.DeviceId == deviceId)
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Select(e => new TelemetryEventDto(e.EventId, e.OccurredAt, e.MetricId, e.Value, e.Status))
            .FirstOrDefaultAsync();

        if (latest is null)
        {
            return null;
        }

        await UpdateCacheAsync(deviceId, latest);
        return new DeviceSnapshotDto(deviceId, latest, DateTimeOffset.UtcNow);
    }

    private static TelemetryEventDto? BestEvent(IReadOnlyList<TelemetryEventDto> submitted, IReadOnlyList<TelemetryEventDto> inserted)
    {
        if (inserted.Count == 0)
        {
            return null;
        }

        var insertedIds = inserted.Select(e => e.EventId).ToHashSet();
        TelemetryEventDto? best = null;
        foreach (var candidate in submitted)
        {
            if (!insertedIds.Contains(candidate.EventId))
            {
                continue;
            }

            if (best is null || candidate.OccurredAt > best.OccurredAt)
            {
                best = candidate;
            }
        }

        return best;
    }

    private async Task UpdateCacheAsync(Guid deviceId, TelemetryEventDto snapshot)
    {
        var key = $"device:latest:{deviceId}";
        var payload = JsonSerializer.Serialize(new
        {
            snapshot.EventId,
            snapshot.OccurredAt,
            snapshot.MetricId,
            snapshot.Value,
            snapshot.Status,
            t = snapshot.OccurredAt.ToUnixTimeMilliseconds()
        }, CacheOptions);

        try
        {
            await redis.GetDatabase().ScriptEvaluateAsync(
                CasScript,
                [key],
                [snapshot.OccurredAt.ToUnixTimeMilliseconds(), payload]);
        }
        catch (Exception ex)
        {
            // The cache is a read accelerator, not the source of truth. On failure the
            // read path falls back to PostgreSQL and repopulates the key.
            logger.LogWarning(ex,
                "snapshot cache update failed for device {DeviceId}; reads will repopulate from the database",
                deviceId);
        }
    }

    private sealed record CachedSnapshot(
        [property: JsonPropertyName("eventId")] Guid EventId,
        [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
        [property: JsonPropertyName("metricId")] string MetricId,
        [property: JsonPropertyName("value")] double Value,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("t")] long T);
}

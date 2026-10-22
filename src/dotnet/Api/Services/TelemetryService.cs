using System.Text.Json;
using Api.Data;
using Api.Domain;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Api.Services;

public class TelemetryService(AppDbContext db, IConnectionMultiplexer redis, ILogger<TelemetryService> logger)
{
    private readonly IDatabase _redis = redis.GetDatabase();

    public async Task ProcessBatchAsync(Guid deviceId, TelemetryBatchRequest request)
    {
        // Idempotent, race-safe device auto-registration. The previous check-then-insert
        // (FindAsync + Add) let two concurrent batches for an unknown device collide on
        // devices_pkey, turning a valid benchmark request into an HTTP 500.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO devices (device_id) VALUES ({deviceId}) ON CONFLICT (device_id) DO NOTHING");

        var accepted = new List<TelemetryEventDto>();

        foreach (var dto in request.Events)
        {
            var exists = await db.TelemetryEvents.AnyAsync(e => e.EventId == dto.EventId);
            if (exists) continue;

            var telemetry = new TelemetryEvent
            {
                EventId = dto.EventId,
                DeviceId = deviceId,
                OccurredAt = dto.OccurredAt,
                MetricId = dto.MetricId,
                Value = dto.Value,
                Status = dto.Status
            };

            db.TelemetryEvents.Add(telemetry);
            accepted.Add(dto);
        }

        await db.SaveChangesAsync();

        // Update the cache only after the batch is durably committed, so a failed save
        // cannot leave a snapshot in Redis that was never persisted.
        foreach (var dto in accepted)
        {
            await UpdateCacheAsync(deviceId, dto);
        }
    }

    private async Task UpdateCacheAsync(Guid deviceId, TelemetryEventDto evt)
    {
        var key = $"device:latest:{deviceId}";
        var cachedJson = await _redis.StringGetAsync(key);

        if (cachedJson.HasValue)
        {
            var cached = JsonSerializer.Deserialize<TelemetryEventDto>(cachedJson.ToString());
            if (cached != null && evt.OccurredAt <= cached.OccurredAt)
            {
                return; // Don't update with older data
            }
        }

        await _redis.StringSetAsync(key, JsonSerializer.Serialize(evt));
    }

    public async Task<DeviceSnapshotDto?> GetLatestAsync(Guid deviceId)
    {
        var key = $"device:latest:{deviceId}";
        var cachedJson = await _redis.StringGetAsync(key);

        if (cachedJson.HasValue)
        {
            var evt = JsonSerializer.Deserialize<TelemetryEventDto>(cachedJson.ToString());
            return new DeviceSnapshotDto(deviceId, evt!, DateTimeOffset.UtcNow);
        }

        // Fallback to DB
        var latest = await db.TelemetryEvents
            .Where(e => e.DeviceId == deviceId)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => new TelemetryEventDto(e.EventId, e.OccurredAt, e.MetricId, e.Value, e.Status))
            .FirstOrDefaultAsync();

        if (latest == null) return null;

        // Repopulate cache
        await _redis.StringSetAsync(key, JsonSerializer.Serialize(latest));
        return new DeviceSnapshotDto(deviceId, latest, DateTimeOffset.UtcNow);
    }
}
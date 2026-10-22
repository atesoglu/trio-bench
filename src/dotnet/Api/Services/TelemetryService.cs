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
        var device = await db.Devices.FindAsync(deviceId);
        if (device == null)
        {
            db.Devices.Add(new Device { DeviceId = deviceId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

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

            await UpdateCacheAsync(deviceId, dto);
        }

        await db.SaveChangesAsync();
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
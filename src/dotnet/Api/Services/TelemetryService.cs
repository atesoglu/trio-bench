using System.Text.Json;
using Api.Data;
using Api.Domain;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Api.Services;

public interface ITelemetryService
{
    Task<bool> ProcessBatchAsync(Guid deviceId, TelemetryBatchRequest request);
    Task<DeviceSnapshotDto?> GetLatestAsync(Guid deviceId);
}

public class TelemetryService : ITelemetryService
{
    private readonly AppDbContext _db;
    private readonly IDatabase _redis;
    private readonly ILogger<TelemetryService> _logger;

    public TelemetryService(AppDbContext db, IConnectionMultiplexer redis, ILogger<TelemetryService> logger)
    {
        _db = db;
        _redis = redis.GetDatabase();
        _logger = logger;
    }

    public async Task<bool> ProcessBatchAsync(Guid deviceId, TelemetryBatchRequest request)
    {
        using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            // Ensure device exists
            var device = await _db.Devices.FindAsync(deviceId);
            if (device == null)
            {
                _db.Devices.Add(new Device { DeviceId = deviceId, CreatedAt = DateTimeOffset.UtcNow });
                await _db.SaveChangesAsync();
            }

            foreach (var dto in request.Events)
            {
                // Idempotency check
                var exists = await _db.TelemetryEvents.AnyAsync(e => e.EventId == dto.EventId);
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

                _db.TelemetryEvents.Add(telemetry);

                // Cache logic: Only update if newer
                await UpdateCacheAsync(deviceId, dto);
            }

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing batch for device {DeviceId}", deviceId);
            await transaction.RollbackAsync();
            return false;
        }
    }

    private async Task UpdateCacheAsync(Guid deviceId, TelemetryEventDto evt)
    {
        string key = $"device:latest:{deviceId}";
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
        string key = $"device:latest:{deviceId}";
        var cachedJson = await _redis.StringGetAsync(key);

        if (cachedJson.HasValue)
        {
            var evt = JsonSerializer.Deserialize<TelemetryEventDto>(cachedJson.ToString());
            return new DeviceSnapshotDto(deviceId, evt!, DateTimeOffset.UtcNow);
        }

        // Fallback to DB
        var latest = await _db.TelemetryEvents
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
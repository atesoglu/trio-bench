using Microsoft.EntityFrameworkCore;
using Api.Domain;

namespace Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<TelemetryEvent> TelemetryEvents => Set<TelemetryEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Explicit mapping to the shared schema (database/migrations/001_initial_schema.sql).
        // Without this, EF Core queries quoted PascalCase names ("Devices", "TelemetryEvents")
        // that do not match the snake_case tables created by the migration.
        modelBuilder.Entity<Device>(entity =>
        {
            entity.ToTable("devices");
            entity.Property(d => d.DeviceId).HasColumnName("device_id");
            entity.Property(d => d.CreatedAt).HasColumnName("created_at");
            entity.Property(d => d.LastSeenAt).HasColumnName("last_seen_at");
        });

        modelBuilder.Entity<TelemetryEvent>(entity =>
        {
            entity.ToTable("telemetry_events");
            entity.Property(t => t.Id).HasColumnName("id");
            entity.Property(t => t.EventId).HasColumnName("event_id");
            entity.Property(t => t.DeviceId).HasColumnName("device_id");
            entity.Property(t => t.OccurredAt).HasColumnName("occurred_at");
            entity.Property(t => t.MetricId).HasColumnName("metric_id");
            entity.Property(t => t.Value).HasColumnName("value");
            entity.Property(t => t.Status).HasColumnName("status");

            entity.HasIndex(t => t.EventId).IsUnique().HasDatabaseName("uq_event_id");
            entity.HasIndex(t => new { t.DeviceId, t.OccurredAt }).HasDatabaseName("idx_telemetry_device_time");
        });
    }
}

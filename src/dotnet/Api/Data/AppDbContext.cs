using Microsoft.EntityFrameworkCore;
using Api.Domain;

namespace Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<TelemetryEvent> TelemetryEvents => Set<TelemetryEvent>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<DeviceAssignment> DeviceAssignments => Set<DeviceAssignment>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

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

        // Stage 2 schema (database/migrations/002_auth_and_tenancy.sql).
        modelBuilder.Entity<Client>(entity =>
        {
            entity.ToTable("clients");
            entity.Property(c => c.ClientId).HasColumnName("client_id").HasDefaultValueSql("gen_random_uuid()");
            entity.Property(c => c.Name).HasColumnName("name");
            entity.Property(c => c.SecretHash).HasColumnName("secret_hash");
            entity.Property(c => c.AllDevices).HasColumnName("all_devices");
            entity.Property(c => c.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<DeviceAssignment>(entity =>
        {
            entity.ToTable("client_devices");
            entity.HasKey(a => new { a.ClientId, a.DeviceId });
            entity.Property(a => a.ClientId).HasColumnName("client_id");
            entity.Property(a => a.DeviceId).HasColumnName("device_id");
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("refresh_tokens");
            entity.HasKey(r => r.TokenHash);
            entity.Property(r => r.TokenHash).HasColumnName("token_hash");
            entity.Property(r => r.FamilyId).HasColumnName("family_id");
            entity.Property(r => r.ClientId).HasColumnName("client_id");
            entity.Property(r => r.CreatedAt).HasColumnName("created_at");
            entity.Property(r => r.ExpiresAt).HasColumnName("expires_at");
            entity.Property(r => r.UsedAt).HasColumnName("used_at");
            entity.Property(r => r.RevokedAt).HasColumnName("revoked_at");
            entity.HasIndex(r => r.FamilyId).HasDatabaseName("idx_refresh_tokens_family");
        });
    }
}

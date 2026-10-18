using Microsoft.EntityFrameworkCore;
using Api.Domain;

namespace Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Device> Devices => Set<Device>();
    public DbSet<TelemetryEvent> TelemetryEvents => Set<TelemetryEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Device>().HasKey(d => d.DeviceId);
        modelBuilder.Entity<TelemetryEvent>().HasKey(e => e.Id);
        modelBuilder.Entity<TelemetryEvent>().HasIndex(e => e.EventId).IsUnique();
        modelBuilder.Entity<TelemetryEvent>().HasIndex(e => new { e.DeviceId, e.OccurredAt });
    }
}
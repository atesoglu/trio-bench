namespace Api.Domain;

public class TelemetryEvent
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string MetricId { get; set; } = string.Empty;
    public double Value { get; set; }
    public string Status { get; set; } = string.Empty;
}
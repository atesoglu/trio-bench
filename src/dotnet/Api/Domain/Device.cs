namespace Api.Domain;

public class Device
{
    public Guid DeviceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}
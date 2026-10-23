namespace Api.Domain;

public class Client
{
    public Guid ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SecretHash { get; set; } = string.Empty;
    public bool AllDevices { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

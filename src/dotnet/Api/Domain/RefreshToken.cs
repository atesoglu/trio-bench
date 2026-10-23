namespace Api.Domain;

public class RefreshToken
{
    public string TokenHash { get; set; } = string.Empty;
    public Guid FamilyId { get; set; }
    public Guid ClientId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

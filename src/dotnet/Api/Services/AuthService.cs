using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Api.Data;
using Api.Domain;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Api.Services;

public sealed class AuthService(AppDbContext db, ILogger<AuthService> logger)
{
    public const string AccessUsage = "access";
    public const string RefreshUsage = "refresh";
    public const string DeviceWildcard = "*";

    public static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromHours(24);

    private static readonly JwtSecurityTokenHandler Handler = new();
    private static readonly byte[] SigningKeyBytes =
        "a_very_long_and_secure_secret_key_for_benchmarking_123"u8.ToArray();
    private static readonly SymmetricSecurityKey IssuerKey = new(SigningKeyBytes);

    internal static TokenValidationParameters TokenValidationParameters() => new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = IssuerKey,
        ValidateIssuer = false,
        ValidateAudience = false
    };

    /// <summary>
    /// Verifies benchmark credentials and mints a fresh token pair:
    /// a signed JWT access token (device claims included) and a one-time-use opaque
    /// refresh token that seeds a new token family.
    /// </summary>
    public async Task<TokenResponse?> IssueTokensAsync(string clientId, string clientSecret)
    {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Name == clientId);
        if (client is null || !VerifySecret(client.SecretHash, clientSecret))
        {
            return null;
        }

        var access = await MintAccessAsync(client);
        var presented = ToBase64Url(RandomNumberGenerator.GetBytes(32));
        var row = new RefreshToken
        {
            TokenHash = HashToken(presented),
            FamilyId = Guid.NewGuid(),
            ClientId = client.ClientId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(RefreshLifetime)
        };
        db.RefreshTokens.Add(row);
        await db.SaveChangesAsync();

        return new TokenResponse(access, presented, (int)AccessLifetime.TotalSeconds);
    }

    /// <summary>
    /// One-time-use refresh token rotation with reuse detection. The atomic claim UPDATE
    /// guarantees that exactly one concurrent caller can consume a token; presenting a
    /// consumed or revoked token is treated as a stolen credential and revokes the
    /// entire family (no grace window — see docs/api-contract.md).
    /// </summary>
    public async Task<TokenResponse?> RotateAsync(string presented)
    {
        var hash = HashToken(presented);

        var claimed = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE refresh_tokens
            SET used_at = CURRENT_TIMESTAMP
            WHERE token_hash = {hash} AND used_at IS NULL AND revoked_at IS NULL
            """);

        var row = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(r => r.TokenHash == hash);
        if (claimed == 0 || row is null)
        {
            if (row is { UsedAt: not null } or { RevokedAt: not null })
            {
                await db.RefreshTokens
                    .Where(r => r.FamilyId == row.FamilyId && r.RevokedAt == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow));
                logger.LogWarning("refresh token reuse detected; token family {FamilyId} revoked", row.FamilyId);
            }

            return null;
        }

        if (row.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        var client = await db.Clients.FirstAsync(c => c.ClientId == row.ClientId);
        var access = await MintAccessAsync(client);
        var successor = ToBase64Url(RandomNumberGenerator.GetBytes(32));
        db.RefreshTokens.Add(new RefreshToken
        {
            TokenHash = HashToken(successor),
            FamilyId = row.FamilyId,
            ClientId = row.ClientId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(RefreshLifetime)
        });
        await db.SaveChangesAsync();

        return new TokenResponse(access, successor, (int)AccessLifetime.TotalSeconds);
    }

    /// <summary>Device-level authorization from the access token's "did" claims.</summary>
    public bool IsDeviceAuthorized(ClaimsPrincipal principal, Guid deviceId)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var granted = principal.FindAll("did").Select(c => c.Value).ToList();
        return granted.Contains(DeviceWildcard) || granted.Contains(deviceId.ToString("D"));
    }

    private async Task<string> MintAccessAsync(Client client)
    {
        var devices = client.AllDevices
            ? [DeviceWildcard]
            : (await db.DeviceAssignments
                .Where(a => a.ClientId == client.ClientId)
                .Select(a => a.DeviceId)
                .ToListAsync())
                .Select(d => d.ToString("D"))
                .ToList();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, client.Name),
            new("usage", AccessUsage),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        claims.AddRange(devices.Select(d => new Claim("did", d)));

        var jwt = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.Add(AccessLifetime),
            signingCredentials: new SigningCredentials(IssuerKey, SecurityAlgorithms.HmacSha256));

        return Handler.WriteToken(jwt);
    }

    private static bool VerifySecret(string expectedHash, string presented)
    {
        var expected = Convert.FromHexString(expectedHash);
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

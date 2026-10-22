using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Api.Data;
using Api.Models;
using Api.Services;
using Api.Validators;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// 1. Infrastructure
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis") ?? "localhost"));

// 2. Domain & Services
builder.Services.AddScoped<TelemetryService>();
builder.Services.AddScoped<IValidator<TelemetryBatchRequest>, TelemetryBatchValidator>();

// 3. Auth Setup
// Baseline: static benchmark credentials, but real HS256-signed JWTs so that the API's own
// bearer validation accepts exactly what /auth/token and /auth/refresh issue. Refresh
// rotation is still stateless at this stage (no stored token lineage); that upgrade is tracked separately.
const string JwtSigningKey = "a_very_long_and_secure_secret_key_for_benchmarking_123";
const string UsageAccess = "access";
const string UsageRefresh = "refresh";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => options.TokenValidationParameters = new TokenValidationParameters
{
    ValidateIssuerSigningKey = true,
    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSigningKey)),
    ValidateIssuer = false,
    ValidateAudience = false
});

builder.Services.AddAuthorization();

// 4. Observability
builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddOtlpExporter());

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// --- Endpoints ---

app.MapPost("/v1/auth/token", (TokenRequest req) =>
{
    if (req.ClientId == "admin" && req.ClientSecret == "password")
    {
        return Results.Ok(TokenPair());
    }

    return Results.Unauthorized();
});

app.MapPost("/v1/auth/refresh", (RefreshRequest req) =>
{
    ClaimsPrincipal principal;
    try
    {
        principal = ValidateToken(req.RefreshToken);
    }
    catch (Exception)
    {
        return Results.Unauthorized();
    }

    if (principal.FindFirst("usage")?.Value != UsageRefresh)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(TokenPair());
});

app.MapPost("/v1/devices/{id}/events", async (Guid id, TelemetryBatchRequest request, TelemetryService service, IValidator<TelemetryBatchRequest> validator) =>
{
    var validationResult = await validator.ValidateAsync(request);
    if (!validationResult.IsValid) return Results.BadRequest(validationResult.Errors);

    await service.ProcessBatchAsync(id, request);
    return Results.Created();
}).RequireAuthorization();

app.MapGet("/v1/devices/{id}/latest", async (Guid id, TelemetryService service) =>
{
    var snapshot = await service.GetLatestAsync(id);
    return snapshot != null ? Results.Ok(snapshot) : Results.NotFound();
}).RequireAuthorization();

app.MapGet("/.ops/health/live", () => Results.Ok("Alive"));
app.MapGet("/.ops/health/ready", async (AppDbContext db, IConnectionMultiplexer redis) =>
{
    try
    {
        if (!await db.Database.CanConnectAsync()) return Results.StatusCode(503);

        // Verifies that the shared schema has been applied, not just that the server is
        // reachable — an initdb-less database would otherwise report readiness.
        await db.Database.ExecuteSqlRawAsync("SELECT 1 FROM devices LIMIT 1");

        await redis.GetDatabase().PingAsync();
        return Results.Ok("Ready");
    }
    catch
    {
        return Results.StatusCode(503);
    }
});

app.MapGet("/.ops/metrics", () => Results.Ok("Prometheus metrics would be here via OTel"));

app.Run();

// --- Auth helpers (baseline; marked as "adapted for benchmark" for Stage 2 review) ---

static TokenResponse TokenPair() => new(
    IssueToken(UsageAccess, TimeSpan.FromMinutes(60)),
    IssueToken(UsageRefresh, TimeSpan.FromHours(24)),
    3600);
static ClaimsPrincipal ValidateToken(string token)
{
    var handler = new JwtSecurityTokenHandler();
    return handler.ValidateToken(token, new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSigningKey)),
        ValidateIssuer = false,
        ValidateAudience = false
    }, out _);
}

static string IssueToken(string usage, TimeSpan lifetime)
{
    var token = new JwtSecurityToken(
        claims: new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, "admin"),
            new Claim("usage", usage),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        },
        expires: DateTime.UtcNow.Add(lifetime),
        signingCredentials: new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSigningKey)),
            SecurityAlgorithms.HmacSha256));

    return new JwtSecurityTokenHandler().WriteToken(token);
}

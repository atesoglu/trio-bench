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

// 3. Auth Setup (Simplified for baseline)
var jwtKey = Encoding.UTF8.GetBytes("a_very_long_and_secure_secret_key_for_benchmarking_123");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => options.TokenValidationParameters = new TokenValidationParameters
{
    ValidateIssuerSigningKey = true,
    IssuerSigningKey = new SymmetricSecurityKey(jwtKey),
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
    // Baseline: simple mock auth
    if (req.ClientId == "admin" && req.ClientSecret == "password")
    {
        return Results.Ok(new TokenResponse("access_token_mock", "refresh_token_mock", 3600));
    }

    return Results.Unauthorized();
});

app.MapPost("/v1/auth/refresh", (RefreshRequest req) =>
{
    if (req.RefreshToken == "refresh_token_mock")
    {
        return Results.Ok(new TokenResponse("new_access_token", "new_refresh_token", 3600));
    }

    return Results.Unauthorized();
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
        await db.Database.CanConnectAsync();
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
using System.Security.Claims;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Prometheus;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// 1. Infrastructure
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis") ?? "localhost"));

// 2. Domain & Services
builder.Services.AddScoped<TelemetryService>();
builder.Services.AddScoped<AuthService>();

// 3. Auth Setup
// Bearer validation of our own issuer's tokens; the "usage" claim is re-checked here so
// a refresh token can never be replayed as an access token.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = AuthService.TokenValidationParameters();
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            if (context.Principal?.FindFirst("usage")?.Value != AuthService.AccessUsage)
            {
                context.Fail("token is not an access token");
            }

            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization();

// 4. Observability
builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddOtlpExporter());

var app = builder.Build();

app.UseHttpMetrics(); // request metrics for Prometheus (served at /.ops/metrics below)
app.UseAuthentication();
app.UseAuthorization();

    // Handlers branch on the parsed body being null: the parser contract guarantees that
    // a null body always carries a ParseError (see ReadTokenRequestAsync etc.). This
    // satisfies nullable flow analysis without `!` suppressions at the use sites.

// --- Endpoints ---

app.MapPost("/v1/auth/token", async (HttpRequest request, AuthService auth) =>
{
    var (body, error) = await RequestReader.ReadTokenRequestAsync(request);
    if (body is null)
    {
        return Error(error!.Status, error.Code, error.Message);
    }

    if (body.ClientId is null || body.ClientSecret is null)
    {
        return Error(400, "INVALID_REQUEST", "clientId and clientSecret are required");
    }

    var tokens = await auth.IssueTokensAsync(body.ClientId, body.ClientSecret);
    return tokens is not null
        ? Results.Json(tokens)
        : Error(401, "INVALID_CREDENTIALS", "invalid client credentials");
});

app.MapPost("/v1/auth/refresh", async (HttpRequest request, AuthService auth) =>
{
    var (body, error) = await RequestReader.ReadRefreshRequestAsync(request);
    if (body is null)
    {
        return Error(error!.Status, error.Code, error.Message);
    }

    if (body.RefreshToken is null)
    {
        return Error(400, "INVALID_REQUEST", "refreshToken is required");
    }

    var tokens = await auth.RotateAsync(body.RefreshToken);
    return tokens is not null
        ? Results.Json(tokens)
        : Error(401, "INVALID_REFRESH_TOKEN", "invalid refresh token");
});

app.MapPost("/v1/devices/{id}/events", async (string id, HttpRequest request, ClaimsPrincipal user, TelemetryService service, AuthService auth) =>
{
    if (!Guid.TryParseExact(id, "D", out var deviceId))
    {
        return Error(400, "INVALID_DEVICE_ID", "device id is not a valid UUID");
    }

    if (!auth.IsDeviceAuthorized(user, deviceId))
    {
        return Error(403, "DEVICE_NOT_AUTHORIZED", "client is not authorized for this device");
    }

    var (events, error) = await RequestReader.ReadBatchAsync(request);
    if (events is null)
    {
        return Error(error!.Status, error.Code, error.Message);
    }

    await service.ProcessBatchAsync(deviceId, events);
    return Results.Created();
}).RequireAuthorization();

app.MapGet("/v1/devices/{id}/latest", async (string id, ClaimsPrincipal user, TelemetryService service, AuthService auth) =>
{
    if (!Guid.TryParseExact(id, "D", out var deviceId))
    {
        return Error(400, "INVALID_DEVICE_ID", "device id is not a valid UUID");
    }

    // Reads answer 404 for devices the caller is not assigned to, so the API never
    // discloses the existence of another client's devices.
    if (!auth.IsDeviceAuthorized(user, deviceId))
    {
        return Results.NotFound();
    }

    var snapshot = await service.GetLatestAsync(deviceId);
    return snapshot is not null ? Results.Json(snapshot) : Results.NotFound();
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

app.MapMetrics("/.ops/metrics");

app.Run();

static IResult Error(int status, string code, string message) =>
    Results.Json(new ErrorResponse(code, message), statusCode: status);

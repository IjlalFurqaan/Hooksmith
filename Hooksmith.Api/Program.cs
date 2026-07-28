using System.Text.Json.Serialization;
using Hooksmith.Api.Services;
using Hooksmith.Infrastructure;
using Hooksmith.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        // Enums cross the wire as their names. "DeadLettered" survives an enum member being
        // inserted above it; 4 does not.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddProblemDetails();

// The single clock for the whole application. Nothing calls DateTimeOffset.UtcNow directly,
// so tests can replace this one registration and control every timestamp in the system.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddHooksmithInfrastructure(builder.Configuration);
builder.Services.AddScoped<EventIngestService>();

const string DashboardCorsPolicy = "hooksmith-dashboard";
builder.Services.AddCors(options => options.AddPolicy(
    DashboardCorsPolicy,
    policy => policy
        .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", defaultValue: false))
{
    // Migrations, never EnsureCreated: EnsureCreated builds the current model with no
    // migration history, which leaves the database unable to accept any future migration.
    // Applying at startup is a convenience for docker-compose and local runs; a production
    // deployment should run `dotnet ef database update` as its own step so a failed schema
    // change is not also a failed rollout.
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<HooksmithDbContext>();
    await db.Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseCors(DashboardCorsPolicy);
app.MapControllers();

await app.RunAsync();

/// <summary>
/// Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the API in integration tests.
/// </summary>
public partial class Program;

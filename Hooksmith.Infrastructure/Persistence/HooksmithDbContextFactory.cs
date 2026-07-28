using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hooksmith.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations</c> construct the context without booting the API.
/// </summary>
/// <remarks>
/// Design-time only. The connection string here is never used to run the application — EF
/// needs a provider configured to work out what SQL to generate, not a reachable database.
/// Override it with <c>HOOKSMITH_DESIGNTIME_CONNECTION</c> when scaffolding against a real
/// instance.
/// </remarks>
public sealed class HooksmithDbContextFactory : IDesignTimeDbContextFactory<HooksmithDbContext>
{
    private const string DefaultDesignTimeConnection =
        "Host=localhost;Port=5432;Database=hooksmith;Username=hooksmith;Password=hooksmith";

    /// <inheritdoc />
    public HooksmithDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("HOOKSMITH_DESIGNTIME_CONNECTION")
            ?? DefaultDesignTimeConnection;

        var options = new DbContextOptionsBuilder<HooksmithDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(HooksmithDbContext).Assembly.FullName))
            .Options;

        return new HooksmithDbContext(options);
    }
}

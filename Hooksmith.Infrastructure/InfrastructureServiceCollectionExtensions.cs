using Hooksmith.Infrastructure.Persistence;
using Hooksmith.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hooksmith.Infrastructure;

/// <summary>
/// Registers the infrastructure services with the application container.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>The configuration key holding the Postgres connection string.</summary>
    public const string ConnectionStringName = "Hooksmith";

    /// <summary>
    /// Adds the database context and the SSRF guard.
    /// </summary>
    /// <param name="services">The container to add to.</param>
    /// <param name="configuration">Application configuration, read for the connection string and guard options.</param>
    /// <returns>The same container, for chaining.</returns>
    /// <exception cref="InvalidOperationException">The connection string is missing.</exception>
    public static IServiceCollection AddHooksmithInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured.");

        services.AddDbContext<HooksmithDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(HooksmithDbContext).Assembly.FullName)));

        services.Configure<SsrfGuardOptions>(configuration.GetSection(SsrfGuardOptions.SectionName));
        services.AddSingleton<IDnsResolver, SystemDnsResolver>();
        services.AddSingleton<IOutboundUrlValidator, OutboundUrlValidator>();

        return services;
    }
}

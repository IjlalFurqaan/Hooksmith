using Hooksmith.Core.Retry;
using Hooksmith.Core.Signing;
using Hooksmith.Infrastructure.Dispatch;
using Hooksmith.Infrastructure.Persistence;
using Hooksmith.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<WebhookSigner>();
        services.AddSingleton<SignatureVerifier>();
        services.AddSingleton(sp => new RetryPolicy(sp.GetRequiredService<TimeProvider>()));

        services.AddScoped<IDeliveryClaimStore, DeliveryClaimStore>();
        services.AddDeliveryHttpClient();
        services.AddScoped<IWebhookDispatcher, WebhookDispatcher>();

        return services;
    }

    /// <summary>
    /// Registers the named <see cref="HttpClient"/> used for webhook dispatch.
    /// </summary>
    /// <param name="services">The container to add to.</param>
    /// <returns>The same container, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Three things here are deliberate and load-bearing.
    /// </para>
    /// <para>
    /// <b>No Polly.</b> Retries are the delivery state machine's job. A resilience pipeline on
    /// the client would retry inside a single attempt, so one logical attempt would become three
    /// HTTP requests, the attempt budget would be silently multiplied, and the attempt log would
    /// no longer describe what the subscriber actually saw.
    /// </para>
    /// <para>
    /// <b>Redirects are not followed automatically.</b> The dispatcher follows them by hand so
    /// the SSRF guard sees each hop. A public URL that answers <c>302 Location:
    /// http://169.254.169.254/</c> would otherwise walk straight past every check we made.
    /// </para>
    /// <para>
    /// <b>The connect callback validates the address.</b> That is the only check positioned to
    /// see what the socket actually connects to, and therefore the only one a DNS rebind cannot
    /// slip past.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddDeliveryHttpClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddHttpClient(WebhookDispatcher.HttpClientName, client =>
            {
                client.Timeout = WebhookDispatcher.Timeout;
                client.DefaultRequestVersion = new Version(1, 1);
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectCallback = SsrfSafeConnectCallback.Create(
                    sp.GetRequiredService<IOutboundUrlValidator>()),
            });

        return services;
    }
}

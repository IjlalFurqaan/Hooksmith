using System.Net;
using System.Net.Sockets;

namespace Hooksmith.Infrastructure.Security;

/// <summary>
/// The production <see cref="IDnsResolver"/>, backed by the operating system resolver.
/// </summary>
public sealed class SystemDnsResolver : IDnsResolver
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        try
        {
            return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // NXDOMAIN and friends are an ordinary answer to "where does this host point?",
            // not an exceptional condition. An unresolvable host is reported as no addresses,
            // which the validator treats as a denial.
            return [];
        }
    }
}

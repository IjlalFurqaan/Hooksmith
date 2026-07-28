using System.Net;

namespace Hooksmith.Infrastructure.Security;

/// <summary>
/// Decides whether Hooksmith is allowed to make an HTTP request to a given URL.
/// </summary>
/// <remarks>
/// There are three call sites, and all three are necessary:
/// <list type="number">
///   <item><description>Subscription registration, so a bad URL is rejected with a 400 rather than stored.</description></item>
///   <item><description>Immediately before each dispatch, because DNS is not a constant.</description></item>
///   <item><description>Each redirect hop, and at socket connect time, because neither is covered by the two above.</description></item>
/// </list>
/// </remarks>
public interface IOutboundUrlValidator
{
    /// <summary>Validates a URL, resolving its host.</summary>
    /// <param name="url">The URL to validate.</param>
    /// <param name="cancellationToken">Cancels the DNS lookup.</param>
    /// <returns>Whether the URL may be contacted, and what it resolved to.</returns>
    ValueTask<UrlValidationResult> ValidateAsync(Uri url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a single resolved address, without DNS.
    /// </summary>
    /// <param name="address">The address about to be connected to.</param>
    /// <param name="host">The host the address was resolved from, for allowlist checks.</param>
    /// <returns>Whether the address may be contacted.</returns>
    /// <remarks>
    /// This is the connect-time check. It is the only one that sees the address the socket
    /// will actually use, which makes it the only one a DNS rebind cannot slip past.
    /// </remarks>
    bool IsAddressAllowed(IPAddress address, string host);
}

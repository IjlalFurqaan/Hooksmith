using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;

namespace Hooksmith.Infrastructure.Security;

/// <summary>
/// The SSRF guard: resolves a subscriber URL and refuses anything that points back inside
/// the network Hooksmith is running in.
/// </summary>
/// <remarks>
/// <para>
/// The threat is that subscriber URLs are attacker-supplied by design. Anyone who can
/// register a subscription can ask this server to issue an HTTP request of their choosing
/// and — via the attempt log — read the response back. That turns the delivery worker into
/// a proxy positioned inside the trust boundary, with access to the cloud metadata service,
/// unauthenticated internal admin panels, and anything else that is only reachable because
/// it is "internal".
/// </para>
/// <para>
/// Validation is deliberately performed against every resolved address rather than the
/// first. A host that answers with <c>[93.184.216.34, 127.0.0.1]</c> would pass a check
/// that looks only at the head of the list and then connect to whichever address the
/// network stack preferred.
/// </para>
/// </remarks>
public sealed class OutboundUrlValidator : IOutboundUrlValidator
{
    private readonly IDnsResolver _dnsResolver;
    private readonly IOptionsMonitor<SsrfGuardOptions> _options;

    /// <summary>Initialises the validator.</summary>
    /// <param name="dnsResolver">Resolver used to turn hostnames into addresses.</param>
    /// <param name="options">Guard configuration, re-read on each call so it can be changed without a restart.</param>
    public OutboundUrlValidator(IDnsResolver dnsResolver, IOptionsMonitor<SsrfGuardOptions> options)
    {
        ArgumentNullException.ThrowIfNull(dnsResolver);
        ArgumentNullException.ThrowIfNull(options);

        _dnsResolver = dnsResolver;
        _options = options;
    }

    /// <inheritdoc />
    public async ValueTask<UrlValidationResult> ValidateAsync(
        Uri url,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var options = _options.CurrentValue;

        if (!url.IsAbsoluteUri)
        {
            return UrlValidationResult.Denied(
                UrlRejectionReason.NotAbsolute,
                "Subscriber URL must be absolute.");
        }

        if (!IsSchemeAllowed(url.Scheme, options))
        {
            return UrlValidationResult.Denied(
                UrlRejectionReason.UnsupportedScheme,
                options.RequireHttps
                    ? "Subscriber URL must use https."
                    : $"Scheme '{url.Scheme}' is not supported; use http or https.");
        }

        // file://, gopher:// and friends are already gone via the scheme check. Credentials
        // are refused separately because http://expected.test@169.254.169.254/ has a host of
        // 169.254.169.254, and a human skim-reading the registration sees expected.test.
        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            return UrlValidationResult.Denied(
                UrlRejectionReason.CredentialsInUrl,
                "Subscriber URL must not embed credentials.");
        }

        if (string.IsNullOrWhiteSpace(url.Host))
        {
            return UrlValidationResult.Denied(UrlRejectionReason.MissingHost, "Subscriber URL has no host.");
        }

        if (options.IsHostAllowlisted(url.Host))
        {
            return UrlValidationResult.Allowed([]);
        }

        var addresses = await ResolveAsync(url, cancellationToken).ConfigureAwait(false);
        if (addresses.Count == 0)
        {
            return UrlValidationResult.Denied(
                UrlRejectionReason.DnsResolutionFailed,
                $"Host '{url.Host}' did not resolve to any address.");
        }

        foreach (var address in addresses)
        {
            if (!IsAddressAllowedCore(address, options, out var reason))
            {
                return UrlValidationResult.Denied(
                    UrlRejectionReason.BlockedAddress,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Host '{url.Host}' resolves to {address}, which is {reason}."));
            }
        }

        return UrlValidationResult.Allowed(addresses);
    }

    /// <inheritdoc />
    public bool IsAddressAllowed(IPAddress address, string host)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(host);

        var options = _options.CurrentValue;
        return options.IsHostAllowlisted(host) || IsAddressAllowedCore(address, options, out _);
    }

    private async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(Uri url, CancellationToken cancellationToken)
    {
        // An IP literal is not a DNS name and must not be handed to the resolver; it is
        // already the answer. Uri reports these via HostNameType.
        if (url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            && IPAddress.TryParse(url.Host.Trim('[', ']'), out var literal))
        {
            return [literal];
        }

        return await _dnsResolver.ResolveAsync(url.IdnHost, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSchemeAllowed(string scheme, SsrfGuardOptions options) =>
        options.RequireHttps
            ? string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            : string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
              || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool IsAddressAllowedCore(IPAddress address, SsrfGuardOptions options, out string reason)
    {
        if (!BlockedAddressRanges.IsBlocked(address, out reason))
        {
            return true;
        }

        var normalised = BlockedAddressRanges.Normalise(address);

        if (options.AllowLoopback && IPAddress.IsLoopback(normalised))
        {
            return true;
        }

        return options.AllowPrivateNetworks && IsPrivateNetwork(normalised);
    }

    /// <summary>
    /// Whether an address is in a range that a deployment might legitimately share with its
    /// subscribers, as opposed to one that is never a real endpoint.
    /// </summary>
    /// <remarks>
    /// <see cref="SsrfGuardOptions.AllowPrivateNetworks"/> deliberately opens only RFC1918
    /// and IPv6 unique-local. Link-local stays blocked even then, because 169.254.169.254 is
    /// the cloud metadata endpoint and nobody's webhook receiver lives there.
    /// </remarks>
    private static bool IsPrivateNetwork(IPAddress address)
    {
        foreach (var range in BlockedAddressRanges.IPv4)
        {
            if (range.Reason.StartsWith("RFC1918", StringComparison.Ordinal) && range.Network.Contains(address))
            {
                return true;
            }
        }

        foreach (var range in BlockedAddressRanges.IPv6)
        {
            if (range.Reason.StartsWith("unique local", StringComparison.Ordinal) && range.Network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }
}

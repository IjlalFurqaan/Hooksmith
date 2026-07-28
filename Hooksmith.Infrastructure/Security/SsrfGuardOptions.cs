namespace Hooksmith.Infrastructure.Security;

/// <summary>
/// Configuration for <see cref="OutboundUrlValidator"/>.
/// </summary>
/// <remarks>
/// Every relaxation here defaults to the safe value. Turning one on is an explicit act in a
/// configuration file, so a production deployment that says nothing gets the strict policy.
/// </remarks>
public sealed class SsrfGuardOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Ssrf";

    /// <summary>
    /// Permits subscriber endpoints on loopback addresses. Development only — this is what
    /// lets you point a subscription at <c>http://localhost:4200</c> while building.
    /// </summary>
    public bool AllowLoopback { get; init; }

    /// <summary>
    /// Permits subscriber endpoints on RFC1918 private ranges. Useful when Hooksmith and its
    /// subscribers share a private network, such as inside a Docker compose network.
    /// </summary>
    public bool AllowPrivateNetworks { get; init; }

    /// <summary>
    /// Requires <c>https</c> for subscriber endpoints. Off by default so local and
    /// intra-network subscribers work; worth turning on for an internet-facing deployment.
    /// </summary>
    public bool RequireHttps { get; init; }

    /// <summary>
    /// Hostnames exempt from address checks entirely, matched case-insensitively against the
    /// URL host. Intended for named services on a container network whose addresses are
    /// private by design.
    /// </summary>
    /// <remarks>
    /// This is a genuine hole in the guard, which is why it is an explicit list of names
    /// rather than a pattern. Everything on it can be reached by anyone who can register a
    /// subscription.
    /// </remarks>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    /// <summary>
    /// How many redirects a dispatch may follow. Each hop is re-validated, so this only
    /// bounds the work, but an unbounded chain is its own denial-of-service.
    /// </summary>
    public int MaxRedirects { get; init; } = 3;

    /// <summary>Whether address checks are bypassed for the given host.</summary>
    /// <param name="host">The URL host to test.</param>
    /// <returns><see langword="true"/> if the host is explicitly allowlisted.</returns>
    public bool IsHostAllowlisted(string host)
    {
        for (var i = 0; i < AllowedHosts.Count; i++)
        {
            if (string.Equals(AllowedHosts[i], host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

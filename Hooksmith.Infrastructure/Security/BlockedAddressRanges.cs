using System.Net;

namespace Hooksmith.Infrastructure.Security;

/// <summary>
/// The set of IP ranges a subscriber endpoint is never allowed to resolve to.
/// </summary>
/// <remarks>
/// <para>
/// Like the delivery transition table, this is written as data. A reviewer can read the
/// whole policy in one screenful and a test can enumerate it, which is not true of a
/// sequence of <c>if</c> statements over octets.
/// </para>
/// <para>
/// The list is wider than "loopback, link-local and RFC1918" on purpose. Every extra entry
/// here closes a documented way of reaching something that is not a subscriber.
/// </para>
/// </remarks>
public static class BlockedAddressRanges
{
    /// <summary>IPv4 ranges that never belong to a legitimate subscriber endpoint.</summary>
    public static readonly IReadOnlyList<BlockedRange> IPv4 =
    [
        new(IPNetwork.Parse("0.0.0.0/8"), "unspecified / this-network"),
        new(IPNetwork.Parse("10.0.0.0/8"), "RFC1918 private"),
        new(IPNetwork.Parse("100.64.0.0/10"), "RFC6598 carrier-grade NAT"),
        new(IPNetwork.Parse("127.0.0.0/8"), "loopback"),
        new(IPNetwork.Parse("169.254.0.0/16"), "link-local, including the 169.254.169.254 cloud metadata service"),
        new(IPNetwork.Parse("172.16.0.0/12"), "RFC1918 private"),
        new(IPNetwork.Parse("192.0.0.0/24"), "IETF protocol assignments"),
        new(IPNetwork.Parse("192.0.2.0/24"), "TEST-NET-1 documentation"),
        new(IPNetwork.Parse("192.168.0.0/16"), "RFC1918 private"),
        new(IPNetwork.Parse("198.18.0.0/15"), "benchmarking"),
        new(IPNetwork.Parse("198.51.100.0/24"), "TEST-NET-2 documentation"),
        new(IPNetwork.Parse("203.0.113.0/24"), "TEST-NET-3 documentation"),
        new(IPNetwork.Parse("224.0.0.0/4"), "multicast"),
        new(IPNetwork.Parse("240.0.0.0/4"), "reserved, including the 255.255.255.255 broadcast address"),
    ];

    /// <summary>IPv6 ranges that never belong to a legitimate subscriber endpoint.</summary>
    public static readonly IReadOnlyList<BlockedRange> IPv6 =
    [
        new(IPNetwork.Parse("::/128"), "unspecified"),
        new(IPNetwork.Parse("::1/128"), "loopback"),
        new(IPNetwork.Parse("64:ff9b::/96"), "NAT64, which translates straight back onto IPv4 targets"),
        new(IPNetwork.Parse("100::/64"), "discard-only"),
        new(IPNetwork.Parse("2001:db8::/32"), "documentation"),
        new(IPNetwork.Parse("fc00::/7"), "unique local, covering the fd00::/8 half that is actually assignable"),
        new(IPNetwork.Parse("fe80::/10"), "link-local"),
        new(IPNetwork.Parse("ff00::/8"), "multicast"),
    ];

    /// <summary>
    /// Determines whether an address falls inside a blocked range.
    /// </summary>
    /// <param name="address">The address to test.</param>
    /// <param name="reason">On a block, why the address was rejected.</param>
    /// <returns><see langword="true"/> if the address must not be contacted.</returns>
    /// <remarks>
    /// IPv4-mapped IPv6 addresses are unwrapped before the check. Without that step
    /// <c>::ffff:169.254.169.254</c> misses every IPv4 rule (it is not an IPv4 address) and
    /// every IPv6 rule (it is not inside any blocked IPv6 range), yet the network stack
    /// happily connects it to the metadata service.
    /// </remarks>
    public static bool IsBlocked(IPAddress address, out string reason)
    {
        ArgumentNullException.ThrowIfNull(address);

        var candidate = Normalise(address);
        var ranges = candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? IPv4 : IPv6;

        foreach (var range in ranges)
        {
            if (range.Network.Contains(candidate))
            {
                reason = range.Reason;
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Reduces an address to the form the blocklist is expressed in.
    /// </summary>
    /// <param name="address">The address to normalise.</param>
    /// <returns>The unwrapped IPv4 address, or the original address with any scope id removed.</returns>
    public static IPAddress Normalise(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        // A scope id (fe80::1%12) does not change which network the address is in, but it
        // does change equality and formatting, so it is dropped before matching.
        // The family check has to come first: IPAddress.ScopeId throws on an IPv4 address.
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes())
            : address;
    }

    /// <summary>A blocked network paired with the reason it is blocked.</summary>
    /// <param name="Network">The CIDR range.</param>
    /// <param name="Reason">Why an address in this range is never a valid subscriber.</param>
    public sealed record BlockedRange(IPNetwork Network, string Reason);
}

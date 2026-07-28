using System.Net;

namespace Hooksmith.Infrastructure.Security;

/// <summary>Why a subscriber URL was rejected.</summary>
public enum UrlRejectionReason
{
    /// <summary>The URL was accepted.</summary>
    None = 0,

    /// <summary>The URL was relative, or not a well-formed absolute URI.</summary>
    NotAbsolute = 1,

    /// <summary>The scheme was something other than <c>http</c>, or <c>https</c> when required.</summary>
    UnsupportedScheme = 2,

    /// <summary>The URL embedded credentials, which are never sent and usually signal an attempt to confuse the parser.</summary>
    CredentialsInUrl = 3,

    /// <summary>The URL had no host component.</summary>
    MissingHost = 4,

    /// <summary>The host did not resolve to any address.</summary>
    DnsResolutionFailed = 5,

    /// <summary>The host resolved to an address inside a blocked range.</summary>
    BlockedAddress = 6,
}

/// <summary>
/// The outcome of validating a subscriber URL, including the addresses it resolved to.
/// </summary>
/// <param name="IsAllowed">Whether the URL may be contacted.</param>
/// <param name="Reason">The rejection category, or <see cref="UrlRejectionReason.None"/>.</param>
/// <param name="Detail">A human-readable explanation, safe to return to the registering client.</param>
/// <param name="ResolvedAddresses">
/// Every address the host resolved to. Empty when the URL was rejected before resolution.
/// </param>
public sealed record UrlValidationResult(
    bool IsAllowed,
    UrlRejectionReason Reason,
    string? Detail,
    IReadOnlyList<IPAddress> ResolvedAddresses)
{
    /// <summary>Builds an accepting result.</summary>
    /// <param name="resolvedAddresses">The addresses the host resolved to.</param>
    /// <returns>An allowed result.</returns>
    public static UrlValidationResult Allowed(IReadOnlyList<IPAddress> resolvedAddresses) =>
        new(true, UrlRejectionReason.None, null, resolvedAddresses);

    /// <summary>Builds a rejecting result.</summary>
    /// <param name="reason">The rejection category.</param>
    /// <param name="detail">A human-readable explanation.</param>
    /// <returns>A denied result.</returns>
    public static UrlValidationResult Denied(UrlRejectionReason reason, string detail) =>
        new(false, reason, detail, []);
}

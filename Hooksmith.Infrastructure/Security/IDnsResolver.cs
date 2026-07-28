using System.Net;

namespace Hooksmith.Infrastructure.Security;

/// <summary>
/// Resolves a hostname to its IP addresses.
/// </summary>
/// <remarks>
/// Abstracted purely so the SSRF guard can be tested against a hostile DNS server without
/// one existing. The table-driven tests need a host that resolves to 169.254.169.254, and
/// registering a real domain that does so is not a reasonable test fixture.
/// </remarks>
public interface IDnsResolver
{
    /// <summary>Resolves a hostname to every address it currently answers with.</summary>
    /// <param name="host">The hostname to resolve.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>Every resolved address; empty if the host does not resolve.</returns>
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default);
}

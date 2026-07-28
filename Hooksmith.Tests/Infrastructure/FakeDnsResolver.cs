using System.Net;
using Hooksmith.Infrastructure.Security;

namespace Hooksmith.Tests.Infrastructure;

/// <summary>
/// A scriptable DNS resolver, so the SSRF tests can describe hostile answers without
/// depending on a network or on someone actually registering evil.test.
/// </summary>
internal sealed class FakeDnsResolver : IDnsResolver
{
    private readonly Dictionary<string, IReadOnlyList<IPAddress>> _answers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hosts asked about, in order, so a test can assert the resolver was consulted.</summary>
    public List<string> ResolvedHosts { get; } = [];

    public FakeDnsResolver Answer(string host, params string[] addresses)
    {
        _answers[host] = addresses.Select(IPAddress.Parse).ToList();
        return this;
    }

    /// <summary>Configures a host that does not resolve at all.</summary>
    public FakeDnsResolver AnswerNothing(string host)
    {
        _answers[host] = [];
        return this;
    }

    public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        ResolvedHosts.Add(host);

        // An unconfigured host resolves to a public address, so a test that means to probe
        // the blocklist has to say so explicitly rather than passing by accident.
        return ValueTask.FromResult(
            _answers.TryGetValue(host, out var addresses)
                ? addresses
                : (IReadOnlyList<IPAddress>)[IPAddress.Parse("93.184.216.34")]);
    }
}

using System.Net;
using Hooksmith.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Hooksmith.Tests.Infrastructure;

/// <summary>
/// Table-driven coverage of the SSRF guard against a list of hostile URLs.
/// </summary>
/// <remarks>
/// Each entry is a real technique, not a variation on the same one. The point of writing
/// them as a table is that adding tomorrow's bypass is one line, and that the list itself
/// documents what the guard claims to stop.
/// </remarks>
public sealed class SsrfGuardTests
{
    /// <summary>URLs that must never be accepted under the default (production) policy.</summary>
    public static TheoryData<string, string> HostileUrls() => new()
    {
        { "http://127.0.0.1/hook", "IPv4 loopback by literal" },
        { "http://127.0.0.2/hook", "the rest of 127/8 is loopback too, not just .1" },
        { "http://localhost/hook", "loopback by name" },
        { "http://[::1]/hook", "IPv6 loopback" },
        { "http://169.254.169.254/latest/meta-data/", "AWS/Azure/GCP instance metadata" },
        { "http://169.254.170.2/v2/credentials", "ECS task credential endpoint" },
        { "http://[fd00::1]/hook", "IPv6 unique-local" },
        { "http://[fe80::1]/hook", "IPv6 link-local" },
        { "http://10.0.0.5/hook", "RFC1918 10/8" },
        { "http://172.16.31.9/hook", "RFC1918 172.16/12" },
        { "http://192.168.1.1/hook", "RFC1918 192.168/16" },
        { "http://0.0.0.0/hook", "unspecified address, which routes to localhost on Linux" },
        { "http://[::ffff:127.0.0.1]/hook", "IPv4-mapped IPv6 loopback: misses naive v4 and v6 checks alike" },
        { "http://[::ffff:169.254.169.254]/hook", "IPv4-mapped IPv6 metadata address" },
        { "http://100.64.0.1/hook", "carrier-grade NAT space" },
        { "http://255.255.255.255/hook", "broadcast" },
        { "http://224.0.0.1/hook", "multicast" },
        { "http://[64:ff9b::7f00:1]/hook", "NAT64 prefix wrapping an IPv4 loopback address" },
        { "file:///etc/passwd", "non-HTTP scheme" },
        { "gopher://127.0.0.1:6379/_INFO", "gopher, the classic Redis-via-SSRF vector" },
        { "ftp://192.168.0.1/", "another non-HTTP scheme" },
        { "http://expected.test@169.254.169.254/", "credentials in the URL: the real host is after the @" },
        { "http://metadata.internal/latest/", "public-looking name that resolves to the metadata address" },
        { "http://rebind.test/hook", "one public and one private answer from the same name" },
        { "http://nowhere.test/hook", "a host that does not resolve at all" },
    };

    [Theory]
    [MemberData(nameof(HostileUrls))]
    public async Task Hostile_urls_are_rejected_under_the_default_policy(string url, string technique)
    {
        var validator = CreateValidator(new SsrfGuardOptions());

        var result = await validator.ValidateAsync(new Uri(url, UriKind.Absolute));

        result.IsAllowed.Should().BeFalse("{0} must not be reachable ({1})", url, technique);
        result.Reason.Should().NotBe(UrlRejectionReason.None);
    }

    [Theory]
    [InlineData("https://hooks.example.com/webhooks/inbound")]
    [InlineData("http://hooks.example.com:8080/inbound")]
    [InlineData("https://hooks.example.com/inbound?tenant=acme")]
    [InlineData("http://93.184.216.34/inbound")]
    public async Task Ordinary_public_endpoints_are_accepted(string url)
    {
        var validator = CreateValidator(new SsrfGuardOptions());

        var result = await validator.ValidateAsync(new Uri(url, UriKind.Absolute));

        result.IsAllowed.Should().BeTrue(result.Detail);
    }

    [Fact]
    public async Task Every_resolved_address_is_checked_not_just_the_first()
    {
        // A hostile DNS server answers with a good address first and a loopback second.
        // Checking only addresses[0] passes this and then connects to whichever address the
        // network stack decides it prefers.
        var validator = CreateValidator(
            new SsrfGuardOptions(),
            dns => dns.Answer("rebind.test", "93.184.216.34", "127.0.0.1"));

        var result = await validator.ValidateAsync(new Uri("http://rebind.test/hook"));

        result.IsAllowed.Should().BeFalse();
        result.Reason.Should().Be(UrlRejectionReason.BlockedAddress);
        result.Detail.Should().Contain("127.0.0.1");
    }

    [Fact]
    public async Task An_ip_literal_is_never_sent_to_the_resolver()
    {
        var dns = new FakeDnsResolver();
        var validator = CreateValidator(new SsrfGuardOptions(), _ => { }, dns);

        await validator.ValidateAsync(new Uri("http://127.0.0.1/hook"));

        dns.ResolvedHosts.Should().BeEmpty("an address literal is already the answer");
    }

    [Fact]
    public async Task AllowLoopback_opens_localhost_but_leaves_the_metadata_address_shut()
    {
        var validator = CreateValidator(new SsrfGuardOptions { AllowLoopback = true });

        (await validator.ValidateAsync(new Uri("http://localhost:4200/hook"))).IsAllowed
            .Should().BeTrue("this is the switch that makes local development possible");
        (await validator.ValidateAsync(new Uri("http://127.0.0.1:4200/hook"))).IsAllowed.Should().BeTrue();

        (await validator.ValidateAsync(new Uri("http://169.254.169.254/latest/meta-data/"))).IsAllowed
            .Should().BeFalse("loopback is not link-local, and nobody's receiver lives on the metadata service");
        (await validator.ValidateAsync(new Uri("http://10.0.0.5/hook"))).IsAllowed
            .Should().BeFalse("AllowLoopback must not imply AllowPrivateNetworks");
    }

    [Fact]
    public async Task AllowPrivateNetworks_opens_rfc1918_but_leaves_link_local_shut()
    {
        var validator = CreateValidator(new SsrfGuardOptions { AllowPrivateNetworks = true });

        (await validator.ValidateAsync(new Uri("http://10.0.0.5/hook"))).IsAllowed.Should().BeTrue();
        (await validator.ValidateAsync(new Uri("http://192.168.1.1/hook"))).IsAllowed.Should().BeTrue();
        (await validator.ValidateAsync(new Uri("http://[fd00::1]/hook"))).IsAllowed.Should().BeTrue();

        (await validator.ValidateAsync(new Uri("http://169.254.169.254/"))).IsAllowed
            .Should().BeFalse("the cloud metadata service is not part of anyone's private network");
        (await validator.ValidateAsync(new Uri("http://127.0.0.1/hook"))).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task An_allowlisted_host_bypasses_address_checks_entirely()
    {
        // The compose-network case: `receiver` is a container name whose address is private
        // by definition, and no address policy can express "this particular private host".
        var validator = CreateValidator(
            new SsrfGuardOptions { AllowedHosts = ["receiver"] },
            dns => dns.Answer("receiver", "172.18.0.4"));

        (await validator.ValidateAsync(new Uri("http://receiver:8080/hook"))).IsAllowed.Should().BeTrue();
        (await validator.ValidateAsync(new Uri("http://other:8080/hook"))).IsAllowed
            .Should().BeFalse("the allowlist is a list of names, not a pattern");
    }

    [Fact]
    public async Task RequireHttps_rejects_plaintext()
    {
        var validator = CreateValidator(new SsrfGuardOptions { RequireHttps = true });

        var result = await validator.ValidateAsync(new Uri("http://hooks.example.com/inbound"));

        result.IsAllowed.Should().BeFalse();
        result.Reason.Should().Be(UrlRejectionReason.UnsupportedScheme);
        (await validator.ValidateAsync(new Uri("https://hooks.example.com/inbound"))).IsAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", true)]
    public void IsAddressAllowed_is_the_connect_time_check(string address, bool expected)
    {
        var validator = CreateValidator(new SsrfGuardOptions());

        validator.IsAddressAllowed(IPAddress.Parse(address), "hooks.example.com").Should().Be(expected);
    }

    [Fact]
    public void Normalise_unwraps_ipv4_mapped_addresses()
    {
        BlockedAddressRanges.Normalise(IPAddress.Parse("::ffff:169.254.169.254"))
            .Should().Be(IPAddress.Parse("169.254.169.254"));

        BlockedAddressRanges.IsBlocked(IPAddress.Parse("::ffff:169.254.169.254"), out var reason)
            .Should().BeTrue();
        reason.Should().Contain("link-local");
    }

    [Fact]
    public async Task A_host_that_does_not_resolve_is_rejected_rather_than_assumed_safe()
    {
        var validator = CreateValidator(
            new SsrfGuardOptions(),
            dns => dns.AnswerNothing("nowhere.test"));

        var result = await validator.ValidateAsync(new Uri("http://nowhere.test/hook"));

        result.IsAllowed.Should().BeFalse();
        result.Reason.Should().Be(UrlRejectionReason.DnsResolutionFailed);
    }

    private static OutboundUrlValidator CreateValidator(
        SsrfGuardOptions options,
        Action<FakeDnsResolver>? configureDns = null,
        FakeDnsResolver? resolver = null)
    {
        var dns = resolver ?? new FakeDnsResolver();

        dns.Answer("localhost", "127.0.0.1")
            .Answer("metadata.internal", "169.254.169.254")
            .Answer("rebind.test", "93.184.216.34", "127.0.0.1")
            .Answer("hooks.example.com", "93.184.216.34")
            .Answer("other", "172.18.0.9")
            .AnswerNothing("nowhere.test");

        configureDns?.Invoke(dns);

        return new OutboundUrlValidator(dns, new StaticOptionsMonitor<SsrfGuardOptions>(options));
    }
}

/// <summary>Minimal <see cref="IOptionsMonitor{T}"/> over a fixed value.</summary>
internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

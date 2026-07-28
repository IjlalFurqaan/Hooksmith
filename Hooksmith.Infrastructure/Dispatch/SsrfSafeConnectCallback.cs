using System.Net;
using System.Net.Sockets;
using Hooksmith.Infrastructure.Security;

namespace Hooksmith.Infrastructure.Dispatch;

/// <summary>
/// A <see cref="SocketsHttpHandler.ConnectCallback"/> that validates the address it is about to
/// connect to, and then connects to <em>that</em> address.
/// </summary>
/// <remarks>
/// <para>
/// This is the check that closes DNS rebinding, and it is the only one that can. Validating a
/// URL before dispatch resolves the hostname, approves the addresses, and then hands the
/// <em>hostname</em> back to <see cref="HttpClient"/>, which resolves it again. A resolver
/// answering with a short TTL and alternating records gives a public address to the validator
/// and a private one to the connection — the classic time-of-check/time-of-use gap.
/// </para>
/// <para>
/// Taking over the connect step removes the second lookup entirely: we resolve once, discard
/// every address the guard rejects, and open the socket to a specific approved
/// <see cref="IPAddress"/>. There is no name left for the stack to re-resolve.
/// </para>
/// </remarks>
public static class SsrfSafeConnectCallback
{
    /// <summary>Builds the callback.</summary>
    /// <param name="validator">The SSRF guard consulted for each candidate address.</param>
    /// <returns>A connect callback suitable for <see cref="SocketsHttpHandler"/>.</returns>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Create(
        IOutboundUrlValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);

        return async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;

            var candidates = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

            var approved = candidates.Where(address => validator.IsAddressAllowed(address, host)).ToArray();

            if (approved.Length == 0)
            {
                // Refusing here rather than connecting-then-checking means the packet is never
                // sent. A blocked internal service should not even see the connection attempt.
                throw new HttpRequestException(
                    $"Refusing to connect to '{host}': it resolves only to addresses blocked by the SSRF guard.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

            try
            {
                await socket.ConnectAsync(approved, port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }
}

using System.Buffers.Text;
using System.Security.Cryptography;

namespace Hooksmith.Core.Signing;

/// <summary>
/// Generates the shared secrets that subscription payloads are signed with.
/// </summary>
public static class WebhookSecret
{
    /// <summary>The prefix every generated secret carries, so a leaked one is recognisable in a log.</summary>
    public const string Prefix = "whsec_";

    private const int EntropyBytes = 32;

    /// <summary>
    /// Generates a new secret with 256 bits of cryptographic entropy.
    /// </summary>
    /// <returns>A secret of the form <c>whsec_&lt;base64url&gt;</c>.</returns>
    /// <remarks>
    /// <see cref="RandomNumberGenerator"/>, not <see cref="Random"/>. The retry policy's jitter
    /// can be predictable without consequence; a predictable signing secret means anyone can
    /// forge deliveries. Base64url rather than plain base64 so the value survives being pasted
    /// into a URL or an environment file without escaping.
    /// </remarks>
    public static string Generate() =>
        Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(EntropyBytes));
}

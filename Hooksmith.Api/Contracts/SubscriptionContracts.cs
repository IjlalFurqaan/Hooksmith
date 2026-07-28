using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Hooksmith.Core;

namespace Hooksmith.Api.Contracts;

/// <summary>Request body for registering a subscription.</summary>
/// <param name="Name">Human-readable label.</param>
/// <param name="Url">Absolute HTTP(S) endpoint to deliver to. Validated by the SSRF guard before it is stored.</param>
/// <param name="EventTypes">Event types to receive. Normalised to lowercase.</param>
/// <param name="Secret">
/// Optional shared secret. Omit it and a cryptographically random one is generated, which is
/// the recommended path — a secret you chose is a secret you might have chosen badly.
/// </param>
/// <param name="IsActive">Whether the subscription starts active. Defaults to true.</param>
public sealed record CreateSubscriptionRequest(
    [property: Required, MaxLength(200)] string Name,
    [property: Required, MaxLength(2000)] string Url,
    [property: Required, MinLength(1)] IReadOnlyList<string> EventTypes,
    [property: MaxLength(200)] string? Secret = null,
    bool IsActive = true);

/// <summary>
/// Request body for updating a subscription. Every field is optional; a field left null is
/// left unchanged.
/// </summary>
/// <param name="Name">New label, or null to leave unchanged.</param>
/// <param name="Url">New endpoint, or null to leave unchanged. Re-validated by the SSRF guard.</param>
/// <param name="EventTypes">Replacement event type list, or null to leave unchanged.</param>
/// <param name="IsActive">New active flag, or null to leave unchanged.</param>
/// <param name="Secret">Replacement secret, or null to leave unchanged.</param>
/// <remarks>
/// PATCH with nullable fields cannot distinguish "absent" from "explicitly null". That is
/// acceptable here because no field on a subscription is legitimately nullable, so "null
/// means leave it alone" loses no expressiveness.
/// </remarks>
public sealed record UpdateSubscriptionRequest(
    [property: MaxLength(200)] string? Name = null,
    [property: MaxLength(2000)] string? Url = null,
    IReadOnlyList<string>? EventTypes = null,
    bool? IsActive = null,
    [property: MaxLength(200)] string? Secret = null);

/// <summary>A subscription as returned by the API, with the secret masked.</summary>
/// <param name="Id">Subscription id.</param>
/// <param name="Name">Human-readable label.</param>
/// <param name="Url">The subscriber endpoint.</param>
/// <param name="EventTypes">Event types this subscription receives.</param>
/// <param name="IsActive">Whether fan-out considers this subscription.</param>
/// <param name="CreatedAt">When it was registered.</param>
/// <param name="SecretPreview">The first few characters of the secret, enough to tell two apart.</param>
public sealed record SubscriptionResponse(
    Guid Id,
    string Name,
    string Url,
    IReadOnlyList<string> EventTypes,
    bool IsActive,
    DateTimeOffset CreatedAt,
    string SecretPreview)
{
    /// <summary>How much of the secret is echoed back after creation.</summary>
    private const int PreviewLength = 12;

    /// <summary>Projects a domain subscription into its API representation.</summary>
    /// <param name="subscription">The subscription to project.</param>
    /// <returns>The response body.</returns>
    public static SubscriptionResponse From(Subscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        return new SubscriptionResponse(
            subscription.Id,
            subscription.Name,
            subscription.Url.ToString(),
            subscription.EventTypes,
            subscription.IsActive,
            subscription.CreatedAt,
            Mask(subscription.Secret));
    }

    private static string Mask(string secret) =>
        secret.Length <= PreviewLength ? secret : string.Concat(secret.AsSpan(0, PreviewLength), "…");
}

/// <summary>
/// The response to a successful registration, carrying the secret in full.
/// </summary>
/// <param name="Subscription">The created subscription.</param>
/// <param name="Secret">
/// The signing secret, in full. This is the only response that contains it — every later read
/// returns the masked preview, so it is worth storing now.
/// </param>
/// <remarks>
/// Authentication is out of scope for this build, so masking on subsequent reads is hygiene
/// rather than a security boundary. It is still worth doing: it keeps secrets out of the
/// dashboard's list view, out of browser history, and out of screenshots.
/// </remarks>
public sealed record CreateSubscriptionResponse(
    [property: JsonPropertyName("subscription")] SubscriptionResponse Subscription,
    [property: JsonPropertyName("secret")] string Secret);

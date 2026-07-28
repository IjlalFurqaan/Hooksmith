using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Hooksmith.Core;
using Hooksmith.Core.Signing;
using Hooksmith.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Hooksmith.Infrastructure.Dispatch;

/// <summary>
/// Sends signed webhook requests to subscribers, following redirects manually so every hop is
/// re-validated by the SSRF guard.
/// </summary>
public sealed class WebhookDispatcher : IWebhookDispatcher
{
    /// <summary>The name of the <see cref="IHttpClientFactory"/> client used for dispatch.</summary>
    public const string HttpClientName = "hooksmith-delivery";

    /// <summary>How long a single attempt may take, including all redirect hops.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOutboundUrlValidator _urlValidator;
    private readonly WebhookSigner _signer;
    private readonly IOptionsMonitor<SsrfGuardOptions> _ssrfOptions;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises the dispatcher.</summary>
    /// <param name="httpClientFactory">Supplies the named delivery client.</param>
    /// <param name="urlValidator">SSRF guard, consulted before the first request and each redirect.</param>
    /// <param name="signer">Computes the signature header.</param>
    /// <param name="ssrfOptions">Guard configuration, read for the redirect budget.</param>
    /// <param name="timeProvider">Clock used to measure attempt duration and resolve <c>Retry-After</c> dates.</param>
    public WebhookDispatcher(
        IHttpClientFactory httpClientFactory,
        IOutboundUrlValidator urlValidator,
        WebhookSigner signer,
        IOptionsMonitor<SsrfGuardOptions> ssrfOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(urlValidator);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(ssrfOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _httpClientFactory = httpClientFactory;
        _urlValidator = urlValidator;
        _signer = signer;
        _ssrfOptions = ssrfOptions;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<DispatchResult> DispatchAsync(
        DispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var started = _timeProvider.GetTimestamp();
        var client = _httpClientFactory.CreateClient(HttpClientName);

        // The signature is computed once. The body does not change across redirect hops, so
        // re-signing per hop would only move the timestamp and buy nothing.
        var signature = _signer.Sign(request.Secret, request.Body).ToHeaderValue();

        try
        {
            return await SendFollowingRedirectsAsync(client, request, signature, started, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient surfaces its own timeout as a cancellation. Distinguishing it from a
            // real shutdown matters: one is the subscriber being slow, the other is us stopping.
            return Failure($"Timed out after {Timeout.TotalSeconds:0.#}s.", started);
        }
        catch (HttpRequestException ex)
        {
            return Failure(ex.Message, started);
        }
    }

    private async Task<DispatchResult> SendFollowingRedirectsAsync(
        HttpClient client,
        DispatchRequest request,
        string signature,
        long started,
        CancellationToken cancellationToken)
    {
        var maxRedirects = Math.Max(0, _ssrfOptions.CurrentValue.MaxRedirects);
        var url = request.Url;

        for (var hop = 0; ; hop++)
        {
            // Re-validated on the first request as well as every redirect. DNS is not a
            // constant, so passing at registration says nothing about this moment.
            var validation = await _urlValidator.ValidateAsync(url, cancellationToken).ConfigureAwait(false);
            if (!validation.IsAllowed)
            {
                return Failure(
                    $"Blocked by the SSRF guard{(hop > 0 ? $" after {hop} redirect(s)" : string.Empty)}: {validation.Detail}",
                    started);
            }

            using var message = BuildRequest(request, url, signature);
            var response = await client
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (TryGetRedirectTarget(response, url, out var next))
                {
                    if (hop >= maxRedirects)
                    {
                        return Failure($"Exceeded the redirect budget of {maxRedirects}.", started);
                    }

                    url = next;
                    continue;
                }

                return await ReadResultAsync(response, started, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                response.Dispose();
            }
        }
    }

    private static HttpRequestMessage BuildRequest(DispatchRequest request, Uri url, string signature)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(request.Body, Encoding.UTF8, "application/json"),
        };

        message.Headers.TryAddWithoutValidation(SignatureHeader.HeaderName, signature);
        message.Headers.TryAddWithoutValidation("X-Hooksmith-Event-Id", request.EventId.ToString());
        message.Headers.TryAddWithoutValidation("X-Hooksmith-Event-Type", request.EventType);
        message.Headers.TryAddWithoutValidation("X-Hooksmith-Delivery-Id", request.DeliveryId.ToString());
        message.Headers.TryAddWithoutValidation(
            "X-Hooksmith-Attempt",
            request.AttemptNumber.ToString(CultureInfo.InvariantCulture));
        message.Headers.UserAgent.Add(new ProductInfoHeaderValue("Hooksmith", "1.0"));

        return message;
    }

    /// <summary>
    /// Determines whether a response is a redirect we should follow, and to where.
    /// </summary>
    /// <remarks>
    /// The method and body are preserved across every hop, including on 301/302/303 where a
    /// browser would switch to GET. A webhook receiver that redirects still wants the payload;
    /// silently converting the POST to a bodyless GET would deliver nothing while reporting
    /// success.
    /// </remarks>
    private static bool TryGetRedirectTarget(HttpResponseMessage response, Uri current, out Uri target)
    {
        target = current;

        if (response.StatusCode is not (HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect))
        {
            return false;
        }

        var location = response.Headers.Location;
        if (location is null)
        {
            return false;
        }

        target = location.IsAbsoluteUri ? location : new Uri(current, location);
        return target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps;
    }

    private async Task<DispatchResult> ReadResultAsync(
        HttpResponseMessage response,
        long started,
        CancellationToken cancellationToken)
    {
        string? body = null;

        try
        {
            body = Attempt.TruncateResponseBody(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            // A subscriber that returns a status and then fails mid-body has still returned a
            // status, and that status is what decides the delivery's fate.
            body = null;
        }

        return new DispatchResult(
            StatusCode: (int)response.StatusCode,
            ResponseBody: body,
            Error: null,
            Duration: _timeProvider.GetElapsedTime(started),
            RetryAfter: ReadRetryAfter(response));
    }

    /// <summary>
    /// Converts a <c>Retry-After</c> header into a delay.
    /// </summary>
    /// <remarks>
    /// The header comes in two forms: delta-seconds, and an HTTP date. Both are handled, and a
    /// date already in the past becomes zero rather than a negative delay.
    /// </remarks>
    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter.Date is { } date)
        {
            var delay = date - _timeProvider.GetUtcNow();
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    /// <summary>Builds a result for an attempt where no usable HTTP response arrived.</summary>
    /// <remarks>The error is capped at the width of the <c>attempts.error</c> column.</remarks>
    private DispatchResult Failure(string error, long started) =>
        new(
            StatusCode: null,
            ResponseBody: null,
            Error: error.Length <= 2000 ? error : error[..2000],
            Duration: _timeProvider.GetElapsedTime(started),
            RetryAfter: null);
}

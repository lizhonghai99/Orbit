using System.Net;
using System.Net.Http.Headers;

namespace Orbit.Contracts;

/// <summary>
/// On <c>401</c>, re-read the local Runtime token once and retry the request
/// (covers Runtime restart that rotates the token while a client stays open).
/// </summary>
public sealed class OrbitTokenRefreshHandler : DelegatingHandler
{
    private static readonly HttpRequestOptionsKey<bool> RetriedKey = new("orbit.token.retry");

    /// <summary>
    /// Skip 401-retry buffering above 1MB (e.g. attachment uploads). Documented trade-off:
    /// large POSTs that hit 401 after a Runtime token rotate will not auto-retry.
    /// </summary>
    private const long MaxRetryBodyBytes = 1 * 1024 * 1024;

    private readonly Func<string?> _readToken;
    private readonly Action<string?> _applyDefaultHeader;

    public OrbitTokenRefreshHandler(Func<string?> readToken, Action<string?> applyDefaultHeader)
    {
        _readToken = readToken ?? throw new ArgumentNullException(nameof(readToken));
        _applyDefaultHeader = applyDefaultHeader ?? throw new ArgumentNullException(nameof(applyDefaultHeader));
    }

    public OrbitTokenRefreshHandler(
        Func<string?> readToken,
        Action<string?> applyDefaultHeader,
        HttpMessageHandler inner)
        : this(readToken, applyDefaultHeader)
    {
        InnerHandler = inner;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Buffer small bodies so a 401 retry can resend POST/PUT content.
        // Large / unknown-size bodies (attachments) skip buffering — no 401 retry.
        byte[]? body = null;
        HttpContentHeaders? contentHeaders = null;
        var canBufferForRetry = request.Content is null
            || (request.Content.Headers.ContentLength is { } len && len <= MaxRetryBodyBytes);

        if (request.Content is not null && canBufferForRetry)
        {
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            contentHeaders = request.Content.Headers;
            request.Content = CreateContent(body, contentHeaders);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized || !canBufferForRetry)
        {
            return response;
        }

        if (request.Options.TryGetValue(RetriedKey, out var already) && already)
        {
            return response;
        }

        var token = _readToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return response;
        }

        response.Dispose();
        // Update DefaultRequestHeaders for subsequent HttpClient calls.
        _applyDefaultHeader(token);

        using var retry = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };
        foreach (var header in request.Headers)
        {
            // Skip auth — set exactly once below (avoid "token,token" if Default + request both attach).
            if (string.Equals(header.Key, OrbitLocalAuth.HeaderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        retry.Options.Set(RetriedKey, true);
        SetTokenHeader(retry.Headers, token);
        if (body is not null)
        {
            retry.Content = CreateContent(body, contentHeaders);
        }

        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private static ByteArrayContent CreateContent(byte[] body, HttpContentHeaders? headers)
    {
        var content = new ByteArrayContent(body);
        if (headers is null)
        {
            return content;
        }

        foreach (var header in headers)
        {
            content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return content;
    }

    private static void SetTokenHeader(HttpRequestHeaders headers, string token)
    {
        headers.Remove(OrbitLocalAuth.HeaderName);
        headers.TryAddWithoutValidation(OrbitLocalAuth.HeaderName, token);
    }
}

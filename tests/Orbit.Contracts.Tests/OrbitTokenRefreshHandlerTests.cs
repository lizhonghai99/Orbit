using System.Net;
using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

public class OrbitTokenRefreshHandlerTests
{
    [Fact]
    public async Task Unauthorized_then_refresh_retries_once_with_new_token()
    {
        string? applied = null;
        var calls = 0;

        string? retryTokenHeader = null;
        var inner = new StubHandler((req, _) =>
        {
            calls++;
            // First call → 401; second (after refresh) → 200
            if (calls == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            if (req.Headers.TryGetValues(OrbitLocalAuth.HeaderName, out var values))
            {
                retryTokenHeader = string.Join(",", values);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}")
            });
        });

        var handler = new OrbitTokenRefreshHandler(
            () => "new-token",
            t => applied = t,
            inner);

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5050/") };
        http.DefaultRequestHeaders.TryAddWithoutValidation(OrbitLocalAuth.HeaderName, "old-token");

        using var response = await http.GetAsync("/agents", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls);
        Assert.Equal("new-token", applied);
        Assert.Equal("new-token", retryTokenHeader);
    }

    [Fact]
    public async Task Second_401_does_not_loop()
    {
        var calls = 0;
        var inner = new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });

        var handler = new OrbitTokenRefreshHandler(
            () => "still-bad",
            _ => { },
            inner);

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5050/") };
        using var response = await http.GetAsync("/agents", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(2, calls);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            impl(request, cancellationToken);
    }
}

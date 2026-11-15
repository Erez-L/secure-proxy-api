using System.Net;
using System.Text;
using SecureProxy.Tests.Support;

namespace SecureProxy.Tests;

/// <summary>Limits, protective behaviour and what is relayed back from the destination.</summary>
public sealed class ResilienceAndResponseTests : IDisposable
{
    private readonly ProxyFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> StartAsync(string apiKey = ProxyFactory.CallerKey)
    {
        var client = _factory.CreateCaller(apiKey);
        await _factory.SeedAsync();
        return client;
    }

    private static object Get(int id = 1) => ProxyFactory.Envelope("customer.get", "crm", new { customerId = id });

    [Fact]
    public async Task Requests_over_the_destination_concurrency_limit_are_rejected_without_forwarding()
    {
        _factory.Settings["Proxy:MaxConcurrentRequestsPerDestination"] = "1";
        var client = await StartAsync();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.Upstream.Responder = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return RecordingUpstream.Json(HttpStatusCode.OK, "{}");
        };

        var first = client.ExecuteAsync(Get(1)).ReadAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await client.ExecuteAsync(Get(2)).ReadAsync();

        Assert.Equal(503, second.Status);
        Assert.Equal("destination_busy", second.Code);
        Assert.Single(_factory.Upstream.Requests);      // only the first request reached the destination

        release.SetResult();
        Assert.Equal(200, (await first).Status);
    }

    [Fact]
    public async Task Circuit_opens_after_repeated_connection_failures_and_stops_calling_the_destination()
    {
        _factory.Settings["Proxy:CircuitBreakerFailureThreshold"] = "2";
        var client = await StartAsync();
        _factory.Upstream.Responder = (_, _) => throw new HttpRequestException("connection refused");

        var first = await client.ExecuteAsync(Get()).ReadAsync();
        var second = await client.ExecuteAsync(Get()).ReadAsync();
        var third = await client.ExecuteAsync(Get()).ReadAsync();

        Assert.Equal(502, first.Status);
        Assert.Equal(502, second.Status);
        Assert.Equal(503, third.Status);
        Assert.Equal("destination_circuit_open", third.Code);
        Assert.Equal(2, _factory.Upstream.Requests.Count);     // the third request was never sent
    }

    [Fact]
    public async Task Upstream_error_responses_do_not_open_the_circuit()
    {
        _factory.Settings["Proxy:CircuitBreakerFailureThreshold"] = "2";
        var client = await StartAsync();
        _factory.Upstream.Responder = (_, _) => Task.FromResult(RecordingUpstream.Json(HttpStatusCode.InternalServerError, "{}"));

        for (var i = 0; i < 5; i++)
            Assert.Equal(500, (await client.ExecuteAsync(Get()).ReadAsync()).Status);

        Assert.Equal(5, _factory.Upstream.Requests.Count);
    }

    [Fact]
    public async Task Caller_over_its_rate_limit_receives_429_and_those_requests_are_not_forwarded()
    {
        _factory.Settings["Proxy:RateLimitPerSecond"] = "3";
        var client = await StartAsync();

        var results = new List<(int Status, string Source, string Code, string Body)>();
        for (var i = 0; i < 20; i++) results.Add(await client.ExecuteAsync(Get()).ReadAsync());

        Assert.Contains(results, r => r.Status == 429 && r.Code == "rate_limited");
        Assert.Equal(results.Count(r => r.Status == 200), _factory.Upstream.Requests.Count);
        Assert.Equal(20, results.Count(r => r.Status is 200 or 429));
    }

    [Fact]
    public async Task Repeated_requests_with_invalid_api_keys_are_throttled()
    {
        _factory.Settings["Proxy:UnauthenticatedRateLimitPerSecond"] = "3";
        await StartAsync();

        var statuses = new List<int>();
        for (var i = 0; i < 20; i++)
        {
            using var guesser = _factory.CreateCaller("guess-" + i);
            statuses.Add((await guesser.ExecuteAsync(Get()).ReadAsync()).Status);
        }

        Assert.Contains(429, statuses);
        Assert.All(statuses, s => Assert.True(s is 401 or 429));
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task Throttling_of_invalid_keys_does_not_affect_a_caller_with_a_valid_key()
    {
        _factory.Settings["Proxy:UnauthenticatedRateLimitPerSecond"] = "1";
        var client = await StartAsync();

        for (var i = 0; i < 10; i++)
        {
            using var guesser = _factory.CreateCaller("guess-" + i);
            await guesser.ExecuteAsync(Get()).ReadAsync();
        }

        Assert.Equal(200, (await client.ExecuteAsync(Get()).ReadAsync()).Status);
    }

    [Fact]
    public async Task Oversized_body_without_content_length_is_rejected_as_too_large()
    {
        var client = await StartAsync();
        var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 70_000))));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/proxy/execute") { Content = content };
        request.Headers.TransferEncodingChunked = true;

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task Safe_upstream_response_headers_are_relayed_and_sensitive_ones_are_not()
    {
        var client = await StartAsync();
        _factory.Upstream.Responder = (_, _) =>
        {
            var response = RecordingUpstream.Json(HttpStatusCode.OK, "{}");
            response.Headers.TryAddWithoutValidation("ETag", "\"v7\"");
            response.Headers.TryAddWithoutValidation("Retry-After", "30");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "upstream-session=secret");
            response.Headers.TryAddWithoutValidation("Server", "internal-crm-01");
            response.Headers.TryAddWithoutValidation("X-Internal-Node", "10.0.0.12");
            return Task.FromResult(response);
        };

        using var response = await client.ExecuteAsync(Get());

        Assert.Equal("\"v7\"", response.Headers.GetValues("ETag").Single());
        Assert.Equal("30", response.Headers.GetValues("Retry-After").Single());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(response.Headers.Contains("Server"));
        Assert.False(response.Headers.Contains("X-Internal-Node"));
    }

    [Theory]
    [InlineData("/customers/1001", true)]
    [InlineData("https://crm.internal.test/api/customers/1001", false)]
    public async Task Location_header_is_relayed_only_when_it_does_not_expose_an_internal_host(string location, bool relayed)
    {
        var client = await StartAsync();
        _factory.Upstream.Responder = (_, _) =>
        {
            var response = RecordingUpstream.Json(HttpStatusCode.Created, "{}");
            response.Headers.TryAddWithoutValidation("Location", location);
            return Task.FromResult(response);
        };

        using var response = await client.ExecuteAsync(Get());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(relayed, response.Headers.Contains("Location"));
        if (relayed) Assert.Equal(location, response.Headers.GetValues("Location").Single());
    }

    [Fact]
    public async Task Destination_without_https_is_not_called_when_https_is_required()
    {
        var client = await StartAsync();

        var result = await client.ExecuteAsync(ProxyFactory.Envelope("plain.get", "plain", new { })).ReadAsync();

        Assert.Equal(500, result.Status);
        Assert.Equal("operation_misconfigured", result.Code);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task Path_parameter_without_a_placeholder_in_the_template_is_not_forwarded()
    {
        var client = await StartAsync();

        var result = await client.ExecuteAsync(ProxyFactory.Envelope("broken.path", "crm", new { id = 5 })).ReadAsync();

        Assert.Equal(500, result.Status);
        Assert.Equal("operation_misconfigured", result.Code);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task Proxy_errors_always_carry_the_correlation_id_header()
    {
        var client = await StartAsync();

        using var response = await client.ExecuteAsync(ProxyFactory.Envelope("customer.unknown", "crm", new { }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(response.Headers.GetValues("X-Correlation-ID").Single()));
    }
}

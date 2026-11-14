using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Data;
using SecureProxy.Tests.Support;

namespace SecureProxy.Tests;

/// <summary>
/// Numbers in test names refer to the required test list in the assignment.
/// Every rejection test asserts that the mock destination recorded nothing:
/// a status code alone is not accepted as proof that a request was blocked.
/// </summary>
public sealed class ProxyExecuteTests : IAsyncLifetime
{
    private readonly ProxyFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateCaller();
        await _factory.SeedAsync();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private void AssertNotForwarded() => Assert.Empty(_factory.Upstream.Requests);

    private static object Get(object parameters) => ProxyFactory.Envelope("customer.get", "crm", parameters);

    // ------------------------------------------------------------------ 1

    [Fact]
    public async Task T01_Valid_configured_operation_is_forwarded()
    {
        _factory.Upstream.Responder = (_, _) =>
            Task.FromResult(RecordingUpstream.Json(HttpStatusCode.OK, """{"id":42,"name":"Dana"}"""));

        var result = await _client.ExecuteAsync(Get(new { customerId = 42, expand = "orders" })).ReadAsync();

        Assert.Equal(200, result.Status);
        Assert.Equal("upstream", result.Source);
        Assert.Equal("""{"id":42,"name":"Dana"}""", result.Body);

        var sent = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("https://crm.internal.test/api/customers/42?expand=orders", sent.Uri.AbsoluteUri);
        Assert.True(sent.Headers.ContainsKey("X-Correlation-ID"));
    }

    // ------------------------------------------------------------------ 2, 3, 4

    [Fact]
    public async Task T02_Unknown_operation_is_rejected()
    {
        var result = await _client.ExecuteAsync(ProxyFactory.Envelope("customer.delete", "crm", new { })).ReadAsync();

        Assert.Equal(404, result.Status);
        Assert.Equal("operation_not_found", result.Code);
        AssertNotForwarded();
    }

    [Fact]
    public async Task T03_Disabled_operation_is_rejected()
    {
        var result = await _client.ExecuteAsync(ProxyFactory.Envelope("customer.disabled", "crm", new { })).ReadAsync();

        Assert.Equal(403, result.Status);
        Assert.Equal("operation_disabled", result.Code);
        AssertNotForwarded();
    }

    [Fact]
    public async Task T04_Disabled_destination_is_rejected()
    {
        var result = await _client.ExecuteAsync(ProxyFactory.Envelope("legacy.get", "legacy", new { })).ReadAsync();

        Assert.Equal(403, result.Status);
        Assert.Equal("destination_unavailable", result.Code);
        AssertNotForwarded();
    }

    // ------------------------------------------------------------------ 5

    [Theory]
    [InlineData("""{"clientId":"customer.get","url":"https://evil.test/steal","json":{"urlId":"crm","parameters":{"customerId":1}}}""")]
    [InlineData("""{"clientId":"customer.get","json":{"urlId":"crm","baseUrl":"https://evil.test","parameters":{"customerId":1}}}""")]
    [InlineData("""{"clientId":"customer.get","json":{"urlId":"crm","path":"/admin","parameters":{"customerId":1}}}""")]
    [InlineData("""{"clientId":"customer.get","json":{"urlId":"https://evil.test","parameters":{"customerId":1}}}""")]
    [InlineData("""{"clientId":"customer.get","json":{"urlId":"legacy","parameters":{"customerId":1}}}""")]
    [InlineData("""{"clientId":"customer.get","json":{"urlId":"crm","parameters":{"customerId":1,"url":"https://evil.test"}}}""")]
    [InlineData("""{"clientId":"file.get","json":{"urlId":"crm","parameters":{"name":"https://evil.test/x"}}}""")]
    [InlineData("""{"clientId":"file.get","json":{"urlId":"crm","parameters":{"name":"evil.test:443"}}}""")]
    [InlineData("""{"clientId":"file.get","json":{"urlId":"crm","parameters":{"name":"user@evil.test"}}}""")]
    public async Task T05_Caller_supplied_destination_cannot_override_configuration(string body)
    {
        var result = await _client.ExecuteRawAsync(body).ReadAsync();

        Assert.Equal(400, result.Status);
        Assert.Equal("proxy", result.Source);
        AssertNotForwarded();
    }

    [Fact]
    public async Task T05b_Forwarded_request_always_targets_the_configured_origin()
    {
        _client.DefaultRequestHeaders.Host = "evil.test";
        _client.DefaultRequestHeaders.Add("X-Forwarded-Host", "evil.test");

        await _client.ExecuteAsync(Get(new { customerId = 7 })).ReadAsync();

        var sent = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal("crm.internal.test", sent.Uri.Host);
        Assert.StartsWith("/api/customers/", sent.Uri.AbsolutePath);
        Assert.False(sent.Headers.ContainsKey("X-Forwarded-Host"));
    }

    // ------------------------------------------------------------------ 6

    [Fact]
    public async Task T06_Caller_supplied_http_method_is_rejected()
    {
        var body = """{"clientId":"customer.get","method":"DELETE","json":{"urlId":"crm","parameters":{"customerId":1}}}""";

        var result = await _client.ExecuteRawAsync(body).ReadAsync();

        Assert.Equal(400, result.Status);
        AssertNotForwarded();
    }

    [Fact]
    public async Task T06b_Operation_configured_with_disallowed_method_is_not_forwarded()
    {
        var result = await _client.ExecuteAsync(ProxyFactory.Envelope("debug.trace", "crm", new { })).ReadAsync();

        Assert.Equal(500, result.Status);
        Assert.Equal("operation_misconfigured", result.Code);
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task T06c_Proxy_endpoint_accepts_only_post(string method)
    {
        using var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/proxy/execute"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        AssertNotForwarded();
    }

    // ------------------------------------------------------------------ 7

    [Fact]
    public async Task T07_Missing_required_parameter_is_rejected()
    {
        var result = await _client.ExecuteAsync(Get(new { expand = "orders" })).ReadAsync();

        Assert.Equal(400, result.Status);
        Assert.Equal("invalid_parameters", result.Code);
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("""{"customerId":"42"}""")]            // wrong type
    [InlineData("""{"customerId":0}""")]               // below range
    [InlineData("""{"customerId":1000000}""")]         // above range
    [InlineData("""{"customerId":1.5}""")]             // not an integer
    [InlineData("""{"customerId":1,"expand":"aaaaaaaaaaaaaaaaaaaaaaaaa"}""")] // too long
    [InlineData("""{"customerId":1,"expand":["a"]}""")]
    [InlineData("""[1]""")]
    public async Task T07b_Invalid_parameter_values_are_rejected(string parameters)
    {
        var body = """{"clientId":"customer.get","json":{"urlId":"crm","parameters":""" + parameters + "}}";

        var result = await _client.ExecuteRawAsync(body).ReadAsync();

        Assert.Equal(400, result.Status);
        Assert.Equal("invalid_parameters", result.Code);
        AssertNotForwarded();
    }

    // ------------------------------------------------------------------ 8

    [Fact]
    public async Task T08_Unknown_parameter_is_rejected_when_operation_does_not_allow_it()
    {
        var result = await _client.ExecuteAsync(Get(new { customerId = 1, debug = true })).ReadAsync();

        Assert.Equal(400, result.Status);
        Assert.Equal("invalid_parameters", result.Code);
        AssertNotForwarded();
    }

    [Fact]
    public async Task T08b_Unknown_parameter_is_forwarded_when_operation_explicitly_allows_it()
    {
        var result = await _client.ExecuteAsync(ProxyFactory.Envelope("customer.search", "crm", new { q = "dana", page = 2 })).ReadAsync();

        Assert.Equal(200, result.Status);
        var sent = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal("?q=dana&page=2", sent.Uri.Query);
    }

    // ------------------------------------------------------------------ 9

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../admin")]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("..\\\\admin")]
    [InlineData("%2e%2e")]
    [InlineData("%2e%2e%2fadmin")]
    [InlineData("..%2f..%2fadmin")]
    [InlineData("report?x=1")]
    [InlineData("report#frag")]
    [InlineData("")]
    public async Task T09_Path_traversal_attempts_are_rejected(string name)
    {
        var body = "{\"clientId\":\"file.get\",\"json\":{\"urlId\":\"crm\",\"parameters\":{\"name\":\"" + name + "\"}}}";

        var result = await _client.ExecuteRawAsync(body).ReadAsync();

        Assert.Equal(400, result.Status);
        Assert.Equal("invalid_parameters", result.Code);
        AssertNotForwarded();
    }

    [Fact]
    public async Task T09b_Legitimate_path_value_is_escaped_and_stays_under_the_base_path()
    {
        await _client.ExecuteAsync(ProxyFactory.Envelope("file.get", "crm", new { name = "report..2024.pdf" })).ReadAsync();

        var sent = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal("/api/files/report..2024.pdf", sent.Uri.AbsolutePath);
    }

    // ------------------------------------------------------------------ 10

    [Fact]
    public async Task T10_Disallowed_headers_are_not_forwarded()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/proxy/execute")
        {
            Content = JsonContent.Create(Get(new { customerId = 5 }))
        };
        request.Headers.Add("X-Tenant", "acme");                       // allowlisted for the operation
        request.Headers.Add("X-Internal-Debug", "1");                  // not allowlisted
        request.Headers.Add("Cookie", "session=secret");               // sensitive
        request.Headers.Add("Authorization", "Bearer caller-token");   // allowlisted in config, still blocked
        request.Headers.Add("Proxy-Authorization", "Basic abc");       // hop-by-hop
        request.Headers.Add("X-Forwarded-For", "10.0.0.1");

        using var response = await _client.SendAsync(request);
        await response.Content.ReadAsStringAsync();

        var sent = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal("acme", sent.Headers["X-Tenant"]);
        foreach (var blocked in new[] { "X-Internal-Debug", "Cookie", "Authorization", "Proxy-Authorization", "X-Forwarded-For", "X-Api-Key" })
            Assert.False(sent.Headers.ContainsKey(blocked), $"{blocked} must not be forwarded");
    }

    // ------------------------------------------------------------------ 11, 12

    [Fact]
    public async Task T11_Invalid_aes_gcm_authentication_tag_is_rejected()
    {
        var envelope = JsonSerializer.SerializeToNode(ProxyFactory.EncryptedEnvelope("customer.create", "crm", new { name = "Dana" }))!;
        var tag = Convert.FromBase64String(envelope["json"]!["encrypted"]!["tag"]!.GetValue<string>());
        tag[0] ^= 0x01;
        envelope["json"]!["encrypted"]!["tag"] = Convert.ToBase64String(tag);

        var result = await _client.ExecuteRawAsync(envelope.ToJsonString()).ReadAsync();

        Assert.Equal(400, result.Status);
        Assert.Equal("invalid_encrypted_payload", result.Code);
        AssertNotForwarded();
    }

    [Fact]
    public async Task T12_Valid_encrypted_payload_is_decrypted_and_forwarded()
    {
        var result = await _client.ExecuteAsync(
            ProxyFactory.EncryptedEnvelope("customer.create", "crm", new { name = "Dana", age = 30 })).ReadAsync();

        Assert.Equal(200, result.Status);
        var sent = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://crm.internal.test/api/customers", sent.Uri.AbsoluteUri);
        Assert.Equal("""{"name":"Dana","age":30}""", sent.Body);
    }

    public static IEnumerable<object[]> InvalidEncryptedPayloads()
    {
        string Mutate(Action<System.Text.Json.Nodes.JsonNode> change, object? source = null)
        {
            var node = JsonSerializer.SerializeToNode(source ?? ProxyFactory.EncryptedEnvelope("customer.create", "crm", new { name = "Dana" }))!;
            change(node["json"]!);
            return node.ToJsonString();
        }

        yield return new object[] { "wrong key", Mutate(_ => { }, ProxyFactory.EncryptedEnvelope("customer.create", "crm", new { name = "Dana" }, key: new byte[32])) };
        yield return new object[] { "bound to another operation", Mutate(_ => { }, ProxyFactory.EncryptedEnvelope("customer.create", "crm", new { name = "Dana" }, aadClientId: "customer.get")) };
        yield return new object[] { "tampered ciphertext", Mutate(j => { var b = Convert.FromBase64String(j["encrypted"]!["ciphertext"]!.GetValue<string>()); b[0] ^= 0xFF; j["encrypted"]!["ciphertext"] = Convert.ToBase64String(b); }) };
        yield return new object[] { "not base64", Mutate(j => j["encrypted"]!["ciphertext"] = "***not-base64***") };
        yield return new object[] { "short nonce", Mutate(j => j["encrypted"]!["nonce"] = Convert.ToBase64String(new byte[8])) };
        yield return new object[] { "short tag", Mutate(j => j["encrypted"]!["tag"] = Convert.ToBase64String(new byte[4])) };
        yield return new object[] { "missing tag", Mutate(j => j["encrypted"]!.AsObject().Remove("tag")) };
        yield return new object[] { "extra field", Mutate(j => j["encrypted"]!["alg"] = "none") };
        yield return new object[] { "plaintext and encrypted together", Mutate(j => j["parameters"] = JsonSerializer.SerializeToNode(new { name = "Dana" })) };
        yield return new object[] { "plaintext instead of encrypted", JsonSerializer.Serialize(ProxyFactory.Envelope("customer.create", "crm", new { name = "Dana" })) };
        yield return new object[] { "decrypts to non-json", EncryptRaw("not json") };
    }

    private static string EncryptRaw(string plaintext)
    {
        var (nonce, ciphertext, tag) = SecureProxy.Api.Security.PayloadCrypto.Encrypt(
            ProxyFactory.EncryptionKey, System.Text.Encoding.UTF8.GetBytes(plaintext),
            SecureProxy.Api.Security.PayloadCrypto.AssociatedData("customer.create", "crm"));
        return JsonSerializer.Serialize(new { clientId = "customer.create", json = new { urlId = "crm", encrypted = new { nonce, ciphertext, tag } } });
    }

    [Theory]
    [MemberData(nameof(InvalidEncryptedPayloads))]
    public async Task T12b_Invalid_encrypted_payloads_are_never_forwarded(string scenario, string body)
    {
        var result = await _client.ExecuteRawAsync(body).ReadAsync();

        Assert.True(result.Status == 400, scenario);
        Assert.Equal("invalid_encrypted_payload", result.Code);
        AssertNotForwarded();
    }

    [Fact]
    public async Task T12c_Decrypted_parameters_are_still_validated()
    {
        var result = await _client.ExecuteAsync(
            ProxyFactory.EncryptedEnvelope("customer.create", "crm", new { name = "Dana", age = 999 })).ReadAsync();

        Assert.Equal(400, result.Status);
        Assert.Equal("invalid_parameters", result.Code);
        AssertNotForwarded();
    }

    [Fact]
    public async Task T12d_Encrypted_payload_is_rejected_by_a_plaintext_operation()
    {
        var result = await _client.ExecuteAsync(
            ProxyFactory.EncryptedEnvelope("customer.get", "crm", new { customerId = 1 })).ReadAsync();

        Assert.Equal(400, result.Status);
        AssertNotForwarded();
    }

    // ------------------------------------------------------------------ 13

    [Fact]
    public async Task T13_Upstream_timeout_returns_gateway_timeout()
    {
        _factory.Upstream.Responder = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);      // destination timeout is 1 second
            return RecordingUpstream.Json(HttpStatusCode.OK, "{}");
        };

        var result = await _client.ExecuteAsync(Get(new { customerId = 1 })).ReadAsync();

        Assert.Equal(504, result.Status);
        Assert.Equal("proxy", result.Source);
        Assert.Equal("upstream_timeout", result.Code);
        Assert.Single(_factory.Upstream.Requests);       // sent exactly once: no retry

        var log = await _factory.WithDbAsync(db => db.RequestLogs.SingleAsync());
        Assert.Equal(ProxyOutcome.UpstreamTimeout, log.Outcome);
        Assert.True(log.Forwarded);
        Assert.Null(log.UpstreamStatusCode);
    }

    // ------------------------------------------------------------------ 14

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task T14_Upstream_http_error_is_preserved_and_marked_as_upstream(HttpStatusCode status)
    {
        _factory.Upstream.Responder = (_, _) =>
            Task.FromResult(RecordingUpstream.Json(status, """{"upstreamError":"details"}"""));

        var result = await _client.ExecuteAsync(Get(new { customerId = 1 })).ReadAsync();

        Assert.Equal((int)status, result.Status);
        Assert.Equal("upstream", result.Source);
        Assert.Equal("""{"upstreamError":"details"}""", result.Body);
        Assert.Single(_factory.Upstream.Requests);       // errors are not retried

        var log = await _factory.WithDbAsync(db => db.RequestLogs.SingleAsync());
        Assert.Equal(ProxyOutcome.UpstreamError, log.Outcome);
        Assert.Equal((int)status, log.UpstreamStatusCode);
    }

    [Fact]
    public async Task T14b_Upstream_connection_failure_returns_bad_gateway_without_internal_details()
    {
        _factory.Upstream.Responder = (_, _) =>
            throw new HttpRequestException("No such host is known. (crm.internal.test:443)");

        var result = await _client.ExecuteAsync(Get(new { customerId = 1 })).ReadAsync();

        Assert.Equal(502, result.Status);
        Assert.Equal("proxy", result.Source);
        Assert.Equal("upstream_unreachable", result.Code);
        Assert.DoesNotContain("crm.internal.test", result.Body);
        Assert.DoesNotContain("No such host", result.Body);
    }

    [Fact]
    public async Task T14c_Oversized_upstream_response_is_not_relayed()
    {
        _factory.Upstream.Responder = (_, _) =>
            Task.FromResult(RecordingUpstream.Json(HttpStatusCode.OK, "\"" + new string('x', 5000) + "\""));   // limit is 1024

        var result = await _client.ExecuteAsync(Get(new { customerId = 1 })).ReadAsync();

        Assert.Equal(502, result.Status);
        Assert.Equal("upstream_response_too_large", result.Code);
    }

    // ------------------------------------------------------------------ 15

    [Fact]
    public async Task T15_Caller_cancellation_is_propagated_to_the_upstream_call()
    {
        var upstreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstreamCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.Upstream.Responder = async (_, ct) =>
        {
            upstreamStarted.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { upstreamCancelled.SetResult(); throw; }
            return RecordingUpstream.Json(HttpStatusCode.OK, "{}");
        };

        // Give the destination a long timeout so that only the caller can end this request.
        await _factory.WithDbAsync(async db =>
        {
            var crm = await db.Destinations.SingleAsync(d => d.Key == "crm");
            crm.TimeoutSeconds = 120;
            return await db.SaveChangesAsync();
        });

        using var callerCts = new CancellationTokenSource();
        var pending = _client.ExecuteAsync(Get(new { customerId = 1 }), callerCts.Token);
        await upstreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        callerCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await upstreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Recorded as a caller cancellation, not as an upstream timeout.
        RequestLog? log = null;
        for (var i = 0; i < 50 && log is null; i++)
        {
            log = await _factory.WithDbAsync(db => db.RequestLogs.SingleOrDefaultAsync());
            if (log is null) await Task.Delay(100);
        }
        Assert.NotNull(log);
        Assert.Equal(ProxyOutcome.CallerCancelled, log!.Outcome);
    }

    // ------------------------------------------------------------------ envelope and limits

    [Fact]
    public async Task Request_without_api_key_is_rejected()
    {
        using var anonymous = _factory.CreateClient();

        var result = await anonymous.ExecuteAsync(Get(new { customerId = 1 })).ReadAsync();

        Assert.Equal(401, result.Status);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Caller_without_permission_for_the_operation_is_rejected()
    {
        using var restricted = _factory.CreateCaller(ProxyFactory.RestrictedKey);

        var result = await restricted.ExecuteAsync(Get(new { customerId = 1 })).ReadAsync();

        Assert.Equal(403, result.Status);
        Assert.Equal("operation_not_permitted", result.Code);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Invalid_content_type_is_rejected()
    {
        var body = JsonSerializer.Serialize(Get(new { customerId = 1 }));

        var result = await _client.ExecuteRawAsync(body, "text/plain").ReadAsync();

        Assert.Equal(415, result.Status);
        Assert.Equal("invalid_content_type", result.Code);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Request_larger_than_the_operation_limit_is_rejected()
    {
        var result = await _client.ExecuteAsync(
            ProxyFactory.Envelope("tiny.post", "crm", new { note = new string('x', 200) })).ReadAsync();

        Assert.Equal(413, result.Status);
        Assert.Equal("request_too_large", result.Code);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Request_larger_than_the_global_limit_is_rejected()
    {
        var result = await _client.ExecuteAsync(
            ProxyFactory.Envelope("customer.search", "crm", new { q = new string('x', 70_000) })).ReadAsync();

        Assert.Equal(413, result.Status);
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"json":{"urlId":"crm","parameters":{}}}""")]
    [InlineData("""{"clientId":"Customer.Get; DROP","json":{"urlId":"crm","parameters":{}}}""")]
    [InlineData("""{"clientId":"customer.get","json":"text"}""")]
    [InlineData("""{"clientId":"customer.get","json":{"parameters":{"customerId":1}}}""")]
    [InlineData("""{"clientId":"customer.get","clientId":"customer.search","json":{"urlId":"crm","parameters":{"customerId":1}}}""")]
    public async Task Malformed_envelopes_are_rejected(string body)
    {
        var result = await _client.ExecuteRawAsync(body).ReadAsync();

        Assert.Equal(400, result.Status);
        Assert.Equal("invalid_request", result.Code);
        AssertNotForwarded();
    }
}

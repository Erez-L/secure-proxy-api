using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Admin;
using SecureProxy.Api.Data;
using SecureProxy.Tests.Support;

namespace SecureProxy.Tests;

/// <summary>Creating and changing configuration through the admin API.</summary>
public sealed class AdminConfigurationTests : IAsyncLifetime
{
    private readonly ProxyFactory _factory = new();
    private HttpClient _caller = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _caller = _factory.CreateCaller();
        _admin = _factory.CreateCaller(ProxyFactory.AdminKey);
        await _factory.SeedAsync();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static object NewDestination(string key = "billing", string baseUrl = "https://billing.internal.test/v1") => new
    {
        key, name = "Billing", baseUrl, timeoutSeconds = 5, maxResponseBytes = 4096, isActive = true
    };

    private static object NewOperation(
        string key = "invoice.get", string destinationKey = "billing", string httpMethod = "GET",
        string pathTemplate = "/invoices/{invoiceId}", object[]? parameters = null, string[]? allowedHeaders = null,
        bool requiresEncryption = false, string? encryptionProfileName = null) => new
    {
        operationKey = key, destinationKey, httpMethod, pathTemplate, isActive = true, maxRequestBytes = 4096,
        requiresEncryption, encryptionProfileName, allowUnknownParameters = false,
        parameters = parameters ?? new object[]
        {
            new { name = "invoiceId", isRequired = true, dataType = "Integer", location = "Path", minValue = 1 }
        },
        allowedHeaders = allowedHeaders ?? Array.Empty<string>()
    };

    [Fact]
    public async Task Destination_and_operation_created_through_the_api_can_be_executed()
    {
        using var createdDestination = await _admin.PostAsJsonAsync("/api/admin/destinations", NewDestination());
        Assert.Equal(HttpStatusCode.Created, createdDestination.StatusCode);

        using var createdOperation = await _admin.PostAsJsonAsync("/api/admin/operations", NewOperation());
        Assert.Equal(HttpStatusCode.Created, createdOperation.StatusCode);
        var view = (await createdOperation.Content.ReadFromJsonAsync<OperationView>())!;
        Assert.Equal("invoice.get", view.OperationKey);
        Assert.Equal("invoiceId", Assert.Single(view.Parameters!).Name);

        var result = await _caller.ExecuteAsync(ProxyFactory.Envelope("invoice.get", "billing", new { invoiceId = 15 })).ReadAsync();

        Assert.Equal(200, result.Status);
        var sent = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal("https://billing.internal.test/v1/invoices/15", sent.Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("Billing Key", "https://billing.internal.test")]         // malformed key
    [InlineData("billing", "http://billing.internal.test")]              // https is required
    [InlineData("billing", "https://user:secret@billing.internal.test")] // credentials in the URL
    [InlineData("billing", "https://billing.internal.test/?x=1")]        // query string
    [InlineData("billing", "ftp://billing.internal.test")]
    [InlineData("billing", "/relative")]
    public async Task Invalid_destination_is_refused_and_not_stored(string key, string baseUrl)
    {
        var result = await _admin.PostAsJsonAsync("/api/admin/destinations", NewDestination(key, baseUrl)).ReadAsync();

        Assert.Equal(400, result.Status);
        Assert.Equal("invalid_configuration", result.Code);
        Assert.False(await _factory.WithDbAsync(db => db.Destinations.AnyAsync(d => d.Name == "Billing")));
    }

    public static IEnumerable<object[]> InvalidOperations()
    {
        yield return new object[] { "disallowed method", NewOperation(httpMethod: "TRACE") };
        yield return new object[] { "malformed key", NewOperation(key: "Invoice Get") };
        yield return new object[] { "unknown destination", NewOperation(destinationKey: "nowhere") };
        yield return new object[] { "template without leading slash", NewOperation(pathTemplate: "invoices/{invoiceId}") };
        yield return new object[] { "traversal in template", NewOperation(pathTemplate: "/../admin/{invoiceId}") };
        yield return new object[] { "query in template", NewOperation(pathTemplate: "/invoices/{invoiceId}?all=1") };
        yield return new object[] { "placeholder without rule", NewOperation(parameters: Array.Empty<object>()) };
        yield return new object[] { "path rule without placeholder", NewOperation(pathTemplate: "/invoices", parameters: new object[] { new { name = "invoiceId", isRequired = true, dataType = "Integer", location = "Path" } }) };
        yield return new object[] { "optional path parameter", NewOperation(parameters: new object[] { new { name = "invoiceId", isRequired = false, dataType = "Integer", location = "Path" } }) };
        yield return new object[] { "body parameter on GET", NewOperation(parameters: new object[] { new { name = "invoiceId", isRequired = true, dataType = "Integer", location = "Path" }, new { name = "note", isRequired = false, dataType = "String", location = "Body" } }) };
        yield return new object[] { "unknown data type", NewOperation(parameters: new object[] { new { name = "invoiceId", isRequired = true, dataType = "Float", location = "Path" } }) };
        yield return new object[] { "numeric enum value", NewOperation(parameters: new object[] { new { name = "invoiceId", isRequired = true, dataType = "1", location = "0" } }) };
        yield return new object[] { "invalid pattern", NewOperation(parameters: new object[] { new { name = "invoiceId", isRequired = true, dataType = "String", location = "Path", pattern = "([a-z" } }) };
        yield return new object[] { "credential header in allowlist", NewOperation(allowedHeaders: new[] { "Authorization" }) };
        yield return new object[] { "hop-by-hop header in allowlist", NewOperation(allowedHeaders: new[] { "Connection" }) };
        yield return new object[] { "encryption without profile", NewOperation(requiresEncryption: true) };
        yield return new object[] { "unknown encryption profile", NewOperation(requiresEncryption: true, encryptionProfileName: "missing") };
    }

    [Theory]
    [MemberData(nameof(InvalidOperations))]
    public async Task Invalid_operation_is_refused_and_not_stored(string scenario, object operation)
    {
        using var destination = await _admin.PostAsJsonAsync("/api/admin/destinations", NewDestination());
        destination.EnsureSuccessStatusCode();

        var result = await _admin.PostAsJsonAsync("/api/admin/operations", operation).ReadAsync();

        Assert.True(result.Status == 400, $"{scenario}: {result.Status} {result.Body}");
        Assert.Equal("invalid_configuration", result.Code);
        Assert.False(await _factory.WithDbAsync(db => db.Operations.AnyAsync(o => o.MaxRequestBytes == 4096)), scenario);
    }

    [Fact]
    public async Task Duplicate_keys_are_refused()
    {
        (await _admin.PostAsJsonAsync("/api/admin/destinations", NewDestination())).EnsureSuccessStatusCode();
        (await _admin.PostAsJsonAsync("/api/admin/operations", NewOperation())).EnsureSuccessStatusCode();

        var destination = await _admin.PostAsJsonAsync("/api/admin/destinations", NewDestination()).ReadAsync();
        var operation = await _admin.PostAsJsonAsync("/api/admin/operations", NewOperation()).ReadAsync();

        Assert.Equal((409, "already_exists"), (destination.Status, destination.Code));
        Assert.Equal((409, "already_exists"), (operation.Status, operation.Code));
    }

    [Fact]
    public async Task Encryption_profile_must_reference_a_key_that_exists_and_never_echoes_the_reference()
    {
        var missing = await _admin.PostAsJsonAsync("/api/admin/encryption-profiles",
            new { name = "billing", keyReference = "no-such-key" }).ReadAsync();
        Assert.Equal(400, missing.Status);

        using var created = await _admin.PostAsJsonAsync("/api/admin/encryption-profiles",
            new { name = "billing", keyReference = ProxyFactory.KeyReference });
        var body = await created.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.DoesNotContain(ProxyFactory.KeyReference, body);
    }

    [Fact]
    public async Task Replacing_rules_requires_the_current_version_and_takes_effect_immediately()
    {
        var current = (await _admin.GetFromJsonAsync<OperationView>("/api/admin/operations/customer.get"))!;
        var stricter = new
        {
            rowVersion = current.RowVersion,
            parameters = new object[]
            {
                new { name = "customerId", isRequired = true, dataType = "Integer", location = "Path", minValue = 1, maxValue = 10 }
            },
            allowedHeaders = new[] { "X-Tenant" }
        };

        using var replaced = await _admin.PutAsJsonAsync("/api/admin/operations/customer.get/rules", stricter);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);

        // The same request again carries a version that is now stale.
        var stale = await _admin.PutAsJsonAsync("/api/admin/operations/customer.get/rules", stricter).ReadAsync();
        Assert.Equal((409, "configuration_conflict"), (stale.Status, stale.Code));

        var outOfRange = await _caller.ExecuteAsync(ProxyFactory.Envelope("customer.get", "crm", new { customerId = 42 })).ReadAsync();
        var removedParameter = await _caller.ExecuteAsync(ProxyFactory.Envelope("customer.get", "crm", new { customerId = 5, expand = "orders" })).ReadAsync();
        var valid = await _caller.ExecuteAsync(ProxyFactory.Envelope("customer.get", "crm", new { customerId = 5 })).ReadAsync();

        Assert.Equal((400, "invalid_parameters"), (outOfRange.Status, outOfRange.Code));
        Assert.Equal((400, "invalid_parameters"), (removedParameter.Status, removedParameter.Code));
        Assert.Equal(200, valid.Status);
        Assert.Single(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task Invalid_rules_are_refused_and_the_existing_rules_stay_in_place()
    {
        var current = (await _admin.GetFromJsonAsync<OperationView>("/api/admin/operations/customer.get"))!;

        var result = await _admin.PutAsJsonAsync("/api/admin/operations/customer.get/rules",
            new { rowVersion = current.RowVersion, parameters = Array.Empty<object>(), allowedHeaders = new[] { "Cookie" } }).ReadAsync();

        Assert.Equal((400, "invalid_configuration"), (result.Status, result.Code));
        var rules = await _factory.WithDbAsync(db => db.ParameterRules.CountAsync(r => r.Name == "customerId"));
        Assert.Equal(1, rules);
    }

    [Fact]
    public async Task Creating_configuration_is_recorded_in_history_and_closed_to_ordinary_callers()
    {
        using var forbidden = await _caller.PostAsJsonAsync("/api/admin/destinations", NewDestination());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        (await _admin.PostAsJsonAsync("/api/admin/destinations", NewDestination())).EnsureSuccessStatusCode();

        var history = await _factory.WithDbAsync(db => db.ConfigurationHistory
            .Where(h => h.EntityType == "Destination" && h.ChangedBy == "test-admin").ToListAsync());
        var added = Assert.Single(history);
        Assert.Equal("Added", added.ChangeType);
        Assert.Contains("billing.internal.test", added.Changes);
    }
}

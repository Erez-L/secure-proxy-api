using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Admin;
using SecureProxy.Api.Data;
using SecureProxy.Tests.Support;

namespace SecureProxy.Tests;

public sealed class ConfigurationAndAuditTests : IAsyncLifetime
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

    // ------------------------------------------------------------------ 16

    [Fact]
    public async Task T16_Stale_configuration_update_returns_conflict_and_changes_nothing()
    {
        var original = (await _admin.GetFromJsonAsync<OperationView>("/api/admin/operations/customer.get"))!;

        // Administrator A updates first.
        using var first = await _admin.PutAsJsonAsync("/api/admin/operations/customer.get",
            new UpdateOperationRequest(true, 2048, null, original.RowVersion));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Administrator B still holds the version read before A's update.
        var second = await _admin.PutAsJsonAsync("/api/admin/operations/customer.get",
            new UpdateOperationRequest(false, 1, null, original.RowVersion)).ReadAsync();

        Assert.Equal(409, second.Status);
        Assert.Equal("configuration_conflict", second.Code);

        var stored = await _factory.WithDbAsync(db => db.Operations.SingleAsync(o => o.OperationKey == "customer.get"));
        Assert.True(stored.IsActive);
        Assert.Equal(2048, stored.MaxRequestBytes);
    }

    [Fact]
    public async Task T16b_Update_with_the_current_version_succeeds_and_takes_effect()
    {
        var current = (await _admin.GetFromJsonAsync<OperationView>("/api/admin/operations/customer.get"))!;

        using var update = await _admin.PutAsJsonAsync("/api/admin/operations/customer.get",
            new UpdateOperationRequest(false, current.MaxRequestBytes, null, current.RowVersion));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var result = await _caller.ExecuteAsync(ProxyFactory.Envelope("customer.get", "crm", new { customerId = 1 })).ReadAsync();
        Assert.Equal(403, result.Status);
        Assert.Equal("operation_disabled", result.Code);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task Configuration_changes_are_recorded_in_history_without_secret_references()
    {
        var current = (await _admin.GetFromJsonAsync<OperationView>("/api/admin/operations/customer.get"))!;
        using var update = await _admin.PutAsJsonAsync("/api/admin/operations/customer.get",
            new UpdateOperationRequest(false, 4096, null, current.RowVersion));
        update.EnsureSuccessStatusCode();

        var history = await _factory.WithDbAsync(db => db.ConfigurationHistory.ToListAsync());

        var change = Assert.Single(history, h => h.EntityType == nameof(Operation) && h.ChangeType == "Modified");
        Assert.Equal("test-admin", change.ChangedBy);
        using var doc = JsonDocument.Parse(change.Changes);
        Assert.Equal("True", doc.RootElement.GetProperty("IsActive").GetProperty("old").GetString());
        Assert.Equal("False", doc.RootElement.GetProperty("IsActive").GetProperty("new").GetString());

        var profileCreated = Assert.Single(history, h => h.EntityType == nameof(EncryptionProfile));
        Assert.DoesNotContain(ProxyFactory.KeyReference, profileCreated.Changes);
    }

    [Fact]
    public async Task Admin_endpoints_are_not_available_to_ordinary_callers()
    {
        using var response = await _caller.GetAsync("/api/admin/operations/customer.get");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------------ 17

    [Fact]
    public async Task T17_Audit_records_are_created_for_successful_and_rejected_requests()
    {
        const string secretValue = "top-secret-payload-value";
        _caller.DefaultRequestHeaders.Add("X-Correlation-ID", "audit-test-0001");

        await _caller.ExecuteAsync(ProxyFactory.Envelope("customer.get", "crm", new { customerId = 9 })).ReadAsync();
        await _caller.ExecuteAsync(ProxyFactory.Envelope("customer.get", "crm", new { customerId = 9, extra = secretValue })).ReadAsync();
        await _caller.ExecuteAsync(ProxyFactory.Envelope("customer.unknown", "crm", new { })).ReadAsync();

        var logs = await _factory.WithDbAsync(db => db.RequestLogs.OrderBy(l => l.Id).ToListAsync());
        Assert.Equal(3, logs.Count);
        Assert.All(logs, l => Assert.Equal("audit-test-0001", l.CorrelationId));
        Assert.All(logs, l => Assert.Equal("test-caller", l.Caller));
        Assert.All(logs, l => Assert.True(l.CompletedAtUtc >= l.StartedAtUtc));

        var success = logs[0];
        Assert.Equal(ProxyOutcome.Success, success.Outcome);
        Assert.True(success.Forwarded);
        Assert.Equal(200, success.UpstreamStatusCode);
        Assert.Equal(200, success.ProxyStatusCode);
        Assert.Equal("customer.get", success.OperationKey);
        Assert.NotNull(success.DestinationId);
        Assert.Null(success.Reason);

        var rejected = logs[1];
        Assert.Equal(ProxyOutcome.Rejected, rejected.Outcome);
        Assert.False(rejected.Forwarded);
        Assert.Null(rejected.UpstreamStatusCode);
        Assert.Equal(400, rejected.ProxyStatusCode);
        Assert.Equal("invalid_parameters", rejected.Reason);

        var unknown = logs[2];
        Assert.Equal(ProxyOutcome.Rejected, unknown.Outcome);
        Assert.False(unknown.Forwarded);
        Assert.Equal("operation_not_found", unknown.Reason);
        Assert.Null(unknown.DestinationId);

        // The audit trail holds metadata only: no payload value appears in any column.
        Assert.DoesNotContain(secretValue, JsonSerializer.Serialize(logs));
        Assert.Single(_factory.Upstream.Requests);
    }
}

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureProxy.Api.Data;
using SecureProxy.Api.Proxy;
using SecureProxy.Api.Security;

namespace SecureProxy.Tests.Support;

/// <summary>
/// Hosts the real application in memory. Only two things are swapped: the database provider
/// (SQLite in-memory instead of SQL Server) and the upstream transport (the recording mock).
/// </summary>
public sealed class ProxyFactory : WebApplicationFactory<Program>
{
    public const string CallerKey = "test-caller-key";
    public const string AdminKey = "test-admin-key";
    public const string RestrictedKey = "test-restricted-key";
    public const string KeyReference = "test-key";
    public const string BaseUrl = "https://crm.internal.test/api";

    public static readonly byte[] EncryptionKey = RandomNumberGenerator.GetBytes(32);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public RecordingUpstream Upstream { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "false");
        builder.UseSetting("Proxy:ConfigCacheSeconds", "0");
        builder.UseSetting("Proxy:RateLimitPerSecond", "10000");
        builder.UseSetting("Proxy:MaxEnvelopeBytes", "65536");
        builder.UseSetting($"EncryptionKeys:{KeyReference}", Convert.ToBase64String(EncryptionKey));

        AddKey(builder, 0, "test-caller", CallerKey, "caller", "*");
        AddKey(builder, 1, "test-admin", AdminKey, "admin", "*");
        AddKey(builder, 2, "test-restricted", RestrictedKey, "caller", "nothing.*");

        builder.ConfigureTestServices(services =>
        {
            var options = services.Single(d => d.ServiceType == typeof(DbContextOptions<ProxyDbContext>));
            services.Remove(options);
            services.AddDbContextFactory<ProxyDbContext>(o => o.UseSqlite(_connection));

            services.AddHttpClient(ProxyExecutor.UpstreamClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Upstream);
        });
    }

    private static void AddKey(IWebHostBuilder builder, int index, string name, string key, string role, string allowed)
    {
        builder.UseSetting($"Auth:ApiKeys:{index}:Name", name);
        builder.UseSetting($"Auth:ApiKeys:{index}:Sha256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
        builder.UseSetting($"Auth:ApiKeys:{index}:Role", role);
        builder.UseSetting($"Auth:ApiKeys:{index}:AllowedOperations:0", allowed);
    }

    public async Task<T> WithDbAsync<T>(Func<ProxyDbContext, Task<T>> action)
    {
        var factory = Services.GetRequiredService<IDbContextFactory<ProxyDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await action(db);
    }

    /// <summary>Creates the schema and the configuration every test starts from.</summary>
    public async Task SeedAsync()
    {
        await WithDbAsync(async db =>
        {
            await db.Database.EnsureCreatedAsync();

            var crm = new Destination { Key = "crm", Name = "CRM", BaseUrl = BaseUrl, TimeoutSeconds = 1, MaxResponseBytes = 1024 };
            var legacy = new Destination { Key = "legacy", Name = "Legacy", BaseUrl = "https://legacy.internal.test", IsActive = false };
            var profile = new EncryptionProfile { Name = "default", KeyReference = KeyReference };

            db.AddRange(crm, legacy, profile,
                new Operation
                {
                    OperationKey = "customer.get", Destination = crm, HttpMethod = "GET", PathTemplate = "/customers/{customerId}",
                    ParameterRules =
                    {
                        new ParameterRule { Name = "customerId", IsRequired = true, DataType = ParameterDataType.Integer, Location = ParameterLocation.Path, MinValue = 1, MaxValue = 999999 },
                        new ParameterRule { Name = "expand", DataType = ParameterDataType.String, Location = ParameterLocation.Query, MaxLength = 20 }
                    },
                    HeaderRules = { new HeaderRule { HeaderName = "X-Tenant" }, new HeaderRule { HeaderName = "Authorization" } }
                },
                new Operation
                {
                    OperationKey = "file.get", Destination = crm, HttpMethod = "GET", PathTemplate = "/files/{name}",
                    ParameterRules = { new ParameterRule { Name = "name", IsRequired = true, DataType = ParameterDataType.String, Location = ParameterLocation.Path, MaxLength = 200 } }
                },
                new Operation
                {
                    OperationKey = "customer.search", Destination = crm, HttpMethod = "GET", PathTemplate = "/customers", AllowUnknownParameters = true,
                    ParameterRules = { new ParameterRule { Name = "q", IsRequired = true, DataType = ParameterDataType.String, Location = ParameterLocation.Query, MaxLength = 50 } }
                },
                new Operation
                {
                    OperationKey = "customer.create", Destination = crm, HttpMethod = "POST", PathTemplate = "/customers",
                    RequiresEncryption = true, EncryptionProfile = profile,
                    ParameterRules =
                    {
                        new ParameterRule { Name = "name", IsRequired = true, DataType = ParameterDataType.String, Location = ParameterLocation.Body, MaxLength = 50 },
                        new ParameterRule { Name = "age", DataType = ParameterDataType.Integer, Location = ParameterLocation.Body, MinValue = 0, MaxValue = 150 }
                    }
                },
                new Operation { OperationKey = "customer.disabled", Destination = crm, HttpMethod = "GET", PathTemplate = "/customers", IsActive = false },
                new Operation { OperationKey = "legacy.get", Destination = legacy, HttpMethod = "GET", PathTemplate = "/items" },
                new Operation { OperationKey = "debug.trace", Destination = crm, HttpMethod = "TRACE", PathTemplate = "/customers" },
                new Operation { OperationKey = "tiny.post", Destination = crm, HttpMethod = "POST", PathTemplate = "/tiny", MaxRequestBytes = 120, AllowUnknownParameters = true });

            await db.SaveChangesAsync();
            return 0;
        });
    }

    public HttpClient CreateCaller(string apiKey = CallerKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, apiKey);
        return client;
    }

    public static object Envelope(string clientId, string urlId, object parameters) =>
        new { clientId, json = new { urlId, parameters } };

    public static object EncryptedEnvelope(string clientId, string urlId, object parameters, byte[]? key = null, string? aadClientId = null)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(parameters);
        var (nonce, ciphertext, tag) = PayloadCrypto.Encrypt(
            key ?? EncryptionKey, plaintext, PayloadCrypto.AssociatedData(aadClientId ?? clientId, urlId));
        return new { clientId, json = new { urlId, encrypted = new { nonce, ciphertext, tag } } };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}

public static class HttpExtensions
{
    public static Task<HttpResponseMessage> ExecuteAsync(this HttpClient client, object envelope, CancellationToken ct = default) =>
        client.PostAsJsonAsync("/api/proxy/execute", envelope, ct);

    public static Task<HttpResponseMessage> ExecuteRawAsync(this HttpClient client, string json, string contentType = "application/json") =>
        client.PostAsync("/api/proxy/execute", new StringContent(json, Encoding.UTF8, contentType));

    /// <summary>Reads the body to the end (so the server pipeline, including audit, has completed).</summary>
    public static async Task<(int Status, string Source, string Code, string Body)> ReadAsync(this Task<HttpResponseMessage> pending)
    {
        using var response = await pending;
        var body = await response.Content.ReadAsStringAsync();
        var source = response.Headers.TryGetValues("X-Proxy-Source", out var values) ? values.First() : "";
        var code = "";
        if (source == "proxy" && body.Length > 0)
        {
            using var doc = JsonDocument.Parse(body);
            code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
        }
        return ((int)response.StatusCode, source, code, body);
    }
}

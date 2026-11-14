using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Data;
using SecureProxy.Api.Security;

namespace SecureProxy.Api.Development;

/// <summary>
/// Development-only helpers so the solution can be exercised on a clean machine without any
/// external service: sample configuration, a tiny in-process "upstream", and a helper that
/// produces an encrypted envelope. None of this is mapped outside the Development environment.
/// </summary>
public static class DevelopmentSupport
{
    public const string DevKeyReference = "dev-crm-key";

    public static async Task SeedAsync(IServiceProvider services, string selfBaseUrl)
    {
        var factory = services.GetRequiredService<IDbContextFactory<ProxyDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        if (await db.Destinations.AnyAsync()) return;

        var destination = new Destination
        {
            Key = "crm-main", Name = "CRM (development mock)", BaseUrl = selfBaseUrl.TrimEnd('/') + "/_mock",
            TimeoutSeconds = 10
        };
        var profile = new EncryptionProfile { Name = "crm-default", KeyReference = DevKeyReference };

        var get = new Operation
        {
            OperationKey = "customer.get", Destination = destination, HttpMethod = "GET", PathTemplate = "/customers/{customerId}",
            ParameterRules =
            {
                new ParameterRule { Name = "customerId", IsRequired = true, DataType = ParameterDataType.Integer, Location = ParameterLocation.Path, MinValue = 1, MaxValue = 999999999 },
                new ParameterRule { Name = "expand", DataType = ParameterDataType.String, Location = ParameterLocation.Query, MaxLength = 20, Pattern = "[a-z]+" }
            },
            HeaderRules = { new HeaderRule { HeaderName = "Accept-Language" } }
        };
        var create = new Operation
        {
            OperationKey = "customer.create", Destination = destination, HttpMethod = "POST", PathTemplate = "/customers",
            RequiresEncryption = true, EncryptionProfile = profile,
            ParameterRules =
            {
                new ParameterRule { Name = "name", IsRequired = true, DataType = ParameterDataType.String, Location = ParameterLocation.Body, MaxLength = 100 },
                new ParameterRule { Name = "age", DataType = ParameterDataType.Integer, Location = ParameterLocation.Body, MinValue = 0, MaxValue = 150 }
            }
        };

        db.AddRange(destination, profile, get, create);
        await db.SaveChangesAsync();
    }

    public static void MapDevelopmentEndpoints(this WebApplication app)
    {
        // In-process stand-in for an upstream API.
        app.MapGet("/_mock/customers/{id:long}", (long id, string? expand) =>
            Results.Ok(new { id, name = "Customer " + id, expand }));
        app.MapPost("/_mock/customers", (JsonElement body) =>
            Results.Created("/_mock/customers/1001", new { id = 1001, received = body }));

        // Produces the "json" member for an operation that requires encryption.
        app.MapPost("/_dev/encrypt", (DevEncryptRequest request, ISecretKeyProvider keys) =>
        {
            var key = keys.GetKey(request.KeyReference ?? DevKeyReference);
            if (key is null) return Results.Problem("Encryption key is not configured. See README.md.");
            var plaintext = Encoding.UTF8.GetBytes(request.Parameters.GetRawText());
            var (nonce, ciphertext, tag) = PayloadCrypto.Encrypt(key, plaintext, PayloadCrypto.AssociatedData(request.ClientId, request.UrlId));
            return Results.Ok(new
            {
                clientId = request.ClientId,
                json = new { urlId = request.UrlId, encrypted = new { nonce, ciphertext, tag } }
            });
        }).RequireAuthorization("Admin");
    }

    public sealed record DevEncryptRequest(string ClientId, string UrlId, JsonElement Parameters, string? KeyReference);
}

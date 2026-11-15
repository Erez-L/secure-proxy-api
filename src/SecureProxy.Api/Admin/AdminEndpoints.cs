using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SecureProxy.Api.Data;
using SecureProxy.Api.Infrastructure;
using SecureProxy.Api.Proxy;
using SecureProxy.Api.Security;

namespace SecureProxy.Api.Admin;

public sealed record OperationView(
    string OperationKey, string DestinationKey, string HttpMethod, string PathTemplate, bool IsActive,
    int MaxRequestBytes, bool RequiresEncryption, bool AllowUnknownParameters, int? TimeoutSeconds, string RowVersion,
    IReadOnlyList<ParameterRuleInput>? Parameters = null, IReadOnlyList<string>? AllowedHeaders = null);

public sealed record CreateOperationRequest(
    string OperationKey, string DestinationKey, string HttpMethod, string PathTemplate, bool IsActive,
    int MaxRequestBytes, bool RequiresEncryption, string? EncryptionProfileName, bool AllowUnknownParameters,
    int? TimeoutSeconds, List<ParameterRuleInput>? Parameters, List<string>? AllowedHeaders);

public sealed record UpdateOperationRequest(bool IsActive, int MaxRequestBytes, int? TimeoutSeconds, string RowVersion);

public sealed record ReplaceRulesRequest(string RowVersion, List<ParameterRuleInput>? Parameters, List<string>? AllowedHeaders);

public sealed record DestinationView(string Key, string Name, bool IsActive, int TimeoutSeconds, long MaxResponseBytes, string RowVersion);

public sealed record CreateDestinationRequest(
    string Key, string Name, string BaseUrl, int TimeoutSeconds, long MaxResponseBytes, string? AllowedPorts, bool IsActive);

public sealed record UpdateDestinationRequest(bool IsActive, int TimeoutSeconds, string RowVersion);

public sealed record EncryptionProfileView(string Name, string Algorithm, bool IsActive);

public sealed record CreateEncryptionProfileRequest(string Name, string KeyReference);

/// <summary>
/// Configuration API (admin role only).
///
/// - Everything written here is validated first (<see cref="ConfigurationValidator"/>), so broken
///   configuration is refused when it is entered rather than discovered on live traffic.
/// - Updates use optimistic concurrency: the caller presents the rowversion it read; a stale
///   version yields 409 and changes nothing.
/// - Every change is recorded in ConfigurationHistory by the DbContext.
/// - There is no delete: an operation or destination is retired by disabling it, which keeps
///   audit records and history meaningful.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").RequireAuthorization("Admin").WithTags("Administration");

        // ------------------------------------------------------------ destinations

        group.MapGet("/destinations/{key}", async (string key, IDbContextFactory<ProxyDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var d = await db.Destinations.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key, ct);
            return d is null ? Results.NotFound() : Results.Ok(ToView(d));
        });

        group.MapPost("/destinations", async (
            CreateDestinationRequest request, HttpContext ctx, IDbContextFactory<ProxyDbContext> factory,
            IOptions<ProxyOptions> options, CancellationToken ct) =>
        {
            var error = ConfigurationValidator.ValidateDestination(
                request.Key, request.Name, request.BaseUrl, request.TimeoutSeconds, request.MaxResponseBytes,
                request.AllowedPorts, options.Value.RequireHttpsDestinations);
            if (error is not null) return await Invalid(ctx, error);

            await using var db = await factory.CreateDbContextAsync(ct);
            db.Actor = Actor(ctx);
            if (await db.Destinations.AnyAsync(d => d.Key == request.Key, ct)) return await Exists(ctx);

            var destination = new Destination
            {
                Key = request.Key, Name = request.Name, BaseUrl = request.BaseUrl, IsActive = request.IsActive,
                TimeoutSeconds = request.TimeoutSeconds, MaxResponseBytes = request.MaxResponseBytes,
                AllowedPorts = string.IsNullOrWhiteSpace(request.AllowedPorts) ? null : request.AllowedPorts
            };
            db.Destinations.Add(destination);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return await Exists(ctx); }

            return Results.Created($"/api/admin/destinations/{destination.Key}", ToView(destination));
        });

        group.MapPut("/destinations/{key}", async (
            string key, UpdateDestinationRequest request, HttpContext ctx,
            IDbContextFactory<ProxyDbContext> factory, IOperationConfigProvider cache, CancellationToken ct) =>
        {
            if (!TryVersion(request.RowVersion, out var version)) return await Invalid(ctx, "rowVersion is required.");
            if (request.TimeoutSeconds is < 1 or > 300) return await Invalid(ctx, "timeoutSeconds must be between 1 and 300.");

            await using var db = await factory.CreateDbContextAsync(ct);
            db.Actor = Actor(ctx);
            var d = await db.Destinations.SingleOrDefaultAsync(x => x.Key == key, ct);
            if (d is null) return Results.NotFound();

            db.Entry(d).Property(x => x.RowVersion).OriginalValue = version;
            d.IsActive = request.IsActive;
            d.TimeoutSeconds = request.TimeoutSeconds;

            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return await Conflict(ctx); }

            cache.InvalidateAll();
            return Results.Ok(ToView(d));
        });

        // ------------------------------------------------------------ encryption profiles

        group.MapPost("/encryption-profiles", async (
            CreateEncryptionProfileRequest request, HttpContext ctx, IDbContextFactory<ProxyDbContext> factory,
            ISecretKeyProvider keys, CancellationToken ct) =>
        {
            if (!ConfigurationValidator.IsValidKey(request.Name)) return await Invalid(ctx, "name is missing or malformed.");
            if (string.IsNullOrWhiteSpace(request.KeyReference) || request.KeyReference.Length > 200)
                return await Invalid(ctx, "keyReference is required (max 200).");

            // Only a reference is stored. It must already resolve to a usable key in the secret source,
            // otherwise every operation using the profile would fail at run time.
            var key = keys.GetKey(request.KeyReference);
            if (key is null) return await Invalid(ctx, "keyReference does not resolve to a 256-bit key in the secret source.");
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);

            await using var db = await factory.CreateDbContextAsync(ct);
            db.Actor = Actor(ctx);
            if (await db.EncryptionProfiles.AnyAsync(p => p.Name == request.Name, ct)) return await Exists(ctx);

            var profile = new EncryptionProfile { Name = request.Name, KeyReference = request.KeyReference };
            db.EncryptionProfiles.Add(profile);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return await Exists(ctx); }

            // The key reference is deliberately not echoed back.
            return Results.Created($"/api/admin/encryption-profiles/{profile.Name}",
                new EncryptionProfileView(profile.Name, profile.Algorithm, profile.IsActive));
        });

        // ------------------------------------------------------------ operations

        group.MapGet("/operations/{key}", async (string key, IDbContextFactory<ProxyDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var op = await db.Operations.AsNoTracking()
                .Include(o => o.Destination).Include(o => o.ParameterRules).Include(o => o.HeaderRules).AsSplitQuery()
                .SingleOrDefaultAsync(o => o.OperationKey == key, ct);
            return op is null ? Results.NotFound() : Results.Ok(ToView(op));
        });

        group.MapPost("/operations", async (
            CreateOperationRequest request, HttpContext ctx, IDbContextFactory<ProxyDbContext> factory, CancellationToken ct) =>
        {
            var rules = request.Parameters ?? new List<ParameterRuleInput>();
            var headers = request.AllowedHeaders ?? new List<string>();

            var error = ConfigurationValidator.ValidateOperation(
                            request.OperationKey, request.HttpMethod, request.PathTemplate, request.MaxRequestBytes,
                            request.TimeoutSeconds, request.RequiresEncryption, !string.IsNullOrEmpty(request.EncryptionProfileName))
                        ?? ConfigurationValidator.ValidateRules(request.HttpMethod, request.PathTemplate, rules, headers);
            if (error is not null) return await Invalid(ctx, error);

            await using var db = await factory.CreateDbContextAsync(ct);
            db.Actor = Actor(ctx);

            var destination = await db.Destinations.SingleOrDefaultAsync(d => d.Key == request.DestinationKey, ct);
            if (destination is null) return await Invalid(ctx, "destinationKey does not name a configured destination.");

            EncryptionProfile? profile = null;
            if (!string.IsNullOrEmpty(request.EncryptionProfileName))
            {
                profile = await db.EncryptionProfiles.SingleOrDefaultAsync(p => p.Name == request.EncryptionProfileName, ct);
                if (profile is null) return await Invalid(ctx, "encryptionProfileName does not name a configured profile.");
            }

            if (await db.Operations.AnyAsync(o => o.OperationKey == request.OperationKey, ct)) return await Exists(ctx);

            var op = new Operation
            {
                OperationKey = request.OperationKey, Destination = destination, HttpMethod = request.HttpMethod,
                PathTemplate = request.PathTemplate, IsActive = request.IsActive, MaxRequestBytes = request.MaxRequestBytes,
                RequiresEncryption = request.RequiresEncryption, EncryptionProfile = profile,
                AllowUnknownParameters = request.AllowUnknownParameters, TimeoutSeconds = request.TimeoutSeconds,
                ParameterRules = rules.Select(ToEntity).ToList(),
                HeaderRules = headers.Select(h => new HeaderRule { HeaderName = h }).ToList()
            };
            db.Operations.Add(op);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return await Exists(ctx); }

            return Results.Created($"/api/admin/operations/{op.OperationKey}", ToView(op));
        });

        group.MapPut("/operations/{key}", async (
            string key, UpdateOperationRequest request, HttpContext ctx,
            IDbContextFactory<ProxyDbContext> factory, IOperationConfigProvider cache, CancellationToken ct) =>
        {
            if (!TryVersion(request.RowVersion, out var version)) return await Invalid(ctx, "rowVersion is required.");
            if (request.MaxRequestBytes < 1) return await Invalid(ctx, "maxRequestBytes must be positive.");
            if (request.TimeoutSeconds is < 1 or > 300) return await Invalid(ctx, "timeoutSeconds must be between 1 and 300.");

            await using var db = await factory.CreateDbContextAsync(ct);
            db.Actor = Actor(ctx);
            var op = await db.Operations
                .Include(o => o.Destination).Include(o => o.ParameterRules).Include(o => o.HeaderRules)
                .SingleOrDefaultAsync(o => o.OperationKey == key, ct);
            if (op is null) return Results.NotFound();

            db.Entry(op).Property(o => o.RowVersion).OriginalValue = version;
            op.IsActive = request.IsActive;
            op.MaxRequestBytes = request.MaxRequestBytes;
            op.TimeoutSeconds = request.TimeoutSeconds;

            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return await Conflict(ctx); }

            cache.InvalidateAll();
            return Results.Ok(ToView(op));
        });

        // Replaces the parameter rules and the header allowlist of an operation as one unit.
        group.MapPut("/operations/{key}/rules", async (
            string key, ReplaceRulesRequest request, HttpContext ctx,
            IDbContextFactory<ProxyDbContext> factory, IOperationConfigProvider cache, CancellationToken ct) =>
        {
            if (!TryVersion(request.RowVersion, out var version)) return await Invalid(ctx, "rowVersion is required.");
            var rules = request.Parameters ?? new List<ParameterRuleInput>();
            var headers = request.AllowedHeaders ?? new List<string>();

            await using var db = await factory.CreateDbContextAsync(ct);
            db.Actor = Actor(ctx);
            var op = await db.Operations
                .Include(o => o.Destination).Include(o => o.ParameterRules).Include(o => o.HeaderRules)
                .SingleOrDefaultAsync(o => o.OperationKey == key, ct);
            if (op is null) return Results.NotFound();

            var error = ConfigurationValidator.ValidateRules(op.HttpMethod, op.PathTemplate, rules, headers);
            if (error is not null) return await Invalid(ctx, error);

            // The rules are child rows, so the operation row itself is marked as modified: that makes
            // the rowversion check apply to this change and gives the operation a new version.
            db.Entry(op).Property(o => o.RowVersion).OriginalValue = version;
            db.Entry(op).Property(o => o.UpdatedAtUtc).IsModified = true;

            db.ParameterRules.RemoveRange(op.ParameterRules);
            db.HeaderRules.RemoveRange(op.HeaderRules);

            // Added through the DbSet on purpose. The new rows already carry a key, and an entity that
            // is only attached through a navigation property with a key set is taken for an existing
            // row, which turns the insert into an update of nothing.
            db.ParameterRules.AddRange(rules.Select(r => { var rule = ToEntity(r); rule.OperationId = op.Id; return rule; }));
            db.HeaderRules.AddRange(headers.Select(h => new HeaderRule { OperationId = op.Id, HeaderName = h }));

            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return await Conflict(ctx); }

            cache.InvalidateAll();
            return Results.Ok(ToView(op));
        });

        // ------------------------------------------------------------ history

        group.MapGet("/history/{entityType}", async (string entityType, IDbContextFactory<ProxyDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await db.ConfigurationHistory.AsNoTracking()
                .Where(h => h.EntityType == entityType)
                .OrderByDescending(h => h.Id).Take(100).ToListAsync(ct);
            return Results.Ok(rows);
        });
    }

    private static string Actor(HttpContext ctx) => ctx.User.Identity?.Name ?? "unknown";

    private static ParameterRule ToEntity(ParameterRuleInput r) => new()
    {
        Name = r.Name, IsRequired = r.IsRequired,
        DataType = Enum.Parse<ParameterDataType>(r.DataType), Location = Enum.Parse<ParameterLocation>(r.Location),
        MaxLength = r.MaxLength, MinValue = r.MinValue, MaxValue = r.MaxValue,
        Pattern = string.IsNullOrEmpty(r.Pattern) ? null : r.Pattern
    };

    private static OperationView ToView(Operation o) => new(
        o.OperationKey, o.Destination?.Key ?? "", o.HttpMethod, o.PathTemplate, o.IsActive, o.MaxRequestBytes,
        o.RequiresEncryption, o.AllowUnknownParameters, o.TimeoutSeconds, Convert.ToBase64String(o.RowVersion),
        o.ParameterRules.Select(p => new ParameterRuleInput(
            p.Name, p.IsRequired, p.DataType.ToString(), p.Location.ToString(), p.MaxLength, p.MinValue, p.MaxValue, p.Pattern)).ToList(),
        o.HeaderRules.Select(h => h.HeaderName).ToList());

    private static DestinationView ToView(Destination d) => new(
        d.Key, d.Name, d.IsActive, d.TimeoutSeconds, d.MaxResponseBytes, Convert.ToBase64String(d.RowVersion));

    private static bool TryVersion(string? value, out byte[] version)
    {
        version = Array.Empty<byte>();
        if (string.IsNullOrEmpty(value)) return false;
        try { version = Convert.FromBase64String(value); return version.Length > 0; }
        catch (FormatException) { return false; }
    }

    private static Task<IResult> Invalid(HttpContext ctx, string message) => Problem(ctx, 400, "invalid_configuration", message);

    private static Task<IResult> Exists(HttpContext ctx) =>
        Problem(ctx, 409, "already_exists", "A configuration entry with this key already exists.");

    private static Task<IResult> Conflict(HttpContext ctx) =>
        Problem(ctx, 409, "configuration_conflict",
            "The configuration was changed by someone else. Reload it and retry the update.");

    private static async Task<IResult> Problem(HttpContext ctx, int status, string code, string message)
    {
        await ProxyErrors.WriteAsync(ctx, status, code, message);
        return Results.Empty;
    }
}

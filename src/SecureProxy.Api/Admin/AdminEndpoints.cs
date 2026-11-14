using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Data;
using SecureProxy.Api.Infrastructure;
using SecureProxy.Api.Proxy;

namespace SecureProxy.Api.Admin;

public sealed record OperationView(
    string OperationKey, string DestinationKey, string HttpMethod, string PathTemplate, bool IsActive,
    int MaxRequestBytes, bool RequiresEncryption, bool AllowUnknownParameters, int? TimeoutSeconds, string RowVersion);

public sealed record UpdateOperationRequest(bool IsActive, int MaxRequestBytes, int? TimeoutSeconds, string RowVersion);

public sealed record DestinationView(string Key, string Name, bool IsActive, int TimeoutSeconds, long MaxResponseBytes, string RowVersion);

public sealed record UpdateDestinationRequest(bool IsActive, int TimeoutSeconds, string RowVersion);

/// <summary>
/// Minimal configuration API. Its purpose in this assessment is to show the concurrency model:
/// every update must present the rowversion it read; a stale version yields 409 and changes nothing.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").RequireAuthorization("Admin");

        group.MapGet("/operations/{key}", async (string key, IDbContextFactory<ProxyDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var op = await db.Operations.AsNoTracking().Include(o => o.Destination).SingleOrDefaultAsync(o => o.OperationKey == key, ct);
            return op is null ? Results.NotFound() : Results.Ok(ToView(op));
        });

        group.MapPut("/operations/{key}", async (
            string key, UpdateOperationRequest request, HttpContext ctx,
            IDbContextFactory<ProxyDbContext> factory, IOperationConfigProvider cache, CancellationToken ct) =>
        {
            if (!TryVersion(request.RowVersion, out var version) || request.MaxRequestBytes <= 0 || request.TimeoutSeconds is <= 0)
                return await Problem(ctx, 400, "invalid_request", "The update request is invalid.");

            await using var db = await factory.CreateDbContextAsync(ct);
            db.Actor = ctx.User.Identity?.Name ?? "unknown";
            var op = await db.Operations.Include(o => o.Destination).SingleOrDefaultAsync(o => o.OperationKey == key, ct);
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

        group.MapGet("/destinations/{key}", async (string key, IDbContextFactory<ProxyDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var d = await db.Destinations.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key, ct);
            return d is null ? Results.NotFound() : Results.Ok(ToView(d));
        });

        group.MapPut("/destinations/{key}", async (
            string key, UpdateDestinationRequest request, HttpContext ctx,
            IDbContextFactory<ProxyDbContext> factory, IOperationConfigProvider cache, CancellationToken ct) =>
        {
            if (!TryVersion(request.RowVersion, out var version) || request.TimeoutSeconds <= 0)
                return await Problem(ctx, 400, "invalid_request", "The update request is invalid.");

            await using var db = await factory.CreateDbContextAsync(ct);
            db.Actor = ctx.User.Identity?.Name ?? "unknown";
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

        group.MapGet("/history/{entityType}", async (string entityType, IDbContextFactory<ProxyDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await db.ConfigurationHistory.AsNoTracking()
                .Where(h => h.EntityType == entityType)
                .OrderByDescending(h => h.Id).Take(100).ToListAsync(ct);
            return Results.Ok(rows);
        });
    }

    private static OperationView ToView(Operation o) => new(
        o.OperationKey, o.Destination?.Key ?? "", o.HttpMethod, o.PathTemplate, o.IsActive, o.MaxRequestBytes,
        o.RequiresEncryption, o.AllowUnknownParameters, o.TimeoutSeconds, Convert.ToBase64String(o.RowVersion));

    private static DestinationView ToView(Destination d) => new(
        d.Key, d.Name, d.IsActive, d.TimeoutSeconds, d.MaxResponseBytes, Convert.ToBase64String(d.RowVersion));

    private static bool TryVersion(string? value, out byte[] version)
    {
        version = Array.Empty<byte>();
        if (string.IsNullOrEmpty(value)) return false;
        try { version = Convert.FromBase64String(value); return version.Length > 0; }
        catch (FormatException) { return false; }
    }

    private static async Task<IResult> Conflict(HttpContext ctx) =>
        await Problem(ctx, 409, "configuration_conflict",
            "The configuration was changed by someone else. Reload it and retry the update.");

    private static async Task<IResult> Problem(HttpContext ctx, int status, string code, string message)
    {
        await ProxyErrors.WriteAsync(ctx, status, code, message);
        return Results.Empty;
    }
}

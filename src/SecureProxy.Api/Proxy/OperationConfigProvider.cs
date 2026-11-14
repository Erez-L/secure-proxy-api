using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using SecureProxy.Api.Data;
using SecureProxy.Api.Infrastructure;

namespace SecureProxy.Api.Proxy;

public sealed class OperationConfigProvider(
    IDbContextFactory<ProxyDbContext> dbFactory, IMemoryCache cache, IOptions<ProxyOptions> options)
    : IOperationConfigProvider
{
    private CancellationTokenSource _generation = new();

    public async Task<OperationSnapshot?> GetAsync(string operationKey, CancellationToken ct)
    {
        var ttl = options.Value.ConfigCacheSeconds;
        var cacheKey = "op:" + operationKey;
        if (ttl > 0 && cache.TryGetValue(cacheKey, out OperationSnapshot? cached)) return cached;

        var generation = Volatile.Read(ref _generation);
        var snapshot = await LoadAsync(operationKey, ct);

        // Unknown keys are not cached: a caller must not be able to fill memory with misses.
        if (ttl > 0 && snapshot is not null)
        {
            cache.Set(cacheKey, snapshot, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttl)
            }.AddExpirationToken(new CancellationChangeToken(generation.Token)));
        }
        return snapshot;
    }

    public void InvalidateAll()
    {
        // The old source is cancelled but intentionally not disposed: a lookup running right now
        // may still read its token. A cancelled token makes that lookup's cache entry expire at once.
        var old = Interlocked.Exchange(ref _generation, new CancellationTokenSource());
        old.Cancel();
    }

    private async Task<OperationSnapshot?> LoadAsync(string operationKey, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var op = await db.Operations.AsNoTracking()
            .Include(o => o.Destination)
            .Include(o => o.EncryptionProfile)
            .Include(o => o.ParameterRules)
            .Include(o => o.HeaderRules)
            .AsSplitQuery()
            .SingleOrDefaultAsync(o => o.OperationKey == operationKey, ct);
        if (op is null) return null;

        DestinationSnapshot? destination = null;
        if (op.Destination is { } d)
        {
            var ports = new HashSet<int>();
            foreach (var part in (d.AllowedPorts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(part, out var port)) ports.Add(port);
            destination = new DestinationSnapshot(d.Id, d.Key, d.BaseUrl, d.IsActive, d.TimeoutSeconds, d.MaxResponseBytes, ports);
        }

        var encryption = op.EncryptionProfile is { } e ? new EncryptionSnapshot(e.Algorithm, e.KeyReference, e.IsActive) : null;

        return new OperationSnapshot(
            op.OperationKey, op.IsActive, op.HttpMethod, op.PathTemplate, op.MaxRequestBytes,
            op.RequiresEncryption, op.AllowUnknownParameters, op.TimeoutSeconds,
            destination, encryption,
            op.ParameterRules.Select(p => new ParameterRuleSnapshot(
                p.Name, p.IsRequired, p.DataType, p.Location, p.MaxLength, p.MinValue, p.MaxValue, p.Pattern)).ToList(),
            op.HeaderRules.Select(h => h.HeaderName).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
}

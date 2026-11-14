using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SecureProxy.Api.Infrastructure;

namespace SecureProxy.Api.Proxy;

/// <summary>
/// Bulkhead: caps in-flight upstream requests per destination so one slow destination cannot
/// absorb every thread/connection of the proxy. Requests over the cap are rejected immediately.
/// </summary>
public sealed class DestinationGate(IOptions<ProxyOptions> options)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public IDisposable? TryEnter(Guid destinationId)
    {
        var limit = Math.Max(1, options.Value.MaxConcurrentRequestsPerDestination);
        var gate = _gates.GetOrAdd(destinationId, _ => new SemaphoreSlim(limit, limit));
        return gate.Wait(0) ? new Releaser(gate) : null;
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}

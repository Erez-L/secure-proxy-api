using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SecureProxy.Api.Infrastructure;

namespace SecureProxy.Api.Proxy;

/// <summary>
/// Circuit breaker per destination. After a configured number of consecutive connection
/// failures or timeouts the destination is not called for a while; requests are rejected at
/// once instead of each waiting for its own timeout. When the pause is over, one request is
/// let through as a probe: if it gets an answer the circuit closes, otherwise it opens again.
///
/// The breaker only ever blocks requests. It never repeats one, so it cannot cause a
/// non-idempotent operation to run twice.
/// </summary>
public sealed class DestinationCircuitBreaker(IOptions<ProxyOptions> options, TimeProvider time)
{
    private sealed class State
    {
        public int ConsecutiveFailures;
        public DateTimeOffset? OpenedAt;
        public bool ProbeInFlight;
    }

    private readonly ConcurrentDictionary<Guid, State> _states = new();

    private int Threshold => options.Value.CircuitBreakerFailureThreshold;

    /// <summary>False when the circuit is open and the request must not be sent.</summary>
    public bool TryEnter(Guid destinationId)
    {
        if (Threshold <= 0) return true;
        var state = _states.GetOrAdd(destinationId, _ => new State());
        lock (state)
        {
            if (state.OpenedAt is not { } openedAt) return true;
            var pause = TimeSpan.FromSeconds(Math.Max(1, options.Value.CircuitBreakerOpenSeconds));
            if (time.GetUtcNow() - openedAt < pause || state.ProbeInFlight) return false;
            state.ProbeInFlight = true;
            return true;
        }
    }

    /// <summary>The destination answered (with any HTTP status).</summary>
    public void RecordSuccess(Guid destinationId)
    {
        if (!_states.TryGetValue(destinationId, out var state)) return;
        lock (state)
        {
            state.ConsecutiveFailures = 0;
            state.OpenedAt = null;
            state.ProbeInFlight = false;
        }
    }

    /// <summary>The destination could not be reached or did not answer in time.</summary>
    public void RecordFailure(Guid destinationId)
    {
        if (Threshold <= 0) return;
        var state = _states.GetOrAdd(destinationId, _ => new State());
        lock (state)
        {
            state.ConsecutiveFailures++;
            if (state.ProbeInFlight || state.ConsecutiveFailures >= Threshold) state.OpenedAt = time.GetUtcNow();
            state.ProbeInFlight = false;
        }
    }

    /// <summary>The attempt ended without a verdict (for example the caller cancelled).</summary>
    public void Release(Guid destinationId)
    {
        if (!_states.TryGetValue(destinationId, out var state)) return;
        lock (state) state.ProbeInFlight = false;
    }
}

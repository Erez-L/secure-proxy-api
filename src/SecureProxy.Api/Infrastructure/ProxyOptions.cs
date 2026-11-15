namespace SecureProxy.Api.Infrastructure;

public sealed class ProxyOptions
{
    public const string Section = "Proxy";

    /// <summary>Hard upper bound for the request envelope, regardless of per-operation limits.</summary>
    public int MaxEnvelopeBytes { get; set; } = 1024 * 1024;

    /// <summary>How long a configuration snapshot is served from memory. 0 disables caching.</summary>
    public int ConfigCacheSeconds { get; set; } = 30;

    /// <summary>Bulkhead: maximum in-flight upstream requests per destination.</summary>
    public int MaxConcurrentRequestsPerDestination { get; set; } = 50;

    /// <summary>Per-caller rate limit (requests per second).</summary>
    public int RateLimitPerSecond { get; set; } = 50;

    /// <summary>
    /// Rate limit per client address for requests that are not authenticated. Slows down API-key
    /// guessing without affecting callers that present a valid key.
    /// </summary>
    public int UnauthenticatedRateLimitPerSecond { get; set; } = 10;

    /// <summary>When true, a destination whose base URL is not https is never called.</summary>
    public bool RequireHttpsDestinations { get; set; } = true;

    /// <summary>
    /// Consecutive connection failures or timeouts after which a destination is not called for
    /// <see cref="CircuitBreakerOpenSeconds"/>. 0 disables the circuit breaker.
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    public int CircuitBreakerOpenSeconds { get; set; } = 30;

    /// <summary>Maximum number of audit records waiting to be written to the database.</summary>
    public int AuditQueueCapacity { get; set; } = 10000;
}

public sealed class ApiKeyEntry
{
    public string Name { get; set; } = "";
    /// <summary>Hex SHA-256 of the API key. The key itself is never stored.</summary>
    public string Sha256 { get; set; } = "";
    /// <summary>"caller" or "admin".</summary>
    public string Role { get; set; } = "caller";
    /// <summary>Operation keys this caller may execute. Supports a trailing "*" wildcard.</summary>
    public List<string> AllowedOperations { get; set; } = new();
}

public sealed class AuthOptions
{
    public const string Section = "Auth";
    public List<ApiKeyEntry> ApiKeys { get; set; } = new();
}

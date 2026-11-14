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

namespace SecureProxy.Api.Data;

/// <summary>Entities that carry an optimistic-concurrency token (SQL Server rowversion).</summary>
public interface IVersioned
{
    byte[] RowVersion { get; set; }
}

/// <summary>Entities whose changes are written to <see cref="ConfigurationHistory"/>.</summary>
public interface IConfigurationEntity
{
    Guid Id { get; }
}

/// <summary>A configured upstream API. Callers reference it by <see cref="Key"/> (the "urlId").</summary>
public class Destination : IVersioned, IConfigurationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 30;
    public long MaxResponseBytes { get; set; } = 10 * 1024 * 1024;
    /// <summary>Optional comma separated list of permitted ports. Empty = the port of BaseUrl only.</summary>
    public string? AllowedPorts { get; set; }
    /// <summary>Optional reference (name only) to an upstream authentication profile held in the secret store.</summary>
    public string? AuthProfileReference { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public List<Operation> Operations { get; set; } = new();
}

/// <summary>An explicitly allowed action. Method, path and destination always come from here.</summary>
public class Operation : IVersioned, IConfigurationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OperationKey { get; set; } = "";
    public Guid DestinationId { get; set; }
    public Destination? Destination { get; set; }
    public string HttpMethod { get; set; } = "GET";
    public string PathTemplate { get; set; } = "/";
    public bool IsActive { get; set; } = true;
    public int MaxRequestBytes { get; set; } = 64 * 1024;
    public bool RequiresEncryption { get; set; }
    public Guid? EncryptionProfileId { get; set; }
    public EncryptionProfile? EncryptionProfile { get; set; }
    public bool AllowUnknownParameters { get; set; }
    /// <summary>Optional override of the destination timeout.</summary>
    public int? TimeoutSeconds { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public List<ParameterRule> ParameterRules { get; set; } = new();
    public List<HeaderRule> HeaderRules { get; set; } = new();
}

public enum ParameterLocation { Path = 0, Query = 1, Body = 2 }

public enum ParameterDataType { String = 0, Integer = 1, Decimal = 2, Boolean = 3, Guid = 4 }

public class ParameterRule : IConfigurationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public string Name { get; set; } = "";
    public bool IsRequired { get; set; }
    public ParameterDataType DataType { get; set; }
    public ParameterLocation Location { get; set; }
    public int? MaxLength { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    /// <summary>Optional regular expression a string value must fully match.</summary>
    public string? Pattern { get; set; }
}

/// <summary>One allowlisted incoming header that may be forwarded for an operation.</summary>
public class HeaderRule : IConfigurationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public string HeaderName { get; set; } = "";
}

/// <summary>Metadata only. The key material lives in the secret store under <see cref="KeyReference"/>.</summary>
public class EncryptionProfile : IConfigurationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Algorithm { get; set; } = "AES-256-GCM";
    public string KeyReference { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum ProxyOutcome
{
    Success = 0,
    UpstreamError = 1,
    Rejected = 2,
    UpstreamTimeout = 3,
    UpstreamUnreachable = 4,
    CallerCancelled = 5,
    ProxyError = 6
}

/// <summary>Audit record of one execution attempt. Never contains payloads, headers or secrets.</summary>
public class RequestLog
{
    public long Id { get; set; }
    public string CorrelationId { get; set; } = "";
    public string? Caller { get; set; }
    public string? OperationKey { get; set; }
    public Guid? DestinationId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public long DurationMs { get; set; }
    public bool Forwarded { get; set; }
    public int? UpstreamStatusCode { get; set; }
    public int ProxyStatusCode { get; set; }
    public ProxyOutcome Outcome { get; set; }
    public string? Reason { get; set; }
}

public class ConfigurationHistory
{
    public long Id { get; set; }
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string ChangeType { get; set; } = "";
    public DateTime ChangedAtUtc { get; set; }
    public string ChangedBy { get; set; } = "";
    /// <summary>JSON: property -> { old, new }. Sensitive references are redacted.</summary>
    public string Changes { get; set; } = "{}";
}

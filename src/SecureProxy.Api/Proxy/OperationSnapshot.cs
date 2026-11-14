using SecureProxy.Api.Data;

namespace SecureProxy.Api.Proxy;

public sealed record DestinationSnapshot(
    Guid Id, string Key, string BaseUrl, bool IsActive, int TimeoutSeconds, long MaxResponseBytes, IReadOnlySet<int> AllowedPorts);

public sealed record EncryptionSnapshot(string Algorithm, string KeyReference, bool IsActive);

public sealed record ParameterRuleSnapshot(
    string Name, bool IsRequired, ParameterDataType DataType, ParameterLocation Location,
    int? MaxLength, decimal? MinValue, decimal? MaxValue, string? Pattern);

/// <summary>
/// Immutable view of one operation and everything needed to execute it. A request works on the
/// snapshot it started with, so configuration changes never affect a request half-way through.
/// </summary>
public sealed record OperationSnapshot(
    string Key, bool IsActive, string HttpMethod, string PathTemplate, int MaxRequestBytes,
    bool RequiresEncryption, bool AllowUnknownParameters, int? TimeoutSeconds,
    DestinationSnapshot? Destination, EncryptionSnapshot? Encryption,
    IReadOnlyList<ParameterRuleSnapshot> Parameters, IReadOnlySet<string> AllowedHeaders);

public interface IOperationConfigProvider
{
    Task<OperationSnapshot?> GetAsync(string operationKey, CancellationToken ct);
    /// <summary>Drops every cached snapshot on this instance.</summary>
    void InvalidateAll();
}

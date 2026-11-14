using SecureProxy.Api.Data;

namespace SecureProxy.Api.Audit;

public interface IAuditWriter
{
    Task WriteAsync(RequestLog record);
}

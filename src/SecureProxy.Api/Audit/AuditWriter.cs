using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Data;

namespace SecureProxy.Api.Audit;

/// <summary>
/// Audit policy: every execution attempt that reaches the proxy endpoint (success, rejection or
/// failure) produces one record. The record is ALWAYS emitted as a structured log event and is
/// persisted to SQL Server on a best-effort basis: if the database is unavailable the request
/// outcome is not changed, and the failure itself is logged so the gap can be reconciled from logs.
/// See DESIGN-NOTES.md, "Audit policy".
/// </summary>
public sealed class AuditWriter(IDbContextFactory<ProxyDbContext> dbFactory, ILogger<AuditWriter> logger) : IAuditWriter
{
    private static readonly TimeSpan PersistTimeout = TimeSpan.FromSeconds(5);

    public async Task WriteAsync(RequestLog r)
    {
        logger.LogInformation(
            "ProxyAudit {CorrelationId} caller={Caller} operation={OperationKey} destination={DestinationId} " +
            "forwarded={Forwarded} upstreamStatus={UpstreamStatusCode} proxyStatus={ProxyStatusCode} " +
            "outcome={Outcome} reason={Reason} durationMs={DurationMs}",
            r.CorrelationId, r.Caller, r.OperationKey, r.DestinationId, r.Forwarded, r.UpstreamStatusCode,
            r.ProxyStatusCode, r.Outcome, r.Reason, r.DurationMs);

        try
        {
            // Deliberately not tied to the request token: a caller that disconnects must still be audited.
            using var cts = new CancellationTokenSource(PersistTimeout);
            await using var db = await dbFactory.CreateDbContextAsync(cts.Token);
            db.RequestLogs.Add(r);
            await db.SaveChangesAsync(cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ProxyAuditPersistFailed {CorrelationId}: audit record was logged but not stored.", r.CorrelationId);
        }
    }
}

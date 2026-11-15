using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SecureProxy.Api.Data;
using SecureProxy.Api.Infrastructure;

namespace SecureProxy.Api.Audit;

/// <summary>
/// Audit policy: every execution attempt that reaches the proxy endpoint (success, rejection or
/// failure) produces one record. The record is ALWAYS emitted as a structured log event first.
/// It is then placed on a bounded in-memory queue and written to SQL Server by a background
/// worker, so a slow or unavailable database never delays a caller.
///
/// Persistence is buffered and best-effort: if the database write fails, or the queue is full,
/// the record is not stored and that fact is logged, so the gap can be reconciled from the logs.
/// See DESIGN-NOTES.md, "Audit policy".
/// </summary>
public sealed class AuditQueue : BackgroundService, IAuditWriter
{
    private const int BatchSize = 100;
    private static readonly TimeSpan PersistTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<RequestLog> _channel;
    private readonly IDbContextFactory<ProxyDbContext> _dbFactory;
    private readonly ILogger<AuditQueue> _logger;
    private int _pending;

    public AuditQueue(IDbContextFactory<ProxyDbContext> dbFactory, IOptions<ProxyOptions> options, ILogger<AuditQueue> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _channel = Channel.CreateBounded<RequestLog>(new BoundedChannelOptions(Math.Max(1, options.Value.AuditQueueCapacity))
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait      // TryWrite returns false when the queue is full
        });
    }

    public Task WriteAsync(RequestLog r)
    {
        _logger.LogInformation(
            "ProxyAudit {CorrelationId} caller={Caller} operation={OperationKey} destination={DestinationId} " +
            "forwarded={Forwarded} upstreamStatus={UpstreamStatusCode} proxyStatus={ProxyStatusCode} " +
            "outcome={Outcome} reason={Reason} durationMs={DurationMs}",
            r.CorrelationId, r.Caller, r.OperationKey, r.DestinationId, r.Forwarded, r.UpstreamStatusCode,
            r.ProxyStatusCode, r.Outcome, r.Reason, r.DurationMs);

        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(r))
        {
            Interlocked.Decrement(ref _pending);
            _logger.LogError("ProxyAuditDropped {CorrelationId}: audit queue is full or closed; record was logged but not stored.", r.CorrelationId);
        }
        return Task.CompletedTask;
    }

    /// <summary>Waits until every queued record has been handled. Used by tests and at shutdown.</summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (Volatile.Read(ref _pending) > 0)
            await Task.Delay(10, cts.Token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<RequestLog>(BatchSize);

        // Deliberately not tied to stoppingToken: on shutdown StopAsync closes the queue and this
        // loop ends by itself once the remaining records have been written.
        while (await _channel.Reader.WaitToReadAsync(CancellationToken.None))
        {
            batch.Clear();
            while (batch.Count < BatchSize && _channel.Reader.TryRead(out var item)) batch.Add(item);

            await PersistAsync(batch);
            Interlocked.Add(ref _pending, -batch.Count);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        if (ExecuteTask is { } running)
            await Task.WhenAny(running, Task.Delay(Timeout.Infinite, cancellationToken));
        await base.StopAsync(cancellationToken);
    }

    private async Task PersistAsync(List<RequestLog> batch)
    {
        try
        {
            using var cts = new CancellationTokenSource(PersistTimeout);
            await using var db = await _dbFactory.CreateDbContextAsync(cts.Token);
            db.RequestLogs.AddRange(batch);
            await db.SaveChangesAsync(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProxyAuditPersistFailed: {Count} audit record(s) were logged but not stored. First: {CorrelationId}",
                batch.Count, batch[0].CorrelationId);
        }
    }
}

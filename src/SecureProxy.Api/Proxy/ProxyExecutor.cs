using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SecureProxy.Api.Audit;
using SecureProxy.Api.Data;
using SecureProxy.Api.Infrastructure;
using SecureProxy.Api.Security;

namespace SecureProxy.Api.Proxy;

/// <summary>
/// The whole request pipeline for POST /api/proxy/execute. Order matters and is fail-closed:
/// nothing is sent upstream until every step before <see cref="ForwardAsync"/> has succeeded.
///
///   1. envelope (content type, size, strict JSON shape)
///   2. operation lookup by clientId, caller permission, enabled checks
///   3. urlId must name the destination configured for the operation
///   4. per-operation size limit
///   5. decryption (AES-GCM) when the operation requires it
///   6. parameter validation
///   7. upstream request built from trusted configuration only
///   8. forward, relay the response as a stream
///   9. audit record (always)
/// </summary>
public sealed partial class ProxyExecutor(
    IOperationConfigProvider config,
    ISecretKeyProvider keys,
    IHttpClientFactory httpClientFactory,
    IAuditWriter audit,
    DestinationGate gate,
    IOptions<ProxyOptions> options,
    ILogger<ProxyExecutor> logger)
{
    public const string UpstreamClientName = "upstream";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,99}$")]
    private static partial Regex KeyFormat();

    private static readonly JsonDocumentOptions StrictJson = new()
    {
        MaxDepth = 16, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow
    };

    public async Task ExecuteAsync(HttpContext ctx)
    {
        var record = new RequestLog
        {
            CorrelationId = Correlation.Get(ctx),
            Caller = ctx.User.Identity?.Name,
            StartedAtUtc = DateTime.UtcNow,
            Outcome = ProxyOutcome.ProxyError
        };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await RunAsync(ctx, record);
        }
        catch (ProxyException ex)
        {
            record.Outcome = ex.Outcome;
            record.Reason = ex.Code;
            record.ProxyStatusCode = ex.Status;
            await FailAsync(ctx, ex.Status, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled proxy failure");
            record.Outcome = ProxyOutcome.ProxyError;
            record.Reason = "internal_error";
            record.ProxyStatusCode = 500;
            await FailAsync(ctx, 500, "internal_error", "The proxy could not process the request.");
        }
        finally
        {
            record.CompletedAtUtc = DateTime.UtcNow;
            record.DurationMs = stopwatch.ElapsedMilliseconds;
            await audit.WriteAsync(record);
        }
    }

    private static async Task FailAsync(HttpContext ctx, int status, string code, string message)
    {
        if (ctx.RequestAborted.IsCancellationRequested) return;      // nobody is listening
        if (ctx.Response.HasStarted) { ctx.Abort(); return; }        // cannot change a response in flight
        await ProxyErrors.WriteAsync(ctx, status, code, message);
    }

    private async Task RunAsync(HttpContext ctx, RequestLog record)
    {
        var ct = ctx.RequestAborted;

        // 1. Envelope
        var body = await ReadEnvelopeAsync(ctx.Request, options.Value.MaxEnvelopeBytes, ct);
        using var document = ParseEnvelope(body);
        var root = document.RootElement;
        var (clientId, json) = ReadEnvelope(root);
        record.OperationKey = clientId;          // safe to log: it matched the strict key format

        // 2. Operation
        OperationSnapshot? op;
        try { op = await config.GetAsync(clientId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw ProxyErrors.CallerCancelled(); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Configuration lookup failed");
            throw ProxyErrors.ConfigurationUnavailable();
        }

        if (op is null) throw ProxyErrors.UnknownOperation();
        if (!CallerPermissions.MayExecute(ctx.User, op.Key)) throw ProxyErrors.OperationNotPermitted();
        if (!op.IsActive) throw ProxyErrors.OperationDisabled();

        var destination = op.Destination;
        record.DestinationId = destination?.Id;
        if (destination is null || !destination.IsActive) throw ProxyErrors.DestinationUnavailable();

        // 3. urlId is a confirmation of the configured destination, never a way to choose one.
        var urlId = ReadString(json, "urlId") ?? throw ProxyErrors.InvalidEnvelope("json.urlId is required.");
        if (!string.Equals(urlId, destination.Key, StringComparison.Ordinal)) throw ProxyErrors.DestinationMismatch();

        // 4. Per-operation size limit
        if (body.Length > op.MaxRequestBytes) throw ProxyErrors.TooLarge();

        // 5. Encryption
        using var decrypted = ResolveParameters(op, clientId, urlId, json, out var parametersElement);

        // 6. Parameters
        var parameters = ParameterValidator.Validate(op, parametersElement);

        // 7. Upstream request, from trusted configuration only
        using var request = UpstreamRequestBuilder.Build(op, destination, parameters, ctx.Request.Headers, record.CorrelationId);

        // 8. Forward
        await ForwardAsync(ctx, record, op, destination, request);
    }

    private async Task ForwardAsync(
        HttpContext ctx, RequestLog record, OperationSnapshot op, DestinationSnapshot destination, HttpRequestMessage request)
    {
        using var slot = gate.TryEnter(destination.Id) ?? throw ProxyErrors.DestinationBusy();

        var timeout = TimeSpan.FromSeconds(Math.Max(1, op.TimeoutSeconds ?? destination.TimeoutSeconds));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        cts.CancelAfter(timeout);

        var client = httpClientFactory.CreateClient(UpstreamClientName);
        try
        {
            record.Forwarded = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            record.UpstreamStatusCode = (int)response.StatusCode;

            if (response.Content.Headers.ContentLength is { } length && length > destination.MaxResponseBytes)
                throw ProxyErrors.UpstreamResponseTooLarge();

            // The upstream answered: relay its status and body as-is and mark the response so the
            // caller can tell an upstream error from a proxy error.
            ctx.Response.StatusCode = (int)response.StatusCode;
            ctx.Response.Headers[ProxyErrors.SourceHeader] = "upstream";
            if (response.Content.Headers.ContentType is { } contentType)
                ctx.Response.ContentType = contentType.ToString();

            await RelayBodyAsync(response, ctx.Response, destination.MaxResponseBytes, cts.Token);

            record.ProxyStatusCode = (int)response.StatusCode;
            record.Outcome = response.IsSuccessStatusCode ? ProxyOutcome.Success : ProxyOutcome.UpstreamError;
            record.Reason = response.IsSuccessStatusCode ? null : "upstream_http_error";
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            throw ProxyErrors.CallerCancelled();
        }
        catch (OperationCanceledException)
        {
            throw ProxyErrors.UpstreamTimeout();
        }
        catch (HttpRequestException ex)
        {
            // Details (host, socket error) go to the log only, never to the caller.
            logger.LogWarning(ex, "Upstream connection failure for {OperationKey}", op.Key);
            throw ProxyErrors.UpstreamUnreachable();
        }
    }

    /// <summary>Streams the upstream body to the caller with a fixed buffer and a hard byte limit.</summary>
    private static async Task RelayBodyAsync(HttpResponseMessage response, HttpResponse target, long maxBytes, CancellationToken ct)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > maxBytes) throw ProxyErrors.UpstreamResponseTooLarge();
                await target.Body.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---------------------------------------------------------------- envelope

    private static async Task<byte[]> ReadEnvelopeAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
            !string.Equals(mediaType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw ProxyErrors.UnsupportedMediaType();

        if (request.ContentLength > maxBytes) throw ProxyErrors.TooLarge();

        // Bounded read: at most maxBytes + 1 bytes are ever buffered, whatever the caller sends.
        using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes) throw ProxyErrors.TooLarge();
                buffer.Write(chunk, 0, read);
            }
        }
        catch (OperationCanceledException)
        {
            throw ProxyErrors.CallerCancelled();
        }
        return buffer.ToArray();
    }

    private static JsonDocument ParseEnvelope(byte[] body)
    {
        try { return JsonDocument.Parse(body, StrictJson); }
        catch (JsonException) { throw ProxyErrors.InvalidEnvelope("The request body is not valid JSON."); }
    }

    /// <summary>
    /// Strict shape: exactly { clientId, json }. Any other member (url, method, path, headers ...)
    /// is rejected rather than ignored, so a caller cannot even attempt to supply routing data.
    /// </summary>
    private static (string ClientId, JsonElement Json) ReadEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw ProxyErrors.InvalidEnvelope("The request body must be a JSON object.");
        EnsureOnly(root, "clientId", "json");

        var clientId = ReadString(root, "clientId");
        if (clientId is null || !KeyFormat().IsMatch(clientId)) throw ProxyErrors.InvalidEnvelope("clientId is missing or malformed.");

        if (!root.TryGetProperty("json", out var json) || json.ValueKind != JsonValueKind.Object)
            throw ProxyErrors.InvalidEnvelope("json is required and must be an object.");
        EnsureOnly(json, "urlId", "parameters", "encrypted");

        return (clientId, json);
    }

    private static void EnsureOnly(JsonElement obj, params string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0) throw ProxyErrors.InvalidEnvelope("The request contains an unsupported field.");
            if (!seen.Add(property.Name)) throw ProxyErrors.InvalidEnvelope("The request contains a duplicated field.");
        }
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // ---------------------------------------------------------------- encryption

    /// <summary>
    /// Returns the parameters object: taken directly from json.parameters for plaintext operations,
    /// or decrypted from json.encrypted when the operation requires encryption. The two forms are
    /// never interchangeable. Every failure maps to the same public error, so the response does not
    /// reveal which part (key, nonce, tag, format) was wrong.
    /// </summary>
    private JsonDocument? ResolveParameters(
        OperationSnapshot op, string clientId, string urlId, JsonElement json, out JsonElement parameters)
    {
        var hasPlain = json.TryGetProperty("parameters", out var plain);
        var hasEncrypted = json.TryGetProperty("encrypted", out var encrypted);

        if (!op.RequiresEncryption)
        {
            if (hasEncrypted) throw ProxyErrors.InvalidEnvelope("This operation does not accept an encrypted payload.");
            if (!hasPlain) throw ProxyErrors.InvalidEnvelope("json.parameters is required.");
            parameters = plain;
            return null;
        }

        if (hasPlain || !hasEncrypted || encrypted.ValueKind != JsonValueKind.Object)
            throw ProxyErrors.InvalidEncryptedPayload();

        var profile = op.Encryption;
        if (profile is null || !profile.IsActive || profile.Algorithm != "AES-256-GCM") throw ProxyErrors.Misconfigured();
        var key = keys.GetKey(profile.KeyReference);
        if (key is null)
        {
            logger.LogError("Encryption key reference for {OperationKey} could not be resolved", op.Key);
            throw ProxyErrors.Misconfigured();
        }

        try
        {
            EnsureOnly(encrypted, "nonce", "ciphertext", "tag");
            var nonce = ReadString(encrypted, "nonce");
            var ciphertext = ReadString(encrypted, "ciphertext");
            var tag = ReadString(encrypted, "tag");
            if (nonce is null || ciphertext is null || tag is null) throw ProxyErrors.InvalidEncryptedPayload();

            var plaintext = PayloadCrypto.Decrypt(key, nonce, ciphertext, tag, PayloadCrypto.AssociatedData(clientId, urlId));
            // JsonDocument keeps a reference to this buffer (no copy), so it must stay intact
            // until the document is disposed at the end of the request.
            var document = JsonDocument.Parse(plaintext, StrictJson);
            parameters = document.RootElement;
            return document;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ProxyException)
        {
            throw ProxyErrors.InvalidEncryptedPayload();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}

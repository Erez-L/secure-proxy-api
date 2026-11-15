# Design notes

## 1. Assumptions

The assignment text leaves several points open. These are the readings this solution implements.

| Open point | Decision |
|------------|----------|
| No starting repository was supplied. | The solution is built from scratch. See DEFECT-REPORT.md. |
| The `json` member of the example request is unspecified. | `json` is an object: `urlId` plus either `parameters` (plaintext) or `encrypted` (AES-GCM envelope). |
| "Json must contain the urlId key", while the caller must not choose the destination. | `urlId` is the key of a configured destination. It is a *confirmation*: it must equal the destination configured for the operation, otherwise the request is rejected. It can never select or change a destination. |
| The encryption format is unspecified (tests mention AES-GCM). | AES-256-GCM, 12-byte nonce, 16-byte tag, base64 fields. Request payloads are encrypted by the caller and decrypted by the proxy, which validates the plaintext before forwarding. Associated data binds a ciphertext to `clientId` and `urlId`. |
| Caller authentication is not specified. | API key per caller (header `X-Api-Key`), with a per-key list of permitted operations. The mechanism is isolated in one authentication handler and can be replaced by JWT or mTLS without touching the pipeline. |

## 2. Request pipeline

`ProxyExecutor` runs these steps in order. Nothing is sent upstream before step 8, and every earlier
step ends the request on failure.

1. **Envelope** - Content-Type must be `application/json`; the body is read with a hard byte limit;
   JSON is parsed strictly (depth limit, no comments, no trailing commas); only `clientId` and
   `json` are accepted, and inside `json` only `urlId`, `parameters`, `encrypted`. Unknown and
   duplicated fields are rejected, not ignored.
2. **Operation** - looked up by `clientId`. Unknown -> 404. Caller not permitted -> 403. Disabled
   operation -> 403. Missing or disabled destination -> 403.
3. **Destination confirmation** - `urlId` must equal the configured destination key.
4. **Size** - the envelope must not exceed the operation's `MaxRequestBytes`.
5. **Decryption** - only for operations that require it. Plaintext and encrypted forms are never
   interchangeable: an encrypted operation rejects plaintext and the reverse.
6. **Parameters** - validated against the operation's rules (required, type, length, range,
   pattern). Unknown parameters are rejected unless the operation sets `AllowUnknownParameters`.
7. **Upstream request** - method, base URL and path template come from configuration. Validated
   path parameters are escaped into the template, query and body are built from validated values,
   headers are copied from the operation's allowlist only.
8. **Forward** - one attempt, with the configured timeout and the caller's cancellation token.
   The response is streamed back.
9. **Audit** - one record per attempt, in a `finally` block.

### Why a caller cannot redirect a request

- The envelope has no field for URL, method, path or headers, and extra fields are rejected.
- A path parameter must be a single inert segment. Values containing `/`, `\`, `%`, `?`, `#`, `:`,
  `@`, whitespace or control characters, and the values `.` and `..`, are rejected. Rejecting `%`
  removes every encoded variant (`%2e%2e`, `%2f`) in one rule. Accepted values are still escaped.
- After the URL is built and normalised by `System.Uri`, a final check requires the same scheme,
  host and port as the configured base URL and a path underneath the configured base path. This
  guard also catches a path template that is itself wrong.
- The upstream `HttpClient` does not follow redirects, so a destination cannot send the proxy to
  another host. It does not use cookies or the system proxy.
- A destination whose base URL is not `https` is refused (`Proxy:RequireHttpsDestinations`, on by
  default; switched off only in the Development settings, where the mock upstream is plain HTTP).
- Only `GET`, `POST`, `PUT`, `PATCH` and `DELETE` can be forwarded. An operation configured with
  any other method is refused at execution time.
- Hop-by-hop headers, `Host`, `Authorization`, `Cookie`, the proxy's own `X-Api-Key` and
  `X-Forwarded-*` are never forwarded, even when an allowlist entry names them.

## 3. Data model

| Table | Purpose |
|-------|---------|
| `Destinations` | Upstream APIs: key, name, base URL, active flag, timeout, response size limit, permitted ports, authentication profile reference, timestamps, `rowversion`. |
| `Operations` | Allowed actions: unique key, destination, method, path template, active flag, request size limit, encryption flag and profile, unknown-parameter policy, optional timeout, `rowversion`. |
| `ParameterRules` | Per operation: name, required, data type, location (path/query/body), max length, range, pattern. |
| `HeaderRules` | Per operation: allowlisted header names. |
| `EncryptionProfiles` | Algorithm and a **key reference**. Never key material. |
| `RequestLogs` | Audit records. Metadata only. |
| `ConfigurationHistory` | Who changed which configuration entity, when, and which properties (old/new). |

The schema is managed with EF Core migrations; `database/schema.sql` is generated from them
(`dotnet ef migrations script --idempotent`), and CI fails when the model and the migrations differ.
Tests run on SQLite and create the schema from the model, so they do not depend on SQL Server types.

### Operational settings

The assignment lists "operational settings" among the things the database contains, without
defining them. This solution splits them by scope:

- **Per destination and per operation - in the database.** Request timeout, maximum request size,
  maximum response size, permitted ports, the unknown-parameter policy and the encryption
  requirement are columns of `Destinations` and `Operations`. They are versioned with `rowversion`,
  recorded in `ConfigurationHistory`, and can differ for every destination and operation.
- **Process-wide - in application configuration (`Proxy:*`).** The global request size cap, the
  configuration cache lifetime, the per-destination concurrency limit and the per-caller rate limit.
  These are needed before, or independently of, a database connection: the size cap is applied by
  Kestrel before any request is read, and the cache lifetime decides how often the database is
  consulted at all. Keeping them in configuration also means the proxy can still enforce its limits
  while the database is unavailable.

If central, runtime-editable management of the process-wide values were required, they would move
to a small `OperationalSettings` key/value table read through the same cache and history mechanism.

Unique indexes enforce one operation per key and one destination per key. `RequestLogs` has no
foreign key to `Destinations`, so audit rows are independent of later configuration changes.

## 4. Encryption and key handling

- The database stores `EncryptionProfiles.KeyReference` only. `ISecretKeyProvider` resolves a
  reference to key bytes. The supplied implementation reads `EncryptionKeys:<reference>` from
  configuration, which is fed by environment variables or user-secrets. A production deployment
  registers an implementation backed by a secret manager (for example Azure Key Vault); nothing else
  changes.
- All decryption failures (wrong key, modified tag or ciphertext, bad base64, wrong sizes, plaintext
  that is not JSON) return the same public error, `invalid_encrypted_payload`, so the response does
  not reveal which part was wrong.
- Associated data (`clientId|urlId`) means a ciphertext captured for one operation fails
  authentication when replayed against another operation.
- Not implemented: replay protection for the *same* operation, and key rotation. Replay protection
  would add a timestamp and a nonce cache; rotation would add a key identifier to the envelope and
  let a profile reference a current and a previous key.
- Response encryption is not implemented; responses are relayed as received over TLS.

## 5. Timeouts and cancellation

- Timeout = operation override, otherwise the destination's `TimeoutSeconds`. It covers the whole
  exchange, including reading the response body.
- A linked `CancellationTokenSource` combines the caller's `RequestAborted` token with the timeout.
  When it fires, the caller token decides the meaning:
  - caller token cancelled -> outcome `CallerCancelled`, no response is written (audit status 499);
  - otherwise -> `504 upstream_timeout`, outcome `UpstreamTimeout`.
- `HttpClient.Timeout` is disabled so that the configured per-destination value is the only timeout.

## 6. HTTP client lifecycle

One named client from `IHttpClientFactory`, backed by a pooled `SocketsHttpHandler` with a bounded
connection lifetime (DNS changes are picked up). No `HttpClient` is created or disposed per request.

## 7. Streaming and size limits

- **Request** - the envelope must be parsed, validated and possibly decrypted, so it is buffered, but
  with a hard bound: Kestrel's `MaxRequestBodySize`, a bounded read loop
  (`Proxy:MaxEnvelopeBytes`), then the operation's `MaxRequestBytes`.
- **Response** - streamed to the caller with a fixed 16 KB buffer (`ResponseHeadersRead`), never
  fully buffered. `Destination.MaxResponseBytes` is enforced twice: by `Content-Length` before
  anything is sent to the caller (-> 502), and by counting bytes while streaming. If the limit is
  crossed mid-stream the status line has already been sent, so the connection is aborted; the
  caller sees a broken response rather than a silently truncated one.

## 8. Retries and idempotency

The proxy performs **no automatic retries**. Each accepted request results in at most one upstream
attempt.

- Safe to retry in principle: failures that prove the request never reached the destination
  (DNS failure, connection refused) for any method; and timeouts or 502/503/504 for idempotent
  methods (`GET`, `PUT`, `DELETE`).
- Not safe: a timeout or connection reset on `POST`/`PATCH`, because the destination may already
  have processed the request.
- Preventing duplicates for `POST`: where the destination supports idempotency keys, the caller
  sends an `Idempotency-Key` header, the operation allowlists that header, and the destination
  deduplicates. A retry policy could then be enabled per operation with a flag such as
  `IsIdempotent`. Without that upstream support, retrying is left to the caller, who knows the
  business meaning of a duplicate.

## 9. Configuration caching and consistency model

- An operation with its destination, rules and encryption profile is loaded as one immutable
  `OperationSnapshot` and cached in memory for `Proxy:ConfigCacheSeconds` (default 30 s). Unknown
  keys are not cached, so a caller cannot fill memory with misses.
- An update through the admin API invalidates the cache of the instance that handled it
  immediately.
- **Consistency model:** read-your-writes on the instance that performed the update; other
  instances converge within the cache lifetime. A disabled operation can therefore still be accepted
  by another instance for up to 30 seconds. If that window is unacceptable, set the lifetime to 0
  (every request reads the database) or add a Redis pub/sub invalidation message.
- If the configuration store cannot be read and there is no cached snapshot, the request is
  rejected with 503. The proxy never forwards without configuration.

## 10. Concurrency

- `Destinations` and `Operations` carry a SQL Server `rowversion`. An update must present the
  version it read. A stale version raises `DbUpdateConcurrencyException`, which becomes
  `409 configuration_conflict`; nothing is written. The client reloads and retries.
- **Requests already executing when an operation or destination is disabled:** they complete. A
  request works on the snapshot it loaded at step 2, and the policy decision has already been made
  when the change happens. Aborting in-flight calls would leave non-idempotent upstream operations
  in an unknown state. New requests are rejected as soon as the instance sees the change (section 9).

## 11. Resilience

- **Rate limiting** - fixed window per caller (`Proxy:RateLimitPerSecond`) -> 429. Requests that
  carry no valid API key are limited separately, per client address
  (`Proxy:UnauthenticatedRateLimitPerSecond`), so keys cannot be guessed at full speed and a flood of
  invalid keys does not consume the allowance of legitimate callers. Behind a load balancer the
  client address must come from forwarded headers; that wiring is deployment-specific and not
  included.
- **Bounded concurrency** - a bulkhead per destination
  (`Proxy:MaxConcurrentRequestsPerDestination`). Requests over the limit are rejected at once with
  `503 destination_busy` instead of queuing, so one slow destination cannot exhaust the proxy.
- **Circuit breaking** - per destination (`DestinationCircuitBreaker`). After
  `Proxy:CircuitBreakerFailureThreshold` consecutive connection failures or timeouts, the
  destination is not called for `Proxy:CircuitBreakerOpenSeconds`; requests get
  `503 destination_circuit_open` at once instead of each waiting for its own timeout. After the
  pause one request is let through as a probe: any HTTP answer closes the circuit, another failure
  opens it again. Only failures to get an answer count. An HTTP error status (including 5xx) is an
  answer and is relayed; a caller cancellation says nothing about the destination and is ignored.
  The breaker only blocks requests and never repeats one, so it adds no duplication risk. State is
  per proxy instance.

## 12. Error handling

- All proxy-generated errors share one JSON shape and fixed texts. They contain no hostnames,
  connection strings, exception messages, stack traces or configuration values. Details go to the
  structured log under the correlation id.
- A global exception handler covers anything outside the pipeline.
- A request body over the size limit is answered with 413 whether or not it carries a
  `Content-Length` (for a chunked body the server's own limit fires while the body is read, and that
  is mapped to the same 413). The 413 response also carries `Connection: close`: the oversized body
  is never read, so the connection cannot be reused.
- From an upstream response only the status, the body, `Content-Type` and a fixed set of headers
  are relayed (`ETag`, `Last-Modified`, `Cache-Control`, `Expires`, `Retry-After`,
  `Content-Language`, `Content-Disposition`, `Content-Encoding`). `Set-Cookie`, authentication
  headers and infrastructure headers are dropped. `Location` is relayed only when it is relative; an
  absolute `Location` would disclose an internal host name.
- `X-Proxy-Source` tells the caller whether a status code came from the destination or from the
  proxy. A destination's own 404 or 500 is relayed unchanged with `upstream`; a proxy 502/504 is
  marked `proxy`.

## 13. Audit policy

- **What is recorded:** one `RequestLogs` row for every request that reaches the proxy endpoint
  after authentication - successful, rejected and failed. Fields: correlation id, caller, operation
  key, destination, start/end, duration, whether forwarding was attempted, upstream status, proxy
  status, outcome category, reason code.
- **What is never recorded:** request or response bodies, parameter values, headers, API keys,
  encryption keys. The operation key is recorded only after it has matched the strict key format,
  so arbitrary caller text is not written to the log.
- Requests rejected by authentication (401) or by the rate limiter (429) do not enter the pipeline;
  they appear in the application log only.
- **How it is written:** the record is first emitted as a structured log event (`ProxyAudit`), then
  placed on a bounded in-memory queue (`Proxy:AuditQueueCapacity`, default 10,000). A background
  worker writes the queue to SQL Server in batches. The caller never waits for the database.
- **If SQL Server is unavailable:** audit persistence is **buffered and best-effort**. A failed
  batch is not retried; a `ProxyAuditPersistFailed` error is logged with the number of records. If
  the queue is full, the record is not queued and `ProxyAuditDropped` is logged. In both cases the
  request outcome is unchanged and the record still exists in the log stream.
  - Consequence: during a database outage the proxy keeps serving operations whose configuration is
    cached, and the audit trail for that period exists only in the logs. Operations must ship logs
    to durable storage and alert on `ProxyAuditPersistFailed` / `ProxyAuditDropped`; missing rows
    can be rebuilt from the `ProxyAudit` events.
  - Records still in the queue are written during a graceful shutdown. A process crash loses the
    queued rows (not the log events).
  - If the status line of an upstream response has already been sent when a timeout or size limit
    cuts the stream, the audit row records the status the caller actually received, together with
    the failure reason.
  - The alternative, mandatory audit (reject every request when the audit row cannot be written),
    gives a complete table but turns a database outage into a full proxy outage, and for a
    forwarded request the row can only be written *after* the upstream call, when it is too late to
    refuse. A regulated deployment that needs it would write an "attempt" row synchronously before
    forwarding and fail closed on that write.

## 14. Configuration history

`ProxyDbContext.SaveChanges` writes a `ConfigurationHistory` row for every added, modified or
deleted configuration entity, in the same transaction as the change: entity type and id, change
type, time, actor, and the changed properties with old and new values. Secret references
(`KeyReference`, `AuthProfileReference`) are stored as `[redacted]`; the fact that they changed is
visible, the values are not.

## 15. Testing approach

- Tests run the real application through `WebApplicationFactory`. Two things are replaced: the
  database provider (SQLite in-memory) and the upstream transport.
- `RecordingUpstream` is the mock destination. It is installed as the primary handler of the
  upstream `HttpClient`, so it sees exactly what would be sent on the wire, and it records every
  request. Every rejection test asserts that it recorded nothing; a status code alone is not treated
  as proof.
- SQLite has no `rowversion`. For non-SQL-Server providers the `DbContext` generates the
  concurrency token in the application, with the same compare-on-update behaviour, so the 409 path is
  tested. The SQL Server path uses the native `rowversion`.

## 16. Configuration API

- Destinations, encryption profiles and operations (with their parameter rules and header
  allowlist) are created through the admin API; flags, limits and rules can be changed there.
- Everything is validated when it is written (`ConfigurationValidator`): key format, allowed HTTP
  methods, an absolute `https` base URL without credentials or query, a well-formed path template
  without `.`/`..` segments, a rule for every placeholder and a placeholder for every path rule,
  body parameters only for methods with a body, compilable patterns, and an allowlist that contains
  no header from the never-forward list. An encryption profile is accepted only if its key reference
  resolves in the secret source.
- This is an early warning for administrators, not the security boundary: the request pipeline
  re-checks the same properties on every request, because configuration can also reach the database
  by other routes.
- There is no delete. Operations and destinations are retired by disabling them, so audit records
  and configuration history keep pointing at something that exists.

## 17. Secrets in development

The repository contains no password, API key or encryption key. `scripts/dev-setup.sh` /
`dev-setup.ps1` generates random values on the developer's machine: a SQL Server password and two
API keys in a git-ignored `.env`, and the connection string, API key hashes and encryption key in
.NET user-secrets. The CI workflow uses a throwaway password for a database container that exists
only for the duration of a job, and the smoke test generates its own keys per run.

## 18. Project structure

The solution is one deployable project with a folder per concern (`Proxy`, `Security`, `Data`,
`Audit`, `Admin`) rather than separate Domain / Application / Infrastructure projects. The service
has a single use case and a single pipeline; splitting it across assemblies would add indirection
without isolating anything that changes independently. The seams that matter are interfaces
(`IOperationConfigProvider`, `ISecretKeyProvider`, `IAuditWriter`), which is also what the tests
replace. If the proxy grew a second host (for example a separate admin service), `Proxy`, `Security`
and `Data` are the folders that would move into a shared library.

## 19. Known limitations

- Body parameters are scalar values; nested objects are not validated by rules.
- Upstream authentication profiles are modelled as a reference on the destination but no
  credentials are attached to upstream requests.
- Cache invalidation and circuit breaker state are per instance (sections 9 and 11).
- The Development environment maps a mock upstream, Swagger UI and an encryption helper. A
  production host must never run with `ASPNETCORE_ENVIRONMENT=Development`.
- A path parameter rule whose name does not appear in the path template is treated as broken
  configuration and the operation is not forwarded.

# Defect report

## Scope

The assignment refers to an existing, partially implemented repository. **No such repository was
supplied**, so this solution was written from scratch and there is no inherited code to report on.

This report therefore contains:

1. defects found and fixed in this solution while it was being developed and tested;
2. the defect classes a proxy of this kind typically contains, with the design decision and the
   test that prevent each one here.

If the original repository is provided, its defects will be added to section 1 in the same format.

## 1. Defects found and fixed during development

| File / component | Observed behavior | Root cause | Risk or impact | Fix | Test |
|---|---|---|---|---|---|
| `Proxy/ProxyExecutor.cs` - `ResolveParameters` | Every valid encrypted request was rejected with `invalid_parameters`. | The decrypted buffer was zeroed immediately after `JsonDocument.Parse`. `JsonDocument` does not copy a `byte[]`; it reads from it lazily, so the parameters were wiped before validation. | Encrypted operations unusable. Fail-closed, so no data exposure. | The buffer is no longer zeroed while the document is alive; the document is disposed at the end of the request. | `T12_Valid_encrypted_payload_is_decrypted_and_forwarded` |
| `Proxy/OperationConfigProvider.cs` - `InvalidateAll` | A lookup running at the same moment as a cache invalidation could throw `ObjectDisposedException`. | The previous `CancellationTokenSource` was disposed while another thread could still read its `Token`. | Sporadic 503 during configuration changes. | The old source is cancelled but not disposed. A cancelled token also makes the concurrent lookup's cache entry expire at once, so a stale snapshot is not cached. | Covered indirectly by `T16b_Update_with_the_current_version_succeeds_and_takes_effect` |
| `Proxy/ProxyExecutor.cs` - `ReadEnvelopeAsync` | An oversized body sent without `Content-Length` (chunked) returned `500 internal_error`. | The server enforces its body limit while the body is read and throws `BadHttpRequestException`; only cancellation was handled, so the exception reached the generic handler. The tests did not see it because the in-memory test server has no such limit. | Wrong status for a case the assignment lists explicitly; audited as an internal error instead of a rejection. Fail-closed. | The exception is mapped to `413 request_too_large`. | `Oversized_body_without_content_length_is_rejected_as_too_large`; smoke test "oversized chunked body is rejected" (real server) |
| `Infrastructure/ProxyErrors.cs` | The request following a 413 on the same connection failed with "response ended prematurely". | The oversized body is not read, so the server closes the connection, but the response did not say so and the client reused it. | Intermittent failure of an unrelated, valid request. | 413 responses carry `Connection: close`. | Verified on the real server (repeated runs) |
| `Program.cs` - middleware order | 300 consecutive requests with wrong API keys all returned 401; none was throttled. | The rate limiter ran after authorization and was keyed by caller name, so unauthenticated requests never reached it. | API keys could be guessed at full speed. | The limiter now runs between authentication and authorization, with a per-address limit for requests without a valid key. | `Repeated_requests_with_invalid_api_keys_are_throttled`, `Throttling_of_invalid_keys_does_not_affect_a_caller_with_a_valid_key`; smoke test |
| `Proxy/ProxyExecutor.cs` - `ForwardAsync` | Upstream response headers other than `Content-Type` were lost (`ETag`, `Retry-After`, `Location`). | Only the content type was copied. | Callers lost caching, back-off and resource-location information. | A fixed allowlist of response headers is relayed; `Location` only when relative. | `Safe_upstream_response_headers_are_relayed_and_sensitive_ones_are_not`, `Location_header_is_relayed_only_when_it_does_not_expose_an_internal_host` |
| `Audit/AuditWriter.cs` (now `AuditQueue.cs`) | Every request waited for the audit insert; with the database down each request was held for up to 5 seconds. | The audit row was written synchronously in the request path. | A database outage could exhaust the proxy under load. | Records go to a bounded queue and are written by a background worker. | `T17_*` (via queue flush); smoke test audit checks |
| `Proxy/ProxyExecutor.cs` - `ExecuteAsync` | When a timeout or size limit cut an upstream response mid-stream, the audit row said 504/502 although the caller had received the upstream status line. | The audit status was taken from the error, not from what was sent. | Misleading audit trail. | The audit row records the status actually sent. | Verified on the real server |
| `Infrastructure/ProxyErrors.cs` | Errors produced by the global exception handler had no `X-Correlation-ID` header. | The handler clears response headers set earlier in the pipeline. | Harder to trace exactly the failures that need tracing. | The error writer sets the header itself. | `Proxy_errors_always_carry_the_correlation_id_header` |
| `Proxy/UpstreamRequestBuilder.cs` | A destination with an `http://` base URL was called; a path parameter with no placeholder in the template was dropped silently. | No scheme requirement; unused path values were ignored. | Unencrypted upstream traffic; a request different from the configured intent. | `Proxy:RequireHttpsDestinations` (default on); unused path parameters are treated as misconfiguration. | `Destination_without_https_is_not_called_when_https_is_required`, `Path_parameter_without_a_placeholder_in_the_template_is_not_forwarded` |
| `Security/ApiKeyAuthentication.cs` | Compiler warning: constant `Scheme` hid the inherited `AuthenticationHandler.Scheme` property. | Name clash with a base-class member. | Confusing code; wrong member could be used by mistake. | Constant renamed to `SchemeName`. | Build |

## 2. Defect classes prevented by design

| Component | Typical defect | Risk | How it is prevented here | Test |
|---|---|---|---|---|
| Request contract | Destination URL, method or path taken from the request, or extra fields silently ignored. | SSRF; the proxy becomes an open forwarder. | Strict envelope; routing data comes only from configuration; unknown fields are rejected. | `T05_*`, `T05b_*`, `T06_*` |
| URL building | Parameter values concatenated into the path. | Path traversal to unintended upstream endpoints. | Single-segment rule, rejection of `%` and separators, escaping, final origin/base-path check. | `T09_*`, `T09b_*` |
| Method handling | Any configured or supplied method is forwarded. | `TRACE`/`CONNECT` abuse. | Fixed allowlist enforced at execution time. | `T06b_*`, `T06c_*` |
| Headers | All incoming headers copied upstream. | Leak of caller credentials and cookies; request smuggling through hop-by-hop headers. | Per-operation allowlist plus a deny list that always wins. | `T10_Disallowed_headers_are_not_forwarded` |
| Validation order | Request forwarded before validation finishes, or validation failure only logged. | Unauthorized request reaches the destination. | Linear pipeline; the upstream call is the last step. Tests assert on the mock destination, not on status codes. | every rejection test |
| Encryption | Tag not verified; decryption failure falls back to plaintext; plaintext accepted for an encrypted operation. | Tampered or unprotected payload forwarded. | AES-GCM with authentication; the two payload forms are not interchangeable; any failure rejects. | `T11_*`, `T12b_*`, `T12c_*`, `T12d_*` |
| Key storage | Keys stored in the database or in `appsettings.json`. | Key disclosure. | Database holds a reference; `ISecretKeyProvider` resolves it from a secret source. | `Configuration_changes_are_recorded_in_history_without_secret_references` |
| HttpClient | `new HttpClient()` per request. | Socket exhaustion. | `IHttpClientFactory` with a pooled handler. | Code review |
| Timeouts | Caller cancellation reported as a timeout, or no timeout at all. | Wrong diagnosis; hung requests. | Linked token; the caller token decides the meaning. | `T13_*`, `T15_*` |
| Retries | Blanket retry policy on the client. | Duplicate `POST` processing. | No automatic retries. | `T13_*`, `T14_*` assert exactly one upstream request |
| Overload | No limit on concurrent upstream calls or on request rate. | One slow destination or one noisy caller exhausts the proxy. | Per-destination bulkhead; per-caller rate limit. | `Requests_over_the_destination_concurrency_limit_are_rejected_without_forwarding`, `Caller_over_its_rate_limit_receives_429_and_those_requests_are_not_forwarded` |
| Redirects | Upstream redirect followed automatically. | Destination steers the proxy to an unconfigured host. | `AllowAutoRedirect = false`. | Code review |
| Response handling | Whole response buffered. | Memory exhaustion. | Streaming with a byte limit. | `T14c_Oversized_upstream_response_is_not_relayed` |
| Errors | Exception text or host names returned to the caller. | Information disclosure. | Fixed public messages; details only in logs. | `T14b_*` |
| Concurrency | Last write wins on configuration. | Silent loss of a change, for example a disable. | `rowversion` and 409. | `T16_*` |
| Audit | Payloads or credentials written to logs; rejected requests not recorded. | Data leak; gaps in the audit trail. | Metadata-only records for every outcome. | `T17_*` |

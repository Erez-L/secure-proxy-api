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
| Redirects | Upstream redirect followed automatically. | Destination steers the proxy to an unconfigured host. | `AllowAutoRedirect = false`. | Code review |
| Response handling | Whole response buffered. | Memory exhaustion. | Streaming with a byte limit. | `T14c_Oversized_upstream_response_is_not_relayed` |
| Errors | Exception text or host names returned to the caller. | Information disclosure. | Fixed public messages; details only in logs. | `T14b_*` |
| Concurrency | Last write wins on configuration. | Silent loss of a change, for example a disable. | `rowversion` and 409. | `T16_*` |
| Audit | Payloads or credentials written to logs; rejected requests not recorded. | Data leak; gaps in the audit trail. | Metadata-only records for every outcome. | `T17_*` |

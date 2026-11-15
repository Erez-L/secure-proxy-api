# Enterprise Secure Proxy Web API

A backend-only ASP.NET Core (.NET 8) Web API that forwards **only explicitly configured operations**
to **predefined destinations**. The caller names an operation key; the destination URL, HTTP method
and path always come from the database. The proxy is configuration-driven, secure by default and
fails closed: a request that fails any check never reaches a destination.

- Stack: C# / .NET 8, ASP.NET Core Web API, Entity Framework Core 8, SQL Server
- Design and trade-offs: [DESIGN-NOTES.md](DESIGN-NOTES.md)
- Defects: [DEFECT-REPORT.md](DEFECT-REPORT.md)
- AI usage declaration: [AI-USAGE.md](AI-USAGE.md)

## Repository layout

```
SecureProxy.sln
src/SecureProxy.Api/        the Web API
  Proxy/                    request pipeline (executor, validation, URL/headers, config cache, bulkhead)
  Security/                 API-key authentication, AES-GCM, secret key source abstraction
  Data/                     EF Core entities and DbContext (rowversion, configuration history)
  Audit/                    audit queue and background writer
  Admin/                    configuration endpoints (optimistic concurrency, 409)
  Development/              development-only seed data, mock upstream, encryption helper
tests/SecureProxy.Tests/    xUnit tests with a recording mock destination
database/schema.sql         SQL Server schema script
scripts/smoke-test.sh       end-to-end check against a real SQL Server (used by CI)
docker-compose.yml          local SQL Server
```

## Prerequisites

- .NET 8 SDK
- SQL Server (any edition). `docker compose up -d` starts one locally.

## Run the tests

The tests need neither SQL Server nor network access. They host the real application in memory,
swap the database provider for SQLite in-memory and replace the upstream transport with a mock
destination that records every request.

```bash
dotnet test
```

The same suite runs in GitHub Actions on every push (`.github/workflows/ci.yml`), together with an
end-to-end run against SQL Server 2022 (`scripts/smoke-test.sh`).

## Run in the browser (GitHub Codespaces)

The repository contains a dev container (`.devcontainer/`) with the .NET 8 SDK and Docker. Open
**Code > Codespaces > Create codespace on main**. When the container is ready, packages are restored
and a development encryption key is already set, so step 2 below can be skipped.

## Run the API locally

1. Start SQL Server:

   ```bash
   docker compose up -d
   ```

   To use another server, change `ConnectionStrings:ProxyDb` in
   `src/SecureProxy.Api/appsettings.Development.json` or set the environment variable
   `ConnectionStrings__ProxyDb`.

2. Provide the development encryption key. Keys are never stored in the repository or the database;
   the database stores only the reference name (`dev-crm-key`).

   ```bash
   cd src/SecureProxy.Api
   dotnet user-secrets set "EncryptionKeys:dev-crm-key" "$(openssl rand -base64 32)"
   ```

   PowerShell:

   ```powershell
   $bytes = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
   dotnet user-secrets set "EncryptionKeys:dev-crm-key" ([Convert]::ToBase64String($bytes))
   ```

3. Run:

   ```bash
   dotnet run --project src/SecureProxy.Api
   ```

   The API listens on `http://localhost:5080`. In the Development environment it creates the
   database schema, seeds two sample operations and maps a small in-process mock upstream
   (`/_mock/...`) so the proxy can be exercised without any external service.

### Development API keys

| Key              | Role   | May execute  |
|------------------|--------|--------------|
| `dev-caller-key` | caller | `customer.*` |
| `dev-admin-key`  | admin  | everything   |

Only SHA-256 hashes of these keys are in `appsettings.Development.json`. They exist for local use
only; other environments supply their own `Auth:ApiKeys` entries through configuration.

### Try it

Plaintext operation:

```bash
curl -i http://localhost:5080/api/proxy/execute \
  -H "X-Api-Key: dev-caller-key" -H "Content-Type: application/json" \
  -d '{"clientId":"customer.get","json":{"urlId":"crm-main","parameters":{"customerId":42}}}'
```

A rejected request (unknown parameter) - returns 400 and is never forwarded:

```bash
curl -i http://localhost:5080/api/proxy/execute \
  -H "X-Api-Key: dev-caller-key" -H "Content-Type: application/json" \
  -d '{"clientId":"customer.get","json":{"urlId":"crm-main","parameters":{"customerId":42,"debug":true}}}'
```

Encrypted operation. `/_dev/encrypt` (Development only, admin key) builds the envelope:

```bash
curl -s http://localhost:5080/_dev/encrypt \
  -H "X-Api-Key: dev-admin-key" -H "Content-Type: application/json" \
  -d '{"clientId":"customer.create","urlId":"crm-main","parameters":{"name":"Dana","age":30}}' > envelope.json

curl -i http://localhost:5080/api/proxy/execute \
  -H "X-Api-Key: dev-caller-key" -H "Content-Type: application/json" -d @envelope.json
```

Configuration update with optimistic concurrency (the second identical PUT returns 409):

```bash
curl -s http://localhost:5080/api/admin/operations/customer.get -H "X-Api-Key: dev-admin-key"
# copy rowVersion from the response
curl -i -X PUT http://localhost:5080/api/admin/operations/customer.get \
  -H "X-Api-Key: dev-admin-key" -H "Content-Type: application/json" \
  -d '{"isActive":true,"maxRequestBytes":32768,"timeoutSeconds":null,"rowVersion":"<value>"}'
```

## Request contract

`POST /api/proxy/execute`

```json
{
  "clientId": "customer.get",
  "json": {
    "urlId": "crm-main",
    "parameters": { "customerId": 42 }
  }
}
```

| Field             | Meaning |
|-------------------|---------|
| `clientId`        | Key of the configured operation. |
| `json.urlId`      | Key of the destination the caller expects. It must equal the destination configured for the operation; it cannot select a different one. |
| `json.parameters` | Parameters of the operation, validated against its parameter rules. |
| `json.encrypted`  | Replaces `parameters` for operations that require encryption: `{ "nonce", "ciphertext", "tag" }`, base64, AES-256-GCM. The plaintext is the JSON `parameters` object. Associated data: UTF-8 of `clientId + "|" + urlId`. |

Any other field, at any level of the envelope, is rejected.

## Responses

Every response carries `X-Correlation-ID` and `X-Proxy-Source`:

- `X-Proxy-Source: upstream` - the destination answered; status code and body are relayed unchanged.
- `X-Proxy-Source: proxy` - the proxy produced the response:

  ```json
  { "source": "proxy", "code": "invalid_parameters", "message": "...", "correlationId": "..." }
  ```

| Status | Code | When |
|--------|------|------|
| 400 | `invalid_request` | Malformed envelope, unsupported or duplicated field |
| 400 | `destination_mismatch` | `urlId` is not the destination of the operation |
| 400 | `invalid_parameters` | Missing, unknown or invalid parameter, path traversal attempt |
| 400 | `invalid_encrypted_payload` | Decryption or authentication failed, or wrong payload form |
| 401 | `unauthorized` | Missing or invalid API key |
| 403 | `operation_not_permitted` | The caller may not execute this operation |
| 403 | `operation_disabled` / `destination_unavailable` | Disabled operation / missing or disabled destination |
| 404 | `operation_not_found` | Unknown operation |
| 409 | `configuration_conflict` | Stale configuration update (admin API) |
| 413 | `request_too_large` | Envelope exceeds the global or per-operation limit |
| 415 | `invalid_content_type` | Content-Type is not `application/json` |
| 429 | `rate_limited` | Per-caller rate limit exceeded, or too many requests without a valid API key from one address |
| 500 | `operation_misconfigured` / `internal_error` | Invalid configuration (not forwarded) / unexpected failure |
| 502 | `upstream_unreachable` / `upstream_response_too_large` | Connection failure / response over the limit |
| 503 | `destination_busy` / `configuration_unavailable` | Concurrency limit reached / configuration store unavailable |
| 504 | `upstream_timeout` | The destination exceeded its configured timeout |

## Database

- In Development the application creates the schema on start-up
  (`Database:ApplyMigrationsOnStartup`). It applies EF Core migrations when the project contains
  them and otherwise creates the schema from the model.
- `database/schema.sql` creates the same schema by hand on an empty database.
- To manage the schema with EF Core migrations:

  ```bash
  dotnet tool install --global dotnet-ef
  dotnet ef migrations add InitialCreate --project src/SecureProxy.Api
  dotnet ef database update --project src/SecureProxy.Api
  ```

## Configuration reference

| Setting | Default | Meaning |
|---------|---------|---------|
| `ConnectionStrings:ProxyDb` | - | SQL Server connection string |
| `Proxy:MaxEnvelopeBytes` | 1048576 | Hard limit for any request body |
| `Proxy:ConfigCacheSeconds` | 30 | Lifetime of cached configuration (0 disables the cache) |
| `Proxy:MaxConcurrentRequestsPerDestination` | 50 | Bulkhead size per destination |
| `Proxy:RateLimitPerSecond` | 50 | Per-caller rate limit |
| `Proxy:UnauthenticatedRateLimitPerSecond` | 10 | Per-address limit for requests without a valid API key |
| `Proxy:RequireHttpsDestinations` | true | Refuse destinations whose base URL is not `https` (false in Development) |
| `Proxy:AuditQueueCapacity` | 10000 | Audit records that may wait for the background writer |
| `Auth:ApiKeys:n` | - | `Name`, `Sha256`, `Role` (`caller`/`admin`), `AllowedOperations` |
| `EncryptionKeys:<reference>` | - | Base64 256-bit key; supply through a secret store or environment |

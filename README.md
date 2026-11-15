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
  Proxy/                    request pipeline (executor, validation, URL/headers, config cache,
                            bulkhead, circuit breaker)
  Security/                 API-key authentication, AES-GCM, secret key source abstraction
  Data/                     EF Core entities and DbContext (rowversion, configuration history)
  Migrations/               EF Core migrations
  Audit/                    audit queue and background writer
  Admin/                    configuration API and its validation
  Development/              development-only seed data, mock upstream, encryption helper
tests/SecureProxy.Tests/    xUnit tests with a recording mock destination
database/schema.sql         SQL Server schema script (generated from the migrations)
scripts/dev-setup.sh|.ps1   one-time local setup: generates development secrets
scripts/smoke-test.sh       end-to-end check against a real SQL Server (used by CI)
docker-compose.yml          local SQL Server
```

## Prerequisites

- .NET 8 SDK
- Docker (for the local SQL Server), or an existing SQL Server

## Run the tests

The tests need neither SQL Server nor network access. They host the real application in memory,
swap the database provider for SQLite in-memory and replace the upstream transport with a mock
destination that records every request.

```bash
dotnet test
```

The same suite runs in GitHub Actions on every push (`.github/workflows/ci.yml`), together with an
end-to-end run against SQL Server 2022 (`scripts/smoke-test.sh`), a run of the local setup described
below, and the setup script on Windows.

## Run in the browser (GitHub Codespaces)

The repository contains a dev container (`.devcontainer/`) with the .NET 8 SDK and Docker. Open
**Code > Codespaces > Create codespace on main**. When the container is ready, packages are restored
and step 1 below has already been done.

## Run the API locally

No password, API key or encryption key is stored in this repository. Step 1 generates them.

1. Generate the development secrets (once):

   ```bash
   bash scripts/dev-setup.sh          # Linux, macOS, Git Bash, WSL
   ```

   ```powershell
   ./scripts/dev-setup.ps1            # Windows PowerShell
   ```

   This creates `.env` (git-ignored) with a SQL Server password and two API keys, and stores the
   connection string, the API key hashes and a development encryption key in .NET user-secrets.

2. Start SQL Server (it reads its password from `.env`):

   ```bash
   docker compose up -d
   ```

   To use another SQL Server instead, set the user secret `ConnectionStrings:ProxyDb`.

3. Run:

   ```bash
   dotnet run --project src/SecureProxy.Api
   ```

   The API listens on `http://localhost:5080`. In the Development environment it waits for the
   database, applies the EF Core migrations, seeds two sample operations and maps a small in-process
   mock upstream (`/_mock/...`), so the proxy can be exercised without any external service.
   Swagger UI is at `http://localhost:5080/swagger`.

### Development API keys

| Variable in `.env`  | Role   | May execute  |
|---------------------|--------|--------------|
| `PROXY_CALLER_KEY`  | caller | `customer.*` |
| `PROXY_ADMIN_KEY`   | admin  | everything   |

Load them into your shell before running the examples:

```bash
set -a; . ./.env; set +a
```

### Try it

Plaintext operation:

```bash
curl -i http://localhost:5080/api/proxy/execute \
  -H "X-Api-Key: $PROXY_CALLER_KEY" -H "Content-Type: application/json" \
  -d '{"clientId":"customer.get","json":{"urlId":"crm-main","parameters":{"customerId":42}}}'
```

A rejected request (unknown parameter) - returns 400 and is never forwarded:

```bash
curl -i http://localhost:5080/api/proxy/execute \
  -H "X-Api-Key: $PROXY_CALLER_KEY" -H "Content-Type: application/json" \
  -d '{"clientId":"customer.get","json":{"urlId":"crm-main","parameters":{"customerId":42,"debug":true}}}'
```

Encrypted operation. `/_dev/encrypt` (Development only, admin key) builds the envelope:

```bash
curl -s http://localhost:5080/_dev/encrypt \
  -H "X-Api-Key: $PROXY_ADMIN_KEY" -H "Content-Type: application/json" \
  -d '{"clientId":"customer.create","urlId":"crm-main","parameters":{"name":"Dana","age":30}}' > envelope.json

curl -i http://localhost:5080/api/proxy/execute \
  -H "X-Api-Key: $PROXY_CALLER_KEY" -H "Content-Type: application/json" -d @envelope.json
```

Create a destination and an operation through the configuration API, then execute it:

```bash
curl -i http://localhost:5080/api/admin/destinations \
  -H "X-Api-Key: $PROXY_ADMIN_KEY" -H "Content-Type: application/json" \
  -d '{"key":"crm-second","name":"Second CRM","baseUrl":"http://localhost:5080/_mock","timeoutSeconds":10,"maxResponseBytes":1048576,"isActive":true}'

curl -i http://localhost:5080/api/admin/operations \
  -H "X-Api-Key: $PROXY_ADMIN_KEY" -H "Content-Type: application/json" \
  -d '{"operationKey":"customer.lookup","destinationKey":"crm-second","httpMethod":"GET","pathTemplate":"/customers/{customerId}","isActive":true,"maxRequestBytes":65536,"requiresEncryption":false,"allowUnknownParameters":false,"parameters":[{"name":"customerId","isRequired":true,"dataType":"Integer","location":"Path","minValue":1}],"allowedHeaders":[]}'

curl -i http://localhost:5080/api/proxy/execute \
  -H "X-Api-Key: $PROXY_CALLER_KEY" -H "Content-Type: application/json" \
  -d '{"clientId":"customer.lookup","json":{"urlId":"crm-second","parameters":{"customerId":7}}}'
```

Configuration update with optimistic concurrency (the second identical PUT returns 409):

```bash
curl -s http://localhost:5080/api/admin/operations/customer.get -H "X-Api-Key: $PROXY_ADMIN_KEY"
# copy rowVersion from the response
curl -i -X PUT http://localhost:5080/api/admin/operations/customer.get \
  -H "X-Api-Key: $PROXY_ADMIN_KEY" -H "Content-Type: application/json" \
  -d '{"isActive":true,"maxRequestBytes":32768,"timeoutSeconds":null,"rowVersion":"<rowVersion>"}'
```

Run the whole end-to-end check against the local SQL Server:

```bash
dotnet build -c Release
SQL_CONTAINER=$(docker compose ps -q sqlserver) SA_PASSWORD=$MSSQL_SA_PASSWORD bash scripts/smoke-test.sh migrations
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
| 400 | `invalid_configuration` | Configuration API: the submitted configuration is not valid |
| 401 | `unauthorized` | Missing or invalid API key |
| 403 | `operation_not_permitted` | The caller may not execute this operation |
| 403 | `operation_disabled` / `destination_unavailable` | Disabled operation / missing or disabled destination |
| 404 | `operation_not_found` | Unknown operation |
| 409 | `configuration_conflict` / `already_exists` | Configuration API: stale update / duplicate key |
| 413 | `request_too_large` | Envelope exceeds the global or per-operation limit |
| 415 | `invalid_content_type` | Content-Type is not `application/json` |
| 429 | `rate_limited` | Per-caller rate limit exceeded, or too many requests without a valid API key from one address |
| 500 | `operation_misconfigured` / `internal_error` | Invalid configuration (not forwarded) / unexpected failure |
| 502 | `upstream_unreachable` / `upstream_response_too_large` | Connection failure / response over the limit |
| 503 | `destination_busy` / `destination_circuit_open` / `configuration_unavailable` | Concurrency limit reached / circuit breaker open / configuration store unavailable |
| 504 | `upstream_timeout` | The destination exceeded its configured timeout |

## Configuration API (admin role)

| Method and path | Purpose |
|-----------------|---------|
| `GET  /api/admin/destinations/{key}` | Read a destination |
| `POST /api/admin/destinations` | Create a destination |
| `PUT  /api/admin/destinations/{key}` | Enable/disable, change the timeout (needs `rowVersion`) |
| `POST /api/admin/encryption-profiles` | Create a profile; the key reference must resolve in the secret source |
| `GET  /api/admin/operations/{key}` | Read an operation with its rules |
| `POST /api/admin/operations` | Create an operation with parameter rules and header allowlist |
| `PUT  /api/admin/operations/{key}` | Enable/disable, change limits (needs `rowVersion`) |
| `PUT  /api/admin/operations/{key}/rules` | Replace parameter rules and header allowlist (needs `rowVersion`) |
| `GET  /api/admin/history/{entityType}` | Configuration history |

Configuration is validated when it is written, updates use optimistic concurrency (409 on a stale
`rowVersion`), and every change is recorded in `ConfigurationHistory`. There is no delete: an
operation or destination is retired by disabling it.

## Database

- The schema is managed with EF Core migrations (`src/SecureProxy.Api/Migrations`). In Development
  the application applies them on start-up (`Database:ApplyMigrationsOnStartup`). Elsewhere apply
  them as a deployment step:

  ```bash
  dotnet tool install --global dotnet-ef
  dotnet ef database update --project src/SecureProxy.Api
  ```

- `database/schema.sql` is the same schema as an idempotent SQL script, generated from the
  migrations, for environments where the schema is applied by hand:

  ```bash
  dotnet ef migrations script --idempotent --project src/SecureProxy.Api -o database/schema.sql
  ```

- CI fails if the model and the migrations drift apart
  (`dotnet ef migrations has-pending-model-changes`).

## Configuration reference

| Setting | Default | Meaning |
|---------|---------|---------|
| `ConnectionStrings:ProxyDb` | - | SQL Server connection string (secret) |
| `Proxy:MaxEnvelopeBytes` | 1048576 | Hard limit for any request body |
| `Proxy:ConfigCacheSeconds` | 30 | Lifetime of cached configuration (0 disables the cache) |
| `Proxy:MaxConcurrentRequestsPerDestination` | 50 | Bulkhead size per destination |
| `Proxy:RateLimitPerSecond` | 50 | Per-caller rate limit |
| `Proxy:UnauthenticatedRateLimitPerSecond` | 10 | Per-address limit for requests without a valid API key |
| `Proxy:CircuitBreakerFailureThreshold` | 5 | Consecutive connection failures/timeouts that open the circuit (0 disables) |
| `Proxy:CircuitBreakerOpenSeconds` | 30 | How long an open circuit blocks calls before a probe is allowed |
| `Proxy:RequireHttpsDestinations` | true | Refuse destinations whose base URL is not `https` (false in Development) |
| `Proxy:AuditQueueCapacity` | 10000 | Audit records that may wait for the background writer |
| `Auth:ApiKeys:n` | - | `Name`, `Sha256`, `Role` (`caller`/`admin`), `AllowedOperations` (secret) |
| `EncryptionKeys:<reference>` | - | Base64 256-bit key; supply through a secret store or environment (secret) |

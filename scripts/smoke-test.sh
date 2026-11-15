#!/usr/bin/env bash
# End-to-end smoke test of the API against a real SQL Server.
#
#   scripts/smoke-test.sh migrations   the application applies the EF Core migrations at start-up
#   scripts/smoke-test.sh script       the schema is created from database/schema.sql first
#
# Expects SQL Server in a Docker container: SQL_CONTAINER (container id or name) and SA_PASSWORD.
# The test uses its own database and its own randomly generated API keys and encryption key,
# which it passes to the application through environment variables.
set -euo pipefail

MODE="${1:-migrations}"
: "${SQL_CONTAINER:?SQL_CONTAINER is required}"
: "${SA_PASSWORD:?SA_PASSWORD is required}"
DB="SecureProxySmoke"
BASE="http://localhost:5080"
FAILED=0

CALLER_KEY="$(openssl rand -hex 24)"
ADMIN_KEY="$(openssl rand -hex 24)"
sha256() { printf '%s' "$1" | openssl dgst -sha256 -hex | awk '{print $NF}'; }

sql() {  # sql <database> <query>
  docker exec "$SQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$SA_PASSWORD" -d "$1" -b -h -1 -W -Q "SET NOCOUNT ON; $2"
}

check() {  # check <description> <expected> <actual>
  if [ "$2" == "$3" ]; then echo "ok    $1"; else echo "FAIL  $1 (expected '$2', got '$3')"; FAILED=1; fi
}

echo "Waiting for SQL Server..."
for _ in $(seq 1 60); do sql master "SELECT 1" >/dev/null 2>&1 && break; sleep 2; done
sql master "SELECT 1" >/dev/null

if [ "$MODE" == "script" ]; then
  sql master "CREATE DATABASE [$DB]"
  docker cp database/schema.sql "$SQL_CONTAINER":/tmp/schema.sql
  docker exec "$SQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$SA_PASSWORD" -d "$DB" -b -I -i /tmp/schema.sql
  export Database__ApplyMigrationsOnStartup=false
fi

export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="$BASE"
export ConnectionStrings__ProxyDb="Server=localhost,1433;Database=$DB;User Id=sa;Password=$SA_PASSWORD;TrustServerCertificate=True;Encrypt=True"
export Auth__ApiKeys__0__Name="smoke-caller"
export Auth__ApiKeys__0__Sha256="$(sha256 "$CALLER_KEY")"
export Auth__ApiKeys__0__Role="caller"
export Auth__ApiKeys__0__AllowedOperations__0="customer.*"
export Auth__ApiKeys__1__Name="smoke-admin"
export Auth__ApiKeys__1__Sha256="$(sha256 "$ADMIN_KEY")"
export Auth__ApiKeys__1__Role="admin"
export Auth__ApiKeys__1__AllowedOperations__0="*"

# The variable name contains '-', which a shell cannot export, so it is passed through env.
env "EncryptionKeys__dev-crm-key=$(openssl rand -base64 32)" \
  dotnet run --project src/SecureProxy.Api -c Release --no-build --no-launch-profile > api.log 2>&1 &
API_PID=$!
trap 'kill $API_PID 2>/dev/null || true' EXIT

echo "Waiting for the API..."
for _ in $(seq 1 60); do curl -fs "$BASE/health" >/dev/null 2>&1 && break; sleep 2; done
curl -fs "$BASE/health" >/dev/null

call() {  # call <api-key> <json-body>  -> prints "<status> <X-Proxy-Source>"
  curl -s -o body.json -D headers.txt -w "%{http_code}" "$BASE/api/proxy/execute" \
    -H "X-Api-Key: $1" -H "Content-Type: application/json" -d "$2" > status.txt
  echo "$(cat status.txt) $(grep -i '^x-proxy-source:' headers.txt | tr -d '\r' | awk '{print $2}')"
}

admin() {  # admin <method> <path> [json-body]  -> prints the status code, body goes to admin.json
  curl -s -o admin.json -w "%{http_code}" -X "$1" "$BASE$2" \
    -H "X-Api-Key: $ADMIN_KEY" -H "Content-Type: application/json" ${3:+-d "$3"}
}

GET='{"clientId":"customer.get","json":{"urlId":"crm-main","parameters":{"customerId":42}}}'

# --- proxy endpoint
check "valid operation is forwarded"          "200 upstream" "$(call "$CALLER_KEY" "$GET")"
check "upstream body is relayed"              "42"           "$(jq -r .id body.json)"
check "unknown parameter is rejected"         "400 proxy"    "$(call "$CALLER_KEY" '{"clientId":"customer.get","json":{"urlId":"crm-main","parameters":{"customerId":42,"debug":true}}}')"
check "unknown operation is rejected"         "404 proxy"    "$(call "$CALLER_KEY" '{"clientId":"customer.delete","json":{"urlId":"crm-main","parameters":{}}}')"
check "caller-supplied url is rejected"       "400 proxy"    "$(call "$CALLER_KEY" '{"clientId":"customer.get","url":"https://example.org","json":{"urlId":"crm-main","parameters":{"customerId":42}}}')"
check "missing api key is rejected"           "401 proxy"    "$(call '' "$GET")"

# --- encrypted operation: build the envelope with the development helper, then execute it
curl -fs "$BASE/_dev/encrypt" -H "X-Api-Key: $ADMIN_KEY" -H "Content-Type: application/json" \
  -d '{"clientId":"customer.create","urlId":"crm-main","parameters":{"name":"Dana","age":30}}' > envelope.json
check "encrypted operation is forwarded"      "201 upstream" "$(call "$CALLER_KEY" "$(cat envelope.json)")"
check "decrypted body reached the upstream"   "Dana"         "$(jq -r .received.name body.json)"
jq '.json.encrypted.tag = "AAAAAAAAAAAAAAAAAAAAAA=="' envelope.json > tampered.json
check "tampered tag is rejected"              "400 proxy"    "$(call "$CALLER_KEY" "$(cat tampered.json)")"
check "plaintext to encrypted op is rejected" "400 proxy"    "$(call "$CALLER_KEY" '{"clientId":"customer.create","json":{"urlId":"crm-main","parameters":{"name":"Dana"}}}')"

# --- configuration API: create a destination and an operation, then use them
NEW_DEST='{"key":"crm-second","name":"Second CRM","baseUrl":"http://localhost:5080/_mock","timeoutSeconds":10,"maxResponseBytes":1048576,"isActive":true}'
check "create destination"                    "201" "$(admin POST /api/admin/destinations "$NEW_DEST")"
check "duplicate destination is refused"      "409" "$(admin POST /api/admin/destinations "$NEW_DEST")"
NEW_OP='{"operationKey":"customer.lookup","destinationKey":"crm-second","httpMethod":"GET","pathTemplate":"/customers/{customerId}","isActive":true,"maxRequestBytes":65536,"requiresEncryption":false,"allowUnknownParameters":false,"parameters":[{"name":"customerId","isRequired":true,"dataType":"Integer","location":"Path","minValue":1}],"allowedHeaders":["Accept-Language"]}'
check "invalid operation is refused"          "400" "$(admin POST /api/admin/operations "$(echo "$NEW_OP" | jq -c '.httpMethod = "TRACE"')")"
check "create operation"                      "201" "$(admin POST /api/admin/operations "$NEW_OP")"
LOOKUP_VERSION="$(jq -r .rowVersion admin.json)"
check "new operation is forwarded"            "200 upstream" "$(call "$CALLER_KEY" '{"clientId":"customer.lookup","json":{"urlId":"crm-second","parameters":{"customerId":7}}}')"
RULES="{\"rowVersion\":\"$LOOKUP_VERSION\",\"parameters\":[{\"name\":\"customerId\",\"isRequired\":true,\"dataType\":\"Integer\",\"location\":\"Path\",\"minValue\":1,\"maxValue\":100}],\"allowedHeaders\":[]}"
check "replace rules of an operation"         "200" "$(admin PUT /api/admin/operations/customer.lookup/rules "$RULES")"
check "replace rules with stale rowversion"   "409" "$(admin PUT /api/admin/operations/customer.lookup/rules "$RULES")"
check "new rule is enforced"                  "400 proxy" "$(call "$CALLER_KEY" '{"clientId":"customer.lookup","json":{"urlId":"crm-second","parameters":{"customerId":500}}}')"

# --- optimistic concurrency on SQL Server rowversion
VERSION="$(curl -fs "$BASE/api/admin/operations/customer.get" -H "X-Api-Key: $ADMIN_KEY" | jq -r .rowVersion)"
put() { admin PUT /api/admin/operations/customer.get "{\"isActive\":$1,\"maxRequestBytes\":32768,\"timeoutSeconds\":null,\"rowVersion\":\"$VERSION\"}"; }
check "update with current rowversion"        "200" "$(put false)"
check "update with stale rowversion"          "409" "$(put true)"
check "disabled operation is rejected"        "403 proxy" "$(call "$CALLER_KEY" "$GET")"
check "admin api is closed to callers"        "403" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/api/admin/operations/customer.get" -H "X-Api-Key: $CALLER_KEY")"

# --- a body larger than the global limit, sent without Content-Length (chunked), on the real server
head -c 1300000 /dev/zero | tr '\0' 'x' > big.txt
check "oversized chunked body is rejected"    "413 proxy" "$(curl -s -o body.json -D headers.txt -w '%{http_code}' "$BASE/api/proxy/execute" \
  -H "X-Api-Key: $CALLER_KEY" -H 'Content-Type: application/json' -H 'Transfer-Encoding: chunked' --data-binary @big.txt) $(grep -i '^x-proxy-source:' headers.txt | tr -d '\r' | awk '{print $2}')"

# --- guessing API keys is throttled per client address
GUESSES=""
for i in $(seq 1 40); do
  GUESSES="$GUESSES $(curl -s -o /dev/null -w '%{http_code}' "$BASE/api/proxy/execute" -H "X-Api-Key: guess-$i" -H 'Content-Type: application/json' -d '{}')"
done
check "invalid api keys are throttled"        "yes" "$(echo "$GUESSES" | grep -q 429 && echo yes || echo no)"
check "valid key still works after the flood" "404 proxy" "$(call "$CALLER_KEY" '{"clientId":"customer.nothere","json":{"urlId":"crm-main","parameters":{}}}')"

# --- API description
check "swagger document describes the proxy endpoint" "true" "$(curl -fs "$BASE/swagger/v1/swagger.json" | jq -r '.paths["/api/proxy/execute"].post.requestBody.content | has("application/json")')"
check "wrong content type gets the proxy error"   "415 proxy" "$(curl -s -o body.json -D headers.txt -w '%{http_code}' "$BASE/api/proxy/execute" \
  -H "X-Api-Key: $CALLER_KEY" -H 'Content-Type: text/plain' -d "$GET") $(grep -i '^x-proxy-source:' headers.txt | tr -d '\r' | awk '{print $2}')"

# Audit records are written by a background worker: give it a moment.
sleep 3

# --- audit and configuration history in SQL Server
check "audit rows written"                    "13" "$(sql "$DB" "SELECT COUNT(*) FROM RequestLogs")"
check "forwarded requests audited"            "3"  "$(sql "$DB" "SELECT COUNT(*) FROM RequestLogs WHERE Forwarded = 1 AND Outcome = 'Success'")"
check "rejections audited as not forwarded"   "10" "$(sql "$DB" "SELECT COUNT(*) FROM RequestLogs WHERE Forwarded = 0 AND Outcome = 'Rejected'")"
check "configuration change recorded"         "1"  "$(sql "$DB" "SELECT COUNT(*) FROM ConfigurationHistory WHERE EntityType = 'Operation' AND ChangeType = 'Modified' AND ChangedBy = 'smoke-admin'")"
check "created operation recorded"            "1"  "$(sql "$DB" "SELECT COUNT(*) FROM ConfigurationHistory WHERE EntityType = 'Operation' AND ChangeType = 'Added' AND ChangedBy = 'smoke-admin'")"
check "key reference not in history"          "0"  "$(sql "$DB" "SELECT COUNT(*) FROM ConfigurationHistory WHERE Changes LIKE '%dev-crm-key%'")"

if [ "$FAILED" -ne 0 ]; then echo "Smoke test FAILED"; exit 1; fi
echo "Smoke test passed"

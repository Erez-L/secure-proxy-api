#!/usr/bin/env bash
# End-to-end smoke test of the API against a real SQL Server.
#
#   scripts/smoke-test.sh model    the application creates the schema (Development start-up)
#   scripts/smoke-test.sh script   the schema is created from database/schema.sql first
#
# Expects SQL Server in a Docker container: SQL_CONTAINER (container id or name) and SA_PASSWORD.
set -euo pipefail

MODE="${1:-model}"
: "${SQL_CONTAINER:?SQL_CONTAINER is required}"
: "${SA_PASSWORD:?SA_PASSWORD is required}"
DB="SecureProxySmoke"
BASE="http://localhost:5080"
FAILED=0

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
  docker exec "$SQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$SA_PASSWORD" -d "$DB" -b -i /tmp/schema.sql
  export Database__ApplyMigrationsOnStartup=false
fi

export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="$BASE"
export ConnectionStrings__ProxyDb="Server=localhost,1433;Database=$DB;User Id=sa;Password=$SA_PASSWORD;TrustServerCertificate=True;Encrypt=True"
# The variable name contains '-', which a shell cannot export, so it is passed through env.
KEY="$(openssl rand -base64 32)"

env "EncryptionKeys__dev-crm-key=$KEY" dotnet run --project src/SecureProxy.Api -c Release --no-build --no-launch-profile > api.log 2>&1 &
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

GET='{"clientId":"customer.get","json":{"urlId":"crm-main","parameters":{"customerId":42}}}'

check "valid operation is forwarded"          "200 upstream" "$(call dev-caller-key "$GET")"
check "upstream body is relayed"              "42"           "$(jq -r .id body.json)"
check "unknown parameter is rejected"         "400 proxy"    "$(call dev-caller-key '{"clientId":"customer.get","json":{"urlId":"crm-main","parameters":{"customerId":42,"debug":true}}}')"
check "unknown operation is rejected"         "404 proxy"    "$(call dev-caller-key '{"clientId":"customer.delete","json":{"urlId":"crm-main","parameters":{}}}')"
check "caller-supplied url is rejected"       "400 proxy"    "$(call dev-caller-key '{"clientId":"customer.get","url":"https://example.org","json":{"urlId":"crm-main","parameters":{"customerId":42}}}')"
check "missing api key is rejected"           "401 proxy"    "$(call '' "$GET")"

# Encrypted operation: build the envelope with the development helper, then execute it.
curl -fs "$BASE/_dev/encrypt" -H "X-Api-Key: dev-admin-key" -H "Content-Type: application/json" \
  -d '{"clientId":"customer.create","urlId":"crm-main","parameters":{"name":"Dana","age":30}}' > envelope.json
check "encrypted operation is forwarded"      "201 upstream" "$(call dev-caller-key "$(cat envelope.json)")"
check "decrypted body reached the upstream"   "Dana"         "$(jq -r .received.name body.json)"
jq '.json.encrypted.tag = "AAAAAAAAAAAAAAAAAAAAAA=="' envelope.json > tampered.json
check "tampered tag is rejected"              "400 proxy"    "$(call dev-caller-key "$(cat tampered.json)")"
check "plaintext to encrypted op is rejected" "400 proxy"    "$(call dev-caller-key '{"clientId":"customer.create","json":{"urlId":"crm-main","parameters":{"name":"Dana"}}}')"

# Optimistic concurrency on SQL Server rowversion.
VERSION="$(curl -fs "$BASE/api/admin/operations/customer.get" -H "X-Api-Key: dev-admin-key" | jq -r .rowVersion)"
put() {  # put <isActive>
  curl -s -o put.json -w "%{http_code}" -X PUT "$BASE/api/admin/operations/customer.get" \
    -H "X-Api-Key: dev-admin-key" -H "Content-Type: application/json" \
    -d "{\"isActive\":$1,\"maxRequestBytes\":32768,\"timeoutSeconds\":null,\"rowVersion\":\"$VERSION\"}"
}
check "update with current rowversion"        "200" "$(put false)"
check "update with stale rowversion"          "409" "$(put true)"
check "disabled operation is rejected"        "403 proxy" "$(call dev-caller-key "$GET")"
check "admin api is closed to callers"        "403" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/api/admin/operations/customer.get" -H 'X-Api-Key: dev-caller-key')"

# A body larger than the global limit, sent without Content-Length (chunked), on the real server.
head -c 1300000 /dev/zero | tr '\0' 'x' > big.txt
check "oversized chunked body is rejected"    "413 proxy" "$(curl -s -o body.json -D headers.txt -w '%{http_code}' "$BASE/api/proxy/execute" \
  -H 'X-Api-Key: dev-caller-key' -H 'Content-Type: application/json' -H 'Transfer-Encoding: chunked' --data-binary @big.txt) $(grep -i '^x-proxy-source:' headers.txt | tr -d '\r' | awk '{print $2}')"

# Guessing API keys is throttled per client address.
GUESSES=""
for i in $(seq 1 40); do
  GUESSES="$GUESSES $(curl -s -o /dev/null -w '%{http_code}' "$BASE/api/proxy/execute" -H "X-Api-Key: guess-$i" -H 'Content-Type: application/json' -d '{}')"
done
check "invalid api keys are throttled"        "yes" "$(echo "$GUESSES" | grep -q 429 && echo yes || echo no)"
check "valid key still works after the flood" "404 proxy" "$(call dev-caller-key '{"clientId":"customer.nothere","json":{"urlId":"crm-main","parameters":{}}}')"

# Audit records are written by a background worker: give it a moment.
sleep 3

# Audit and configuration history in SQL Server.
check "audit rows written"                    "10" "$(sql "$DB" "SELECT COUNT(*) FROM RequestLogs")"
check "forwarded requests audited"            "2" "$(sql "$DB" "SELECT COUNT(*) FROM RequestLogs WHERE Forwarded = 1 AND Outcome = 'Success'")"
check "rejections audited as not forwarded"   "8" "$(sql "$DB" "SELECT COUNT(*) FROM RequestLogs WHERE Forwarded = 0 AND Outcome = 'Rejected'")"
check "configuration change recorded"         "1" "$(sql "$DB" "SELECT COUNT(*) FROM ConfigurationHistory WHERE EntityType = 'Operation' AND ChangeType = 'Modified' AND ChangedBy = 'dev-admin'")"
check "key reference not in history"          "0" "$(sql "$DB" "SELECT COUNT(*) FROM ConfigurationHistory WHERE Changes LIKE '%dev-crm-key%'")"

if [ "$FAILED" -ne 0 ]; then echo "Smoke test FAILED"; exit 1; fi
echo "Smoke test passed"

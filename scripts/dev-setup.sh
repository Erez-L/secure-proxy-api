#!/usr/bin/env bash
# One-time local setup. Generates development secrets so that none are stored in the repository:
#   .env           SQL Server password and two API keys (git-ignored; also read by docker compose)
#   user-secrets   connection string, API key hashes and the development encryption key
# Safe to run again: existing values in .env and the existing encryption key are kept.
set -euo pipefail
cd "$(dirname "$0")/.."
PROJECT=src/SecureProxy.Api

if [ ! -f .env ]; then
  {
    echo "MSSQL_SA_PASSWORD=Dev_$(openssl rand -hex 12)aA1"
    echo "PROXY_CALLER_KEY=$(openssl rand -hex 24)"
    echo "PROXY_ADMIN_KEY=$(openssl rand -hex 24)"
  } > .env
  echo "Created .env"
fi
set -a; . ./.env; set +a

sha256() { printf '%s' "$1" | openssl dgst -sha256 -hex | awk '{print $NF}'; }
secret() { dotnet user-secrets set "$1" "$2" --project "$PROJECT" > /dev/null; }

secret "ConnectionStrings:ProxyDb" "Server=localhost,1433;Database=SecureProxy;User Id=sa;Password=$MSSQL_SA_PASSWORD;TrustServerCertificate=True;Encrypt=True"

# Only the SHA-256 of each API key is given to the application.
secret "Auth:ApiKeys:0:Name" "dev-caller"
secret "Auth:ApiKeys:0:Sha256" "$(sha256 "$PROXY_CALLER_KEY")"
secret "Auth:ApiKeys:0:Role" "caller"
secret "Auth:ApiKeys:0:AllowedOperations:0" "customer.*"
secret "Auth:ApiKeys:1:Name" "dev-admin"
secret "Auth:ApiKeys:1:Sha256" "$(sha256 "$PROXY_ADMIN_KEY")"
secret "Auth:ApiKeys:1:Role" "admin"
secret "Auth:ApiKeys:1:AllowedOperations:0" "*"

if ! dotnet user-secrets list --project "$PROJECT" | grep -q '^EncryptionKeys:dev-crm-key'; then
  secret "EncryptionKeys:dev-crm-key" "$(openssl rand -base64 32)"
fi

echo "Development secrets are set."
echo "API keys are in .env (PROXY_CALLER_KEY, PROXY_ADMIN_KEY). Load them into a shell with:  set -a; . ./.env; set +a"

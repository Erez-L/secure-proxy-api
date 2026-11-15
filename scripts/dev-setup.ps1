# One-time local setup for Windows PowerShell / PowerShell 7. Same result as dev-setup.sh:
#   .env           SQL Server password and two API keys (git-ignored; also read by docker compose)
#   user-secrets   connection string, API key hashes and the development encryption key
# Safe to run again: existing values in .env and the existing encryption key are kept.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$project = 'src/SecureProxy.Api'

function New-Hex([int]$bytes) {
    $buffer = New-Object byte[] $bytes
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
    return -join ($buffer | ForEach-Object { $_.ToString('x2') })
}
function Get-Sha256([string]$text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))
    return -join ($hash | ForEach-Object { $_.ToString('x2') })
}
function Set-Secret([string]$name, [string]$value) {
    dotnet user-secrets set $name $value --project $project | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not set user secret $name" }
}

if (-not (Test-Path '.env')) {
    @(
        "MSSQL_SA_PASSWORD=Dev_$(New-Hex 12)aA1"
        "PROXY_CALLER_KEY=$(New-Hex 24)"
        "PROXY_ADMIN_KEY=$(New-Hex 24)"
    ) | Set-Content -Path '.env' -Encoding ascii
    Write-Host 'Created .env'
}

$settings = @{}
Get-Content '.env' | Where-Object { $_ -match '^([^#=]+)=(.*)$' } | ForEach-Object { $settings[$Matches[1].Trim()] = $Matches[2].Trim() }

Set-Secret 'ConnectionStrings:ProxyDb' "Server=localhost,1433;Database=SecureProxy;User Id=sa;Password=$($settings['MSSQL_SA_PASSWORD']);TrustServerCertificate=True;Encrypt=True"

# Only the SHA-256 of each API key is given to the application.
Set-Secret 'Auth:ApiKeys:0:Name' 'dev-caller'
Set-Secret 'Auth:ApiKeys:0:Sha256' (Get-Sha256 $settings['PROXY_CALLER_KEY'])
Set-Secret 'Auth:ApiKeys:0:Role' 'caller'
Set-Secret 'Auth:ApiKeys:0:AllowedOperations:0' 'customer.*'
Set-Secret 'Auth:ApiKeys:1:Name' 'dev-admin'
Set-Secret 'Auth:ApiKeys:1:Sha256' (Get-Sha256 $settings['PROXY_ADMIN_KEY'])
Set-Secret 'Auth:ApiKeys:1:Role' 'admin'
Set-Secret 'Auth:ApiKeys:1:AllowedOperations:0' '*'

$existing = dotnet user-secrets list --project $project
if (-not ($existing | Select-String -SimpleMatch 'EncryptionKeys:dev-crm-key')) {
    $key = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($key)
    Set-Secret 'EncryptionKeys:dev-crm-key' ([Convert]::ToBase64String($key))
}

Write-Host 'Development secrets are set.'
Write-Host 'API keys are in .env (PROXY_CALLER_KEY, PROXY_ADMIN_KEY).'

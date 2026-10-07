# Creates the .env file that docker-compose reads, filled with fresh random secrets.
# Run it once:   powershell -ExecutionPolicy Bypass -File scripts\make-env.ps1
# It will NOT overwrite an existing .env (that would lock you out of your own database).

$envFile = Join-Path (Split-Path $PSScriptRoot -Parent) ".env"

if (Test-Path $envFile) {
    Write-Host ".env already exists, leaving it alone: $envFile"
    exit 0
}

function New-Secret([int]$bytes) {
    # Cryptographically secure random bytes, made URL-safe.
    $data = New-Object byte[] $bytes
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($data)
    return ([Convert]::ToBase64String($data)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

$lines = @(
    "# Secrets for docker-compose. NEVER commit this file or share it.",
    "POSTGRES_PASSWORD=$(New-Secret 24)",
    "JWT_KEY=$(New-Secret 48)"
)

Set-Content -Path $envFile -Value $lines -Encoding ascii
Write-Host "Created $envFile with new random secrets."

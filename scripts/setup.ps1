# One-step developer setup for Windows.
#   pwsh ./scripts/setup.ps1           install dependencies, start the database, run migrations
#   pwsh ./scripts/setup.ps1 -NoDb     skip the database steps
param([switch]$NoDb)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

function Say($text) { Write-Host "`n$text" -ForegroundColor Cyan }

Say 'Checking Node.js (>= 22.18)'
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw 'Node.js is not installed. Install Node 22.18+ (for example: winget install OpenJS.NodeJS.LTS).'
}
node -e 'const [a,b]=process.versions.node.split(".").map(Number); if (a<22||(a===22&&b<18)) process.exit(1)'
if ($LASTEXITCODE -ne 0) { throw "Node $(node --version) is too old; NimPad needs 22.18+." }

Say 'Checking pnpm'
if (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) { corepack enable }

Say 'Checking .NET SDK (version from global.json)'
$sdk = (Get-Content global.json | ConvertFrom-Json).sdk.version
$want = [version]$sdk
# global.json uses rollForward=latestFeature: an installed SDK qualifies when it has the same major.minor and a
# feature band/patch (the third component, e.g. 401) at least as high as the pinned one. Previews don't count.
$haveSdk = $false
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    foreach ($line in (dotnet --list-sdks)) {
        $v = ($line -split ' ')[0]
        if ($v -match '-') { continue }
        $installed = [version]$v
        if ($installed.Major -eq $want.Major -and $installed.Minor -eq $want.Minor -and $installed.Build -ge $want.Build) {
            $haveSdk = $true
        }
    }
}
if (-not $haveSdk) {
    Say "Installing .NET SDK $sdk"
    $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $installer
    & $installer -Version $sdk
    $env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
}

Say 'Installing JavaScript dependencies'
pnpm install --frozen-lockfile

Say 'Restoring and building the desktop solution'
dotnet build apps/windows/NimPad.slnx

if (-not (Test-Path .env)) { Copy-Item .env.example .env; Write-Host 'Created .env from .env.example' }

if (-not $NoDb) {
    Say 'Starting PostgreSQL'
    if ((Get-Command docker -ErrorAction SilentlyContinue) -and (docker info 2>$null)) {
        docker compose up -d --wait db
    } else {
        Write-Host 'Docker is not available. Make sure PostgreSQL 16 is reachable as described in .env (DATABASE_URL_MIGRATE).'
    }
    Say 'Running database migrations'
    pnpm db:migrate
}

Say 'Done.'
Write-Host @'
Next steps:
  dotnet run --project apps/windows/src/NimPad.App     launch NimPad
  pnpm dev:api                                         start the API on http://127.0.0.1:8787
  pnpm test                                            run the backend tests
'@

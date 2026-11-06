<#
.SYNOPSIS
    Set up the dev environment from a fresh clone of totk-vscode. Needs Node.js and the .NET 10 SDK.
.DESCRIPTION
    1. Initializes git submodules
    2. Installs Node dependencies (root)
    3. Builds the C# host for this machine
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Push-Location $root
try {
    # ── 1. Git submodules ──────────────────────────────────────────────
    Write-Host "`n=== Initializing git submodules ===" -ForegroundColor Cyan
    git submodule update --init --recursive
    if ($LASTEXITCODE -ne 0) { throw "git submodule update failed" }

    # ── 2. Node dependencies ───────────────────────────────────────────
    Write-Host "`n=== Installing Node dependencies (root) ===" -ForegroundColor Cyan
    npm install
    if ($LASTEXITCODE -ne 0) { throw "npm install failed" }

    # ── 3. C# host ─────────────────────────────────────────────────────
    Write-Host "`n=== Building the C# host ===" -ForegroundColor Cyan
    node scripts/build-host.js
    if ($LASTEXITCODE -ne 0) { throw "host build failed" }

    Write-Host "`n=== Dev Environment Setup complete ===" -ForegroundColor Green
} finally {
    Pop-Location
}

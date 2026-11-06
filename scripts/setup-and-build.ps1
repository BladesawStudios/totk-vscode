<#
.SYNOPSIS
    Full setup and build from a fresh clone of totk-vscode. Needs Node.js and the .NET 10 SDK.
.DESCRIPTION
    1. Initializes git submodules
    2. Installs Node dependencies (root)
    3. Compiles the extension
    4. Builds the C# host and packages a .vsix for this machine, or only builds the host with -SkipVsix
.PARAMETER SkipVsix
    Skip the final .vsix packaging step.
#>
param(
    [switch]$SkipVsix
)

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

    # ── 3. Compile ─────────────────────────────────────────────────────
    Write-Host "`n=== Compiling extension ===" -ForegroundColor Cyan
    npm run compile
    if ($LASTEXITCODE -ne 0) { throw "compile failed" }

    # ── 4. Host and VSIX ───────────────────────────────────────────────
    if (-not $SkipVsix) {
        Write-Host "`n=== Packaging .vsix ===" -ForegroundColor Cyan
        node scripts/package-vsix.js
        if ($LASTEXITCODE -ne 0) { throw "packaging failed" }
    } else {
        Write-Host "`n=== Building the C# host ===" -ForegroundColor Cyan
        node scripts/build-host.js
        if ($LASTEXITCODE -ne 0) { throw "host build failed" }
    }

    Write-Host "`n=== Setup complete ===" -ForegroundColor Green
} finally {
    Pop-Location
}

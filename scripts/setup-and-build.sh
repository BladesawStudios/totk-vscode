#!/usr/bin/env bash
#
# Full setup and build from a fresh clone of totk-vscode. Needs Node.js and the .NET 10 SDK.
#
#   1. Initializes git submodules
#   2. Installs Node dependencies (root)
#   3. Compiles the extension
#   4. Builds the C# host and packages a .vsix for this machine, or only builds the host with --skip-vsix
#
# Usage:
#   ./scripts/setup-and-build.sh                      # full build + vsix
#   ./scripts/setup-and-build.sh --skip-vsix          # build without vsix

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SKIP_VSIX=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --skip-vsix) SKIP_VSIX=true; shift ;;
        *)           echo "Unknown option: $1"; exit 1 ;;
    esac
done

cd "$ROOT"

# ── 1. Git submodules ──────────────────────────────────────────────
echo ""
echo "=== Initializing git submodules ==="
git submodule update --init --recursive

# ── 2. Node dependencies ───────────────────────────────────────────
echo ""
echo "=== Installing Node dependencies (root) ==="
npm install

# ── 3. Compile ─────────────────────────────────────────────────────
echo ""
echo "=== Compiling extension ==="
npm run compile

# ── 4. Host and VSIX ───────────────────────────────────────────────
if [[ "$SKIP_VSIX" == false ]]; then
    echo ""
    echo "=== Packaging .vsix ==="
    node scripts/package-vsix.js
else
    echo ""
    echo "=== Building the C# host ==="
    node scripts/build-host.js
fi

echo ""
echo "=== Setup complete ==="

#!/usr/bin/env bash
#
# Set up the dev environment from a fresh clone of totk-vscode. Needs Node.js and the .NET 10 SDK.
#
#   1. Initializes git submodules
#   2. Installs Node dependencies (root)
#   3. Builds the C# host for this machine
#
# Usage:
#   ./scripts/init-dev-env.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

cd "$ROOT"

# ── 1. Git submodules ──────────────────────────────────────────────
echo ""
echo "=== Initializing git submodules ==="
git submodule update --init --recursive

# ── 2. Node dependencies ───────────────────────────────────────────
echo ""
echo "=== Installing Node dependencies (root) ==="
npm install

# ── 3. C# host ─────────────────────────────────────────────────────
echo ""
echo "=== Building the C# host ==="
node scripts/build-host.js

echo ""
echo "=== Dev Environment Setup complete ==="

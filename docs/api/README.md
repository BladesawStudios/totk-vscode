# TKVSC Addon API Documentation

## DISCLAIMER
API docs are currently 100% AI written based on analysis of the codebase, some things may be incorrect. I will rewrite the docs when I have time (likely on full release).

Programmatic API for companion VS Code extensions that extend [TKVSC](https://github.com/TKVSC-Team/totk-vscode).

| Document | Description |
|----------|-------------|
| **[v1.md](v1.md)** | **Complete API reference** (current) |
| [CHANGELOG.md](CHANGELOG.md) | API version history |
| [addon-development.md](addon-development.md) | Addon author guide and patterns |

**Extension ID:** `TKVSC-Team.totk-vscode`  
**Current API version:** `1` (`api.apiVersion`)

Source of truth for TypeScript types:

- [`src/api/types.ts`](../../src/api/types.ts) - `TkvscApi`, `TkvscBridgeAccess`
- [`src/api/constants.ts`](../../src/api/constants.ts) - view IDs, context values
- [`src/formatRegistry.ts`](../../src/formatRegistry.ts) - format / bridge handler types
- [`src/gameProfile.ts`](../../src/gameProfile.ts) - game profile types
- [`src/projectAdapters/types.ts`](../../src/projectAdapters/types.ts) - project adapter types

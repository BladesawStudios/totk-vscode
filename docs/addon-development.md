# TKVSC Addon Development

TKVSC supports **companion VS Code extensions** (addons) that extend the editor with tools, custom editors, and (in future phases) game-specific formats.

This guide covers how addons integrate with core TKVSC. For the programmatic API surface, see the versioned references under [docs/api/](api/).

## Requirements

- TKVSC installed and enabled (`TKVSC-Team.totk-vscode`)
- Your addon declares a dependency on core:

```json
{
  "extensionDependencies": [
    "TKVSC-Team.totk-vscode"
  ]
}
```

- Pin a minimum TKVSC version in your addon README or `package.json` `engines` once we publish semver guarantees. Today, match the API version your addon targets (see below).

## Getting the API

```typescript
import * as vscode from 'vscode';
import type { TkvscApi } from 'totk-vscode'; // types: see docs/api/v1.md until @tkvsc/api is published

export async function activate(context: vscode.ExtensionContext) {
  const ext = vscode.extensions.getExtension('TKVSC-Team.totk-vscode');
  const api = await ext?.activate() as TkvscApi | undefined;
  if (!api) {
    return;
  }

  if (api.apiVersion !== 1) {
    void vscode.window.showErrorMessage(
      `This addon requires TKVSC API v1 (got v${api.apiVersion}).`,
    );
    return;
  }

  // Wait for projects tree + core services, or run immediately if already ready:
  api.onDidReady(() => {
    // safe to assume archive tree is registered
  });
}
```

## Integration models

| Model | When to use |
|-------|-------------|
| **Standard VS Code contributes** | Commands, menus, custom editors, grammars, settings — no TKVSC API required beyond optional helpers |
| **`contributes.tkvsc` manifest** | Declarative file formats, AAMP extensions, Python bridge handlers — scanned at core startup |
| **TKVSC programmatic API** | Project tree context, raw file I/O inside archives, Python bridge access, runtime format registration |

## Declarative formats (`contributes.tkvsc`)

Declare formats in your addon `package.json`. Core merges them with built-in TOTK formats and writes a handler manifest for the Python bridge.

```json
{
  "contributes": {
    "tkvsc": {
      "formats": [
        {
          "extensions": ["ainb"],
          "handler": "ainb",
          "language": "yaml",
          "editable": false
        }
      ],
      "bridgeHandlers": [
        {
          "kind": "ainb",
          "modulePath": "./python/ainb_io.py"
        }
      ]
    }
  }
}
```

Python module contract (addon-provided):

```python
def read_content(file_data: bytes, logical_path: str, romfs_path: str = "") -> str: ...

def write_bytes(original: bytes, editor_text: str, logical_path: str, romfs_path: str = "") -> bytes: ...
```

Function names default to `read_content` / `write_bytes` and can be overridden per handler.

You can also register at runtime:

```typescript
api.registerFormatHandler({ extensions: ['ainb'], handler: 'ainb', language: 'yaml', editable: false });
api.registerBridgeHandler({
  kind: 'ainb',
  modulePath: path.join(context.extensionPath, 'python', 'ainb_io.py'),
});
```

See [api/v1.md](api/v1.md) for full field reference.

## Common patterns

### Context menu on a project root

```json
"contributes": {
  "commands": [{
    "command": "my-addon.packageAsTkcl",
    "title": "Package as TKCL"
  }],
  "menus": {
    "view/item/context": [{
      "command": "my-addon.packageAsTkcl",
      "when": "view == totk-editor.archives && viewItem == archiveRoot",
      "group": "2_package@1"
    }]
  }
}
```

Use `api.views.archives` and `api.contextValues.archiveRoot` in code instead of hardcoding strings when possible (same values; see [API v1](api/v1.md)).

### Custom editor with archive read/write

```typescript
const bytes = await api.readRawBytes(document.uri);
// ... parse, edit in webview ...
await api.writeRawBytes(document.uri, serialized);
```

Works for `sarc://`, `totk-disk://`, and `file://` URIs that point inside nested archives.

## API documentation

| Document | Description |
|----------|-------------|
| [api/v1.md](api/v1.md) | **Current** — API reference (Phase 1 + Phase 2 format registration) |
| [api/CHANGELOG.md](api/CHANGELOG.md) | API version history |

When new API versions ship, a new `vN.md` is added. Breaking changes bump `api.apiVersion`.

## URI schemes (stable)

Addons should treat these schemes as the TKVSC virtual file system:

| Scheme | Writable | Description |
|--------|----------|-------------|
| `sarc` | Yes (in projects) | Project/archive browser |
| `totk-disk` | Yes | On-disk project files with bridge conversion on save |
| `totk-dump` | No | Read-only game dump mirror |

## Settings

User-facing settings use the `TKVSC.*` namespace (see [settings.md](settings.md)). Addon-specific settings should use your own prefix, e.g. `myAddon.cliPath`.

## Further reading

- [commands.md](commands.md) — stable `totk-editor.*` command IDs for menu `when` clauses
- [settings.md](settings.md) — core configuration

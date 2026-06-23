# TKVSC Extension API Changelog

Programmatic API changes for addon extensions consuming `TKVSC-Team.totk-vscode` exports.

Convention: **`api.apiVersion`** increments on breaking changes to the `TkvscApi` surface. Additive methods/properties within a major version do not require a bump (document them here under the same version).

Reference docs: [v1.md](v1.md)

---

## v1 (Phase 1)

**Added**

- `activate()` returns `TkvscApi`
- `apiVersion`, `extensionId`
- `views`, `contextValues` — stable IDs for menus
- `onDidReady` event
- `resolveProjectRoot(item)`
- `readRawBytes(uri)`, `writeRawBytes(uri, data)`
- `getBridge()` — `bridgePath`, `getPython`, `getBridgeEnv`, `runBridgeJsonAsync`
- `getProjectRoots()`

**Notes**

- Command IDs (`totk-editor.*`) and URI schemes are stable contracts but are not part of `apiVersion`; see [commands.md](../commands.md).
- Settings namespace is `TKVSC.*` (legacy `totk-editor.*` settings keys are no longer read in core paths updated during Phase 1).

---

## v1 — Phase 2 (additive)

**Added**

- `registerFormatHandler(registration)` — programmatic format registration
- `registerBridgeHandler(registration)` — Python bridge handler registration
- `contributes.tkvsc` manifest scanning (`formats`, `aampExtensions`, `bridgeHandlers`)
- `FormatRegistry` — merged built-in + manifest + API format map
- Handler manifest (`tkvsc-handler-manifest.json`) written to global storage; Python bridge loads addon handlers dynamically via `TKVSC_HANDLER_MANIFEST`

**Notes**

- `api.apiVersion` remains `1` (additive only).
- API `registerFormatHandler` / `registerBridgeHandler` disposables do not unregister handlers in v1.
- AAMP extension list refreshes on `TKVSC.extraAampExtensions` setting change.

---

## Template for future entries

```markdown
## vN

**Breaking**

- ...

**Added**

- ...

**Deprecated**

- ...
```

# TKVSC Extension API Changelog

Programmatic API changes for addon extensions consuming `BladesawStudios.totk-vscode` exports.

Convention: **`api.apiVersion`** increments on breaking changes to the `TkvscApi` surface. Additive methods/properties within a major version do not require a bump (document them here under the same version).

Reference docs: [v1.md](v1.md)

---

## Value names

**Added**

- Game profile field `valueNamesPath`: a file of names, one per line, for values the game stores as the MurmurHash3 (seed 0) of a name, such as enum values. A `!u` BYML value equal to a listed name's hash shows in the text editor as `!hash Name`.
- `!hash Name` in BYML text, for any game, saves as the unsigned hash of `Name`, listed or not.

---

## Project folders per game

**Changed**

- Each game has its own list of project folders, setting `TKVSC.projectFolders` (`{ "<gameId>": ["<folder>", ...] }`), edited in settings. New projects are created in the first one. A game with none uses `<TKVSC.projectsPath>/<gameId>/`. Projects already added stay where they are.

---

## BARS tree items

**Changed**

- `.bars` files in the projects tree now have the context values `archiveBars` and `archiveVirtualBars` (in `TKVSC_ARCHIVE_CONTEXT`) instead of `archiveFile` and `archiveVirtualFile`, so their right-click menu can offer exporting and replacing tracks. An addon that matches `archiveFile` for a `.bars` file needs to match these too.

---

## Game support

**Added**

- [Host plugins](host-plugins.md): a game's add-on can ship a .NET DLL, named by the profile's `hostPlugin`, that gives the host formats, compression, archive formats and commands.
- Game profile fields `compressedSuffixes`, `zsDicPath`, `hashNamesPath`, `aampNameTable`, `aampHashNamesPath` and `xlinkVersions`.
- SARC archives on disk compressed with Yaz0 now open, and stay Yaz0 when saved.

**Changed**

- The host now uses the profile's `archivePatterns`, as the extension already did. A profile that leaves out an extension (Splatoon 3 leaves out `.bkres`) no longer has it opened as an archive by the host.
- An archive pattern or extension now also matches with a compressed suffix added, so a `\.spack$` pattern covers `data.spack.zs`.
- The hex editor now treats a file as compressed only when its name ends with a compressed suffix. It used to match `.zs` anywhere in the name, which included `.zsdic` files.
- TotK's flag hash names now apply only to TotK. Another game gets them only from its own `hashNamesPath`.
- A profile with the id `botw` no longer gets BotW's AAMP names and XLink versions automatically. Set `aampNameTable` and `xlinkVersions` (see [Adding a game](host-plugins.md)).

---

## Python handlers removed

**Removed** (no add-on used them)

- `registerBridgeHandler(registration)` and the `bridgeHandlers` entry of `contributes.tkvsc`; the extension no longer runs Python, so add-on Python modules are not loaded. A format of your own is read and written by your own custom editor (`registerFormatHandler` with `editable: false`, then `readRawBytes` / `writeRawBytes`).
- `handlers` in `tkvsc-handler-manifest.json`.
- `getBridge()` gains `getHost()`, the path of the TKVSC host. `getPython()` is a deprecated alias of it, and `bridgePath` is always empty and ignored (the signature of `runBridgeJsonAsync` is unchanged).

---

## v1 (Phase 1)

**Added**

- `activate()` returns `TkvscApi`
- `apiVersion`, `extensionId`
- `views`, `contextValues` - stable IDs for menus
- `onDidReady` event
- `resolveProjectRoot(item)`
- `readRawBytes(uri)`, `writeRawBytes(uri, data)`
- `getBridge()` - `bridgePath`, `getPython`, `getBridgeEnv`, `runBridgeJsonAsync`
- `getProjectRoots()`

**Notes**

- Command IDs (`totk-editor.*`) and URI schemes are stable contracts but are not part of `apiVersion`; see [commands.md](../commands.md).
- Settings namespace is `TKVSC.*` (legacy `totk-editor.*` settings keys are no longer read in core paths updated during Phase 1).

---

## v1 - Phase 2 (additive)

**Added**

- `registerFormatHandler(registration)` - programmatic format registration
- `registerBridgeHandler(registration)` - Python bridge handler registration
- `contributes.tkvsc` manifest scanning (`formats`, `aampExtensions`, `bridgeHandlers`)
- `FormatRegistry` - merged built-in + manifest + API format map
- Handler manifest (`tkvsc-handler-manifest.json`) written to global storage; Python bridge loads addon handlers dynamically via `TKVSC_HANDLER_MANIFEST`

**Notes**

- `api.apiVersion` remains `1` (additive only).
- API `registerFormatHandler` / `registerBridgeHandler` disposables do not unregister handlers in v1.
- AAMP extension list refreshes on `TKVSC.extraAampExtensions` setting change.

---

## v1 - Phase 3 (additive)

**Added**

- `registerGameProfile(registration)` - game profile registration
- `getActiveGameProfile()`, `getGameProfile(gameId)`
- `contributes.tkvsc.gameProfile` and `archivePatterns` manifest fields
- `GameProfile` registry - RomFS sentinel, compression backend, per-game settings key
- `ArchiveRegistry` - per-game archive file patterns
- Per-game index paths: `globalStorage/indexes/{gameId}/` (auto-migrates legacy TotK indexes)
- Python compression backend dispatch (`totk-zstd`, `plain-zstd-yaz0`)
- Bridge env: `TKVSC_ROMFS`, `TKVSC_GAME_ID`, `TKVSC_COMPRESSION_BACKEND`, `TKVSC_ARCHIVE_EXTENSIONS`
- Setting: `TKVSC.activeGameId` (default `totk`)

**Notes**

- `api.apiVersion` remains `1`.
- TotK built-in profile: [`config/games/totk.json`](../../config/games/totk.json).
- `TKVSC.romfsPath` remains the TotK dump path; game addons use `TKVSC.<gameId>.romfsPath` via `romfsSettingsKey`.
- Canonical save propagation is disabled when `indexing.enableCanonicalPaths` is `false`.

---

## v1 - Phase 4 (additive)

**Added**

- `registerProjectAdapter(adapter)` - pluggable mod project layouts
- `detectProjectAdapter(projectRootPath)`, `getProjectAdapters()`
- `ProjectAdapter` interface + built-in `TkmmProjectAdapter` (`id: 'tkmm'`)
- Projects tree, add-to-option, dump-to-project, and `resolveProjectRoot()` delegate to adapter registry
- `importProjectsFromAdapters()` - merges import paths from all adapters with `importProjects()`

**Notes**

- `api.apiVersion` remains `1`.
- TKMM tree `contextValue` strings (`tkmmOptionsRoot`, etc.) unchanged - they are defined on `TkmmProjectAdapter.contextValues`.
- `tkmmOptions.ts` is a thin deprecated re-export; new code should use the adapter registry.

---

## v1 - Pre-merge fixes (additive / behavioral)

**Fixed**

- `onDidReady` replays for dependent addons that subscribe after `activate()` resolves
- `contributes.tkvsc.archivePatterns` at manifest root merges onto `gameProfile.archivePatterns`
- `getDiskArchivePath()` uses per-game archive patterns instead of a hardcoded TotK extension list
- `getProjectRoots()` returns logical active project paths (multi-mod aware)
- `detectProjectAdapterAsync()` added for async `isProjectRoot` adapters
- `TKVSC.activeGameId` changes refresh the archive pattern registry
- `archiveFsCommands` uses adapter `contextValues` instead of hardcoded TKMM strings
- Manifest parsing validates `contributes.tkvsc` shape instead of unchecked casts

**Added exports** (`src/api/index.ts`): `GameIndexingConfig`, `ProjectOptionPickResult`, `TkvscReadyEmitter`

---

## v1 - Per-project gameId (additive)

**Added**

- `.tkvsc` `gameId` field (defaults to `totk` when missing)
- View-title command `totk-editor.selectActiveGame` on Your Projects and Game Dump
- Your Projects / `getProjectRoots()` filtered to projects whose `gameId` matches `TKVSC.activeGameId`
- New/imported projects stamp the current active game into `.tkvsc`; existing stored roots migrate to `totk`

**Notes**

- Changing the active game via the picker updates `TKVSC.activeGameId` globally; both sidebars stay in sync
- Manual edits to `.tkvsc` `gameId` are preserved (never overwritten on import/touch)

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

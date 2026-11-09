# Contributing to TKVSC

Thanks for your interest in contributing! Here's what you need to know.

## Getting started

1. Fork the repository and clone your fork with submodules (`git clone --recurse-submodules`, or run `git submodule update --init --recursive` afterwards). The C# format libraries in `host/libs/` are submodules.
2. Install Node.js and the .NET 10 SDK, then run the script to set up the development environment: `scripts/init-dev-env.ps1` (Windows) or `scripts/init-dev-env.sh` (Linux/macOS)
3. Open the project in VS Code and press `F5` to launch the extension in a dev host window

## Project layout

| Path | What lives there |
| --- | --- |
| `src/` | The extension, in TypeScript (bundled with esbuild into `dist/`) |
| `src/api/` | The public API other addon extensions call; it is versioned and documented (see below) |
| `host/src/TkvscHost/` | The C# host (`tkvsc-host`) that does all file reading, writing and conversion. The extension calls it as a child process and gets JSON back |
| `host/src/TkvscHost.PluginApi/` | The contract game plugins compile against |
| `host/tests/` | Tests for the host |
| `host/libs/` | Format libraries (SarcSharp, BymlSharp, ...), each a git submodule with its own repository |
| `config/`, `syntaxes/`, `icons/`, `vendor/` | Bundled data: game profiles, language definitions, icons, hash lists |
| `docs/` | User and addon documentation, plus release notes in `docs/release-notes/` |

Format parsing and file manipulation belong in the C# host. The TypeScript side is VS Code UI (editors, trees, commands) and the bridge to the host.

## Building and checking

```bash
npm run compile        # type check + lint + bundle
npm run check-types    # tsc --noEmit
npm run lint           # eslint src
npm run fix            # eslint src --fix
npm run build-host     # publish the C# host for this machine
dotnet test host/TkvscHost.slnx
npm run build          # package a VSIX
```

CI runs `lint`, `check-types` and `dotnet test`, then builds a VSIX for every platform. Run those yourself before opening a PR, and note that CI skips runs that only touch Markdown, `docs/` or `graphics/`.

## Before submitting a PR

- Make sure the extension builds: `npm run build`
- Run `npm run fix`, then confirm `npm run lint` and `npm run check-types` are clean
- If you touched `host/`, run `dotnet test host/TkvscHost.slnx`
- Keep changes focused - one concern per PR
- Reference any related issue in your PR description (e.g. `Closes #123`)
- Don't include unrelated formatting or whitespace changes
- Update the docs when behavior changes (see [Documentation](#documentation))

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/) where it makes sense: `feat(recipes): ...`, `fix(bfres): ...`, `chore(deps): ...`. Keep the subject short and in the imperative.

## Code style

Follow the existing patterns in the file you're editing. There is no auto-formatter, so match what is around you.

### TypeScript (`src/`)

- 4-space indentation, single quotes, semicolons. ESLint enforces `semi`, `curly` (always use braces), `eqeqeq` (use `===`), `no-throw-literal` (throw `Error`s) and camelCase/PascalCase for imports.
- `strict` mode is on. Avoid `any`; use `unknown` and narrow it.
- Use named exports. The codebase uses no default exports.
- Filenames are camelCase (`archiveTree.ts`), and one file should have one clear responsibility. If a file is growing past a few hundred lines, look for something to split out (`extension.ts` and `hexEditor.ts` are already too large; don't make them bigger without a reason).
- Log through `logger` from `src/logger.ts` (`logger.debug/info/warn/error`) so output lands in the TKVSC output channel and respects the `logLevel` setting. Don't add `console.log`.
- Call the host through `src/bridge.ts` rather than spawning it yourself.
- Register commands under the `totk-editor.` prefix and declare them in `package.json`.
- Comment the *why*, using `/** ... */` on exported functions and non-obvious logic. Skip comments that restate the code.

### C# (`host/`)

- .NET 10, `Nullable` and `ImplicitUsings` enabled, `LangVersion` latest. Use file-scoped namespaces and 4-space indentation.
- Commands are static classes with a `Register(Dictionary<string, CommandHandler>)` method (see `Commands/`). New commands are registered there by kebab-case name and return JSON.
- Failures are reported as `{"error", "traceback"}` by the host, so throw exceptions with a clear message rather than returning error codes.
- Settings come from the environment through `Env`; don't read environment variables directly elsewhere.
- Write XML doc comments (`/// <summary>`) on public types and commands.
- Library code (parsing a format) belongs in the matching `*Sharp` submodule, not in `TkvscHost`. To change a library, commit and push it in its own repository, then `git add host/libs/<Name>` here to move the pin.

### Tests

- Host behavior gets a test in `host/tests/TkvscHost.Tests/`. Build small synthetic files in the test (see `RecipeTests.cs`) instead of committing game files.
- **Never commit game data.** No romfs files, dumps, or anything extracted from the game. Tests must run without a game dump.
- The TypeScript side has no automated tests yet, so describe how you tested your change manually in the PR.

## Documentation

- User-facing changes get an entry in the next file under `docs/release-notes/`, and the relevant page in `docs/` (`commands.md`, `settings.md`, `recipes.md`, ...) is updated when behavior changes.
- New settings and commands are declared in `package.json` and documented in `docs/settings.md` / `docs/commands.md`.

### Addon API documentation

Changes under `src/api/` that affect addon extensions **must** update:

- [`docs/api/v1.md`](../docs/api/v1.md) - method/property reference for the current API version
- [`docs/api/CHANGELOG.md`](../docs/api/CHANGELOG.md) - additive changes under v1; new file `vN.md` + changelog entry when `apiVersion` bumps
- [`docs/api/addon-development.md`](../docs/api/addon-development.md) - when integration patterns or requirements change

Link new symbols from TSDoc in `src/api/types.ts` to the matching `docs/api/v1.md` anchor.

Game plugins for the host are documented in [`docs/api/host-plugins.md`](../docs/api/host-plugins.md); update it if you change `TkvscHost.PluginApi`.

## Third-party content

New dependencies, bundled data or code taken from elsewhere must be license-compatible and listed in [`THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md). Dependabot handles routine version bumps.

## Questions?

Join the [Bladesaw Studios Discord](https://discord.gg/vwPnX2uB8s) if you want to discuss a contribution before starting.

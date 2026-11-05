# Adding a game

A game add-on is a VS Code extension with a [game profile](v1.md#game-profiles). Start with the profile alone: many games need nothing else. Write a host plugin only for what a profile can't describe.

## Step 1: try a profile

A profile can set all of this without any C#:

| The game has | Profile field |
|---|---|
| Compressed files named with something other than `.zs` | `compressedSuffixes` |
| Zstd with a dictionary pack somewhere else | `zsDicPath` (with `compressionBackend: "totk-zstd"`) |
| Plain zstd and Yaz0 | `compressionBackend: "plain-zstd-yaz0"` |
| Its own SARC archive extensions | `archivePatterns` and `indexing.archiveExtensions` |
| Its own MSBT tags | `msbtConfigPath` |
| Flag hash names | `hashNamesPath` |
| AAMP names | `aampNameTable`, `aampHashNamesPath` |
| XLink files of another version | `xlinkVersions` |

For example, Breath of the Wild's settings would look like this:

```json
"gameProfile": {
  "id": "botw",
  "displayName": "Breath of the Wild",
  "romfsSentinel": "Pack/Bootup.pack",
  "compressionBackend": "plain-zstd-yaz0",
  "romfsSettingsKey": "botw.romfsPath",
  "aampNameTable": "botw",
  "xlinkVersions": { "elink": 30, "slink": 28 },
  "archivePatterns": ["\\.(pack|sarc|sbactorpack|ssarc)$"]
}
```

## Step 2: write a host plugin if you need code

A plugin is a .NET DLL the TKVSC host loads for its game. It can provide:

- **Formats:** a file kind converted to editor text and back, or a replacement for a built-in one.
- **Compression:** a replacement for zstd.
- **Archive formats:** archives other than SARC, which then list, read, write, delete, rename and nest like any other.
- **Commands:** new host commands, or a replacement for a built-in one, such as `list-bars` for a BARS version `TotkBars` can't read.

[`host/samples/SamplePlugin`](../../host/samples/SamplePlugin) is a complete one to copy. The contract is [`PluginApi.cs`](../../host/src/TkvscHost.PluginApi/PluginApi.cs).

```csharp
[assembly: TkvscPlugin(typeof(MyGame.Plugin))]

namespace MyGame;

public sealed class Plugin : IGamePlugin
{
    public void Configure(IPluginBuilder builder)
    {
        builder.AddFormat("mygame-table", new TableFormat());
        builder.UseCompression(new MyCompression());
        builder.AddArchiveFormat(new MyArchive());
        builder.AddCommand("mygame-rebuild", command => new JsonObject { ["success"] = true });
    }
}
```

### The project file

Target `net10.0` and compile against the contract without shipping it:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <EnableDynamicLoading>true</EnableDynamicLoading>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="path\to\TkvscHost.PluginApi.csproj">
    <Private>false</Private>
    <ExcludeAssets>runtime</ExcludeAssets>
  </ProjectReference>
</ItemGroup>
```

The contract isn't on NuGet yet, so reference the project from a checkout of TKVSC.

### Wiring it into your add-on

Run `dotnet publish -c Release`, include the output folder in your VSIX, and name the DLL in your profile. Register your formats there too, so the extension knows those files have text:

```json
"contributes": {
  "tkvsc": {
    "gameProfile": {
      "id": "mygame",
      "displayName": "My Game",
      "romfsSentinel": "Data/Boot.pack",
      "compressionBackend": "plugin",
      "compressedSuffixes": [".lz"],
      "archivePatterns": ["\\.myarc$"],
      "indexing": { "archiveExtensions": [".myarc"] },
      "hostPlugin": "bin/plugin/MyGame.Plugin.dll"
    },
    "formats": [
      { "extensions": ["mytable"], "handler": "mygame-table", "language": "yaml" }
    ]
  }
}
```

If your add-on registers the profile from code as well, as Splatoon 3's does, pass `{ extensionRoot: context.extensionPath }` so a relative `hostPlugin` can be found.

An add-on runs a plugin's command like any other, with the active game's environment:

```typescript
const bridge = api.getBridge();
await bridge.runBridgeJsonAsync(bridge.getHost(), '', ['mygame-rebuild'], undefined, bridge.getBridgeEnv());
```

## How plugins behave

- **Only the active game's plugin loads.** Each command is a new process, so `Configure` runs every time: keep it fast. Loading added about 40 ms per command to the host's roughly 260 ms in one measurement with the sample plugin.
- **Format handlers get decompressed bytes.** The host removes compression before calling you and restores it afterwards. `original` is empty for a new file.
- **A compression backend replaces zstd completely.** Files it doesn't recognize are treated as uncompressed, apart from Yaz0, which still works. If your game also uses zstd, your backend has to handle that too. It can be called from several threads while an index builds.
- **Archive formats only read and write a list of files.** The host handles compression, nested paths and every operation on top of that. A format is chosen by `Matches` on the decompressed contents, and anything no format matches is opened as SARC. Name your extensions in `archivePatterns` so the extension and host treat them as archives, and in `indexing.archiveExtensions` so they're indexed. They match with or without a compressed suffix. For a read-only format, throw from `Write` with a message saying so: the file on disk isn't touched.
- **Compressed suffixes come from the profile.** `compressedSuffixes` replaces the default `.zs` for that game, in both the extension and the host. It's independent of how files are actually compressed, which is decided from their contents.
- **A plugin that can't start stops the command with an error.** Running without the game's code could write files in the wrong form. The host says what went wrong: file not found, not a plugin, or built for another contract version.
- **Printing is safe.** Anything a plugin writes to standard output goes to standard error, so it can't corrupt the JSON result.

## Dependencies

Anything the host already has comes from the host: the contract, the framework and the format libraries (BymlSharp, SarcSharp and the others). Anything else loads from your plugin's folder, including native libraries listed in its `.deps.json`.

- **You can use the host's format libraries.** Reference them the same way as the contract, with `Private` false and `ExcludeAssets` runtime. They aren't part of the versioned contract, so a TKVSC update that changes one can break your plugin. That shows up as an error naming what's missing, and you fix it by rebuilding against the new version.
- **You can't bring your own version of a library the host has.** The host's copy is used whenever it can satisfy the request.
- **Native dependencies are per platform.** A purely managed plugin needs one build for every platform. One with native libraries has to include each platform's library.

## Versions

`PluginApi.Version` is compiled into your DLL. A host runs plugins from `PluginApi.MinimumVersion` to its own `PluginApi.Version`. Adding to the contract raises `Version`; only a breaking change raises `MinimumVersion`.

## What your add-on still has to declare

A plugin runs in the host and a profile is read at run time, but VS Code reads some things only from `package.json`. TKVSC's own list names its extensions with `.zs`, so for other extensions or suffixes your add-on contributes its own:

- **Custom editor selectors**, for example the texture and audio viewers on `*.bars.lz`.
- **File icons and language associations**, for example `**.myarc`.

Custom editors and menus also belong in your add-on's TypeScript.

## Trust

A plugin runs as ordinary code with the user's permissions, with no sandbox, the same as the VS Code extension that ships it. Profiles come only from installed extensions, never from a workspace or a project file.

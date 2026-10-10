# TKVSC host

A C# program that runs the extension's file commands. The extension calls it with a command and its arguments (and input
on stdin where a command takes some) and gets JSON back; a failure comes back as `{"error", "traceback"}`. It replaced
a Python bridge, which is gone: nothing in the extension needs Python any more.

```bash
tkvsc-host list <archive> [<path inside it>]
tkvsc-host read <archive> <path>             # text for the editor, or a texture's metadata and a picture
echo "edited text" | tkvsc-host write <archive> <path>
```

The settings come in the environment (`TKVSC_ROMFS`, `TKVSC_GAME_ID`, `TKVSC_HANDLER_MANIFEST`, ...; see
`src/TkvscHost/Env.cs`; `src/api/bridgeEnv.ts` builds them).

## What it runs itself

| Commands | Formats |
| --- | --- |
| `list`, `read`, `write`, `write-raw`, `delete-entry`, `rename-entry` | any `.pack` / `.sarc` / `.genvb` / `.blarc` / `.bfarc`, nested to any depth, with or without zstd |
| `read-disk`, `write-disk` | BYML (including the colours of the particle data in a `PtclBin`, and the `Tag.Product` table's per-actor form), MSBT, AAMP, XLNK (`.belnk` / `.bslnk`) |
| `export-temp`, `export-stored`, `export-converted`, `decompress-file`, `compress-file` | the above, plus PNG / JPEG / BMP / TGA / DDS of textures, YAML of BYML and AAMP |
| `build-romfs-index`, `build-canonical-path-index` | the whole romfs; about 10 s |
| `read-font-disk`, `prepare-font-replacement` | `.bfttf` / `.bfotf` fonts, through BFontSharp |
| `read-bwav`, `read-bwav-audio`, `list-bars`, `read-bars-audio`, `replace-bars-audio` | BWAV and BARS (DSP-ADPCM, PCM16 and Opus), through BfAudioSharp |
| `evaluate-hexpat` | the hex editor's pattern view, through HexpatSharp (ImHex's pattern language) |
| `read` (textures), `render-bntx-texture`, `render-txtg`, `update-bntx-metadata`, `update-txtg-metadata`, `replace-bntx-payload`, `replace-txtg-payload` | BNTX and TXTG |


## Building

```bash
npm run build-host          # publishes bin/host/<this machine's runtime>, which the extension picks up
dotnet test host/TkvscHost.slnx
```

The libraries (SarcSharp, BymlSharp, ...) are git submodules in `host/libs`; clone with `--recurse-submodules` or run
`git submodule update --init`. To build against a folder of your own checkouts instead, set `LIBROOT` or pass
`-p:LibRoot=<folder>` (`host/Directory.Build.props`). To change a library, commit and push it in its own repository, then
`git add host/libs/<Name>` here to move the pin. In a checkout
the extension also finds a plain `dotnet build` in `host/src/TkvscHost/bin/`.

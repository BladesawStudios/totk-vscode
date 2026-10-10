# TKVSC host

A C# program that runs the extension's file commands. It takes the same command line and gives the same JSON as
`python/totk_bridge.py`, so the extension can call it in place of the script. What it has not taken over yet it hands
to the Python bridge, with the same arguments and input, so nothing is lost while the port goes on.

```bash
tkvsc-host list <archive> [<path inside it>]
tkvsc-host read <archive> <path>             # text for the editor, or a texture's metadata and a picture
echo "edited text" | tkvsc-host write <archive> <path>
```

The settings come in the environment, under the names the bridge already reads (`TKVSC_ROMFS`, `TKVSC_GAME_ID`,
`TKVSC_HANDLER_MANIFEST`, ...; see `src/TkvscHost/Env.cs`). For what it hands on, the extension also sets
`TKVSC_PYTHON` and `TKVSC_BRIDGE` (`src/bridge.ts` does this when the "interpreter" it is given is the host).

## What it runs itself

| Commands | Formats |
| --- | --- |
| `list`, `read`, `write`, `write-raw`, `delete-entry`, `rename-entry` | any `.pack` / `.sarc` / `.genvb` / `.blarc` / `.bfarc`, nested to any depth, with or without zstd |
| `read-disk`, `write-disk` | BYML (including the colours of the particle data in a `PtclBin`, and the `Tag.Product` table's per-actor form), MSBT, AAMP, XLNK (`.belnk` / `.bslnk`) |
| `export-temp`, `export-stored`, `export-converted`, `decompress-file`, `compress-file` | the above, plus PNG / JPEG / BMP / TGA / DDS of textures, YAML of BYML and AAMP |
| `build-romfs-index`, `build-canonical-path-index` | the whole romfs; about 10 s where Python takes about a minute |
| `read-font-disk`, `prepare-font-replacement` | `.bfttf` / `.bfotf` fonts, through BFontSharp |
| `read-bwav`, `read-bwav-audio`, `list-bars`, `read-bars-audio`, `replace-bars-audio` | BWAV and BARS (DSP-ADPCM and PCM16), through BfAudioSharp |
| `evaluate-hexpat` | the hex editor's pattern view, through HexpatSharp (ImHex's pattern language) |
| `read` (textures), `render-bntx-texture`, `render-txtg`, `update-bntx-metadata`, `update-txtg-metadata`, `replace-bntx-payload`, `replace-txtg-payload` | BNTX and TXTG |

## What still goes to Python

The extension does not start Python at all unless one of these is used: with the host present, activation builds the indexes
right away, a Python environment from an earlier setup is picked up if there is one, and otherwise the environment is made the
first time a command the host hands over finds none (`src/bridge.ts`, `ensurePython`).

Only add-on handlers (and a BWAV with a codec BfAudioSharp does not know, which none of the game's do; Opus ones decode natively). AINB, ASB and BAEV
are no longer supported by the extension at all. Those commands run, as before, if Python is set up. Without it the host answers with an error that says so.

## Where it differs from the Python bridge

These are on purpose.

- **Hex patterns** run on HexpatSharp, a real interpreter of the pattern language, instead of hexpyt's translation to Python. It
  handles what hexpyt left out (functions, `[[format]]` / `[[transform]]` / `[[inline]]` / `[[hidden]]`, `match`, `for`, pointers,
  namespaces, templates with value arguments, bitfield orders, sections, `std::` functions). A pattern that stops part way
  shows what it placed and why it stopped. Arrays of numbers are one node with a preview; characters read as a string.
- **Hash maps in BYML.** A file with `!h32` / `!h64` hash maps (11 files in the game, the voice tables among them) shows in
  BymlSharp's YAML with those tags and saves back byte for byte. Python showed `<oead.byml.Hash32 object ...>`.
- **Multi-line strings in BYML.** The editor text used a bare `|` block, which YAML reads back with an extra newline,
  so saving changed the string. The host chooses `|-`, `|` or `|+` to match the string.
- **Array textures.** The viewer shows one layer at a time, with a layer picker. `read`, `render-bntx-texture`,
  `render-txtg`, `export-converted`, `replace-bntx-payload` and `replace-txtg-payload` take an optional layer number
  after their usual arguments, and the metadata gains `arrayCount`. A layer is replaced from a DDS; if its size, format or
  mip count differ from the texture's it is converted to fit (resized, mips rebuilt, re-encoded; the reply carries a `note`
  that the editor shows). BC6H and signed formats can't be made from an 8-bit image and are refused. The encoders are
  TexSharp's own and aim for a close match rather than the best possible quality. Python only ever showed layer 0.
- **Font replacement** encrypts a `.bfotf` with the Win key, the one the game's own files use. The Python bridge always used
  the NX key unless it could tell otherwise, which it never could for a font inside an archive. Importing a plain font over a
  game font gives that font's exact stored bytes back (checked on three of them).
- **Audio.** BWAV is decoded by the host itself instead of vgmstream-cli, and new audio is encoded by a port of the same
  DSP-ADPCM encoder, so what it writes is byte for byte what the Python bridge wrote (checked on real audio). The viewer WAV is
  a single pass: vgmstream's default made two passes of a looping sound plus a fade, with the loop points given separately
  anyway. A stereo prefetch clip is decoded from its own data (vgmstream read the second channel from the wrong place; the
  host's output equals the start of the full sound). A loop that lies beyond a prefetch clip is not reported. Non-WAV
  audio for `replace-bars-audio` still needs ffmpeg on `PATH` (or `TKVSC_FFMPEG`).
- **Texture metadata saves** cannot change a texture's name or path yet, and say so.
- **Archive rewrites keep their layout.** SarcSharp keeps each file's alignment, so an archive written back unchanged
  is the same bytes (checked on all 15,075 packs of the romfs). oead's writer re-guesses it; it matched 31 of 40.
- **AAMP names and numbers.** Names come from the same lists oead uses, plus TotK's, and floats print with nine
  significant digits as oead does.
- **BC1 textures** keep the alpha the format stores. The Python decoder always made them opaque.
- Error messages are the C# ones.

## Building

```bash
npm run build-host          # publishes bin/host/<this machine's runtime>, which the extension picks up
dotnet test host/TkvscHost.slnx
```

The libraries (SarcSharp, BymlSharp, ...) are expected as a folder of siblings; `host/Directory.Build.props` looks for
`C# Libs` five levels above `host/`. Set `LIBROOT` or pass `-p:LibRoot=<folder>` to put them elsewhere. In a checkout
the extension also finds a plain `dotnet build` in `host/src/TkvscHost/bin/`.

## Checking it against the bridge

`host/tests/parity/parity.py` runs the same commands through the bridge and the host on a real romfs and compares
what comes back: lists, reads, write round trips, indexes, textures and their edits.

```bash
python host/tests/parity/parity.py --romfs <romfs> --count 20 --only list,read,discover,write,textures,exports
```

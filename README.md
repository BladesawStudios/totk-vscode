<div align="center">
  <img src="https://github.com/BladesawStudios/totk-vscode/blob/main/graphics/logo/Logo_FancyShading.png?raw=true" width="300vh">
  <h1>Tears of the Kingdom VSCode Extension</h1>
  
  <a href="https://discord.gg/vwPnX2uB8s">
    <img src="https://img.shields.io/discord/1508590792149827745?style=for-the-badge&logoColor=5865F2&color=5865F2&labelColor=2A2C33&logo=discord&label=discord" alt="Discord"/>
  </a> &nbsp;
  <a href="https://github.com/BladesawStudios/totk-vscode/releases/latest">
    <img src="https://img.shields.io/github/v/tag/BladesawStudios/totk-vscode?style=for-the-badge&logoColor=ffffff&color=C71B42&labelColor=2A2C33&logo=github&label=Version" alt="Latest Release"/>
  </a> &nbsp;
  <a href="https://github.com/BladesawStudios/totk-vscode">
    <img src="https://img.shields.io/github/stars/BladesawStudios/totk-vscode?style=for-the-badge&color=FFCB41&labelColor=2A2C33&logo=github" alt="Stars"/>

  </a> &nbsp;
  <a href="https://github.com/BladesawStudios/totk-vscode/releases">
    <img src="https://img.shields.io/github/downloads/BladesawStudios/totk-vscode/total?style=for-the-badge&labelColor=2A2C33&color=31c059&logo=data:image/svg%2bxml;base64,PD94bWwgdmVyc2lvbj0iMS4wIiBlbmNvZGluZz0iaXNvLTg4NTktMSI/Pg0KPCEtLSBVcGxvYWRlZCB0bzogU1ZHIFJlcG8sIHd3dy5zdmdyZXBvLmNvbSwgR2VuZXJhdG9yOiBTVkcgUmVwbyBNaXhlciBUb29scyAtLT4NCjxzdmcgZmlsbD0iIzMxYzA1OSIgaGVpZ2h0PSI4MDBweCIgd2lkdGg9IjgwMHB4IiB2ZXJzaW9uPSIxLjEiIGlkPSJDYXBhXzEiIHhtbG5zPSJodHRwOi8vd3d3LnczLm9yZy8yMDAwL3N2ZyIgeG1sbnM6eGxpbms9Imh0dHA6Ly93d3cudzMub3JnLzE5OTkveGxpbmsiDQoJIHZpZXdCb3g9IjAgMCA0NzEuMiA0NzEuMiIgeG1sOnNwYWNlPSJwcmVzZXJ2ZSI+DQo8Zz4NCgk8Zz4NCgkJPHBhdGggZD0iTTQ1Ny43LDIzMC4xNWMtNy41LDAtMTMuNSw2LTEzLjUsMTMuNXYxMjIuOGMwLDMzLjQtMjcuMiw2MC41LTYwLjUsNjAuNUg4Ny41Yy0zMy40LDAtNjAuNS0yNy4yLTYwLjUtNjAuNXYtMTI0LjgNCgkJCWMwLTcuNS02LTEzLjUtMTMuNS0xMy41cy0xMy41LDYtMTMuNSwxMy41djEyNC44YzAsNDguMywzOS4zLDg3LjUsODcuNSw4Ny41aDI5Ni4yYzQ4LjMsMCw4Ny41LTM5LjMsODcuNS04Ny41di0xMjIuOA0KCQkJQzQ3MS4yLDIzNi4yNSw0NjUuMiwyMzAuMTUsNDU3LjcsMjMwLjE1eiIvPg0KCQk8cGF0aCBkPSJNMjI2LjEsMzQ2Ljc1YzIuNiwyLjYsNi4xLDQsOS41LDRzNi45LTEuMyw5LjUtNGw4NS44LTg1LjhjNS4zLTUuMyw1LjMtMTMuOCwwLTE5LjFjLTUuMy01LjMtMTMuOC01LjMtMTkuMSwwbC02Mi43LDYyLjgNCgkJCVYzMC43NWMwLTcuNS02LTEzLjUtMTMuNS0xMy41cy0xMy41LDYtMTMuNSwxMy41djI3My45bC02Mi44LTYyLjhjLTUuMy01LjMtMTMuOC01LjMtMTkuMSwwYy01LjMsNS4zLTUuMywxMy44LDAsMTkuMQ0KCQkJTDIyNi4xLDM0Ni43NXoiLz4NCgk8L2c+DQo8L2c+DQo8L3N2Zz4=&cache=1" alt="Downloads"/>
  </a>
</div>
<br />

The **T**ears of the **K**ingdom **V**isual **S**tudio **C**ode Extension or **TKVSC** is a Visual Studio Code extension for editing and browsing **Tears of the Kingdom** (**TotK**) game files.

<sup></sub>It also pairs well with **TKMM!**</sup></sub>

**TKVSC Website**: https://tkvsc.dev/

**TKVSC** GameBanana page: https://gamebanana.com/tools/22893

**Game Dump Guide** (required): https://gamebanana.com/tuts/19858

## DISCLAIMER
**WE ARE NOT RESPONSIBLE IF YOUR MOD BREAKS. THIS TOOL IS IN ACTIVE DEVELOPMENT AND THINGS CAN BREAK. MAKE FREQUENT BACKUPS**

## Features
Preview and edit subfiles of SARC and BNTX archives

 <img src="https://github.com/BladesawStudios/totk-vscode/blob/main/graphics/Promo/SARC_Edit.png?raw=true" width="720vh">

### Supported Filetypes 


#### Text Editor
- BYML
- BGYML
- AAMP
- MSBT
- XLINK
- BFRES material animations (`.fmaa`), as YAML. Open a `.bfres` like an archive to see everything in it: material animations and embedded files can be edited, added and deleted, while each model is a folder with a YAML file per material (colours and numbers) that can be edited; shapes, bones, skeletal animations and textures are listed as read-only summaries
- Particle files (`.vsetb.byml`) open like an archive: a folder per effect set and a file per emitter (`Esets/<set>/<emitter>.emitter`), each a short YAML of that emitter's colours. A big file like `static` no longer has to be edited as one document
- Recipes (`*.recipe.yaml`): make a mod's files from the game's own with a few variables, so a whole recolour can be remade in another colour by changing one line. A key with a plain value is a variable, a key with a map under it is a file in the game, and each entry under it is a pattern for the files inside with a step (`tint $color [only REGEX]` for colours and textures, `replace FIND -> WITH`, `copy FILE`, `delete`); `include:` pulls in other recipes. Run it from the file's right-click menu or **TKVSC: Run Recipe**, or see what it would change first with **TKVSC: Preview Recipe Changes**; it asks for each variable and writes the changed files to a `romfs` folder next to the recipe. See [docs/recipes.md](docs/recipes.md) and `docs/examples/navi-recolour.recipe.yaml`
- PCHTXT
- PTCL
- TKMM Config Files

#### Texture Viewer/Replacer
- BNTX
- Tex2Go
- **Unsupported**
- - VSP

#### Audio Player/Replacer
- Bars
- BWAV

#### Font Viewer/Replacer
- bfttf
- bfotf
---

### Activity Bar Tabs

 <img src="https://github.com/BladesawStudios/totk-vscode/blob/main/graphics/Promo/ActivityBarGuide.png?raw=true" width="400vh">


**Your Projects**
- Add the current folder open in VS Code to a list of Project Folders

**Game Dump**
- Browse your dump of TotK in Read-Only mode. Right click to add a file to the correct romfs path within a Project Folder of your choosing

### Additional capabilities:

- Archives within archives (e.g. a `.sarc` inside a `.pack`) can be browsed and edited
- Search for romfs contents, including filenames inside of SARCs
- Right-click a file within an archive to export it to a Project Folder.
- File templates: Rather than the typical method of copying, pasting, and wiping an existing file to make a new file of the same format, TKVSC can create empty files of formats supported by the editor.
- Preview `.bntx` subfiles and `.txtg` files as PNGs
- Visual editor for [TKMM](https://tkmm.org) `.tkproj` files
- For files TKVSC does not support, such as BFRES model meshes, the user can choose external programs to open them in by default (separate from similar features in the OS).

 <img src="https://github.com/BladesawStudios/totk-vscode/blob/main/graphics/Promo/tkproj_Edit.png?raw=true" width="800vh">

## Canonical Path Saving

The canonical path of a file is where the file exists in the game's memory, as opposed to the romfs path which describes its location within a game dump. Using the romfs file located at `\Pack\Actor\Armor_001_Head.pack.zs\Component\ArmorParam\Armor_001_Head.game__component__ArmorParam.bgyml` as an example, the canonical path would be `\Component\ArmorParam\Armor_001_Head.game__component__ArmorParam.bgyml`.

Anyone who has made changes to an upgradeable armor set, or to any parameter file that is parented to a file used in another actor pack, is familiar with needing to make the same edits to an identically named file in several different locations in order for everything to work nicely ingame. TKVSC builds a database of all canonical paths in your game dump and in each of your mod projects and automatically saves changes to all instances of a file's canonical path, pulling files in from the game dump as needed when it isn't already present in your mod project.

To demonstrate with a direct comparison, modifying the defense of every level of the Champion's Leathers would take **15 file modifications without TKVSC**, and only **5 with TKVSC**.

## Planned Features

*( if there are any features you don't see on here, feel free to make an issue on this GitHub! )*

- BFRES Support (Preview, Editing.)
- Actor Tooling (Automating the process as much as possible.)
- Full romfs file content string searching

- <sub></sup>PTCL/Effect support is being researched but not confirmed.</sup></sub>

## Setup

### Requirements
* Node.js and the .NET 10 SDK (if building extension from source; see `host/README.md`)
* Valid TotK dump

### Steps
1. Download the VSIX for your platform from [Releases](https://github.com/BladesawStudios/totk-vscode/releases/latest) (`win32-x64`, `linux-x64`, `darwin-x64` or `darwin-arm64`) and install it.
2. Follow the prompt to select your romfs dump path.

### Other games

[Add-ons](https://github.com/BladesawStudios/tkvsc-addons) add support for Splatoon 3 and Ocarina of Time.


## Build Instructions

A VSIX file is the installable extension package. It carries the C# host for one platform, so it is built per platform. You need Node.js and the .NET 10 SDK:

```bash
git clone --recurse-submodules https://github.com/BladesawStudios/totk-vscode.git
cd totk-vscode
npm install
npm run package:vsix                      # for this machine
node scripts/package-vsix.js linux-x64    # or win32-x64, linux-x64, darwin-x64, darwin-arm64
```

That produces `out/totk-vscode-<target>-<version>.vsix` (version comes from `package.json`).


### Contributing:

See: [CONTRIBUTING.md](./.github/CONTRIBUTING.md)

### Addon development

Companion VS Code extensions can extend TKVSC via the exported API. See [docs/api/addon-development.md](docs/api/addon-development.md) (guide) and [docs/api/v1.md](docs/api/v1.md) (full API reference).

## Credits
### TKVSC Team
**Mind** - Project Lead, Developer\
**The5thTear** - Developer, Research\
**Aster** - Graphics, Documentation

### Contributors
**Clonephaze** - [GitHub Template Refactor](https://github.com/BladesawStudios/totk-vscode/pull/62)\
**Bryn** - [ASB Splatoon 3 Formats](https://github.com/FemBryn/asb-Spl3) (since removed)\
**lucythegoosey07** - [RSTB Support](https://github.com/BladesawStudios/totk-vscode/pull/124) (since removed)

### Third-Parties
**dt13245**: References for many formats within the game\
**AeonSake**: [MSBT Editor](https://gitlab.com/AeonSake/msbt-editor) - TotK Config included\
**LordBubbles**: [TKMM](https://tkmm.org/) - Assistance with implementing TKMM compatibility\
**KillzXGaming**: [Switch Toolbox](https://github.com/KillzXGaming/Switch-Toolbox) - image handling referenced\
**Arch Leaders**: [NX Editor](https://github.com/NX-Editor/NxEditor) - text formatting referenced\
**MacSpazzy & MrCheeze**: [TotK save variable hashes](https://www.marcrobledo.com/savegame-editors/zelda-totk/) - cracked names for the GameDataList hashes, bundled as `vendor/zelda-totk.hashes.csv`\
**SolidLink**: [Totkbits](https://github.com/SolidLink95/TotkBits) - Tag.Product & text formatting referenced\
**lostromb**: [Concentus](https://github.com/lostromb/concentus) (BSD-3-Clause) - Opus decoding for BWAV audio

Licences of the libraries bundled with TKVSC are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Help and Community

- [Discord Server](https://discord.gg/vwPnX2uB8s)
- [Documentation](https://tkvsc.dev/docs/index.html)
- [Patreon](https://www.patreon.com/cw/TKVSC)

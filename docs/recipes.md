# Recipes

A recipe is a `.recipe.yaml` file that describes a mod as a few variables and the game files to change. Running it copies each file from the game's RomFS, edits it, and writes the result to a `romfs` folder next to the recipe. Every run starts again from the game's own files, so a recipe is safe to run as often as you like, and changing one variable remakes the whole mod.

Run one from the file's right-click menu or **TKVSC: Run Recipe**, or check it first with **TKVSC: Preview Recipe Changes**. It asks for each variable (the file's value is the default), then offers to show the output folder.

## Shape

```yaml
color: "#FF0000"

Effect/static.Nin_Ounce_NVN2.vsetb.byml.zs:
  Esets/Npc_Navi_*/*.emitter: tint $color

Model/Npc_Navi.Npc_Navi.bfres.zs:
  Models/Npc_Navi/Materials/*.fmat: tint $color only ns_param_color[0-5]
```

- A key with a plain value is a **variable**. Use it in a step as `$color` or `${color}`.
- A key with a map under it is a **file in the game's RomFS**. Each key of the map is a pattern for the files inside it, and its value is a step or a list of steps.
- A key whose value is a step (or a list of steps) works on the file itself, for plain files like a `.bgyml`.
- Quote a value that starts with `#`, since YAML reads it as a comment.

Patterns: `*` matches inside one folder, `**` across folders, `?` one character.

## Steps

| Step | What it does |
| --- | --- |
| `tint COLOR [only REGEX]` | Recolours every colour in the file to `COLOR` and keeps each one's brightness. `only` limits it to colours whose name matches the regex. Works on particle emitters, material parameters (`.fmat`), material animations (`.fmaa`) and textures (see below). |
| `replace FIND -> WITH` | A regex replace over the file's text. `\n` in `WITH` is a new line, `$1` is a regex group, `$name` is a recipe variable. Matches work per line, and `\z` matches the end of the file. |
| `copy FILE` | Makes the file a copy of another game file (or of one the recipe has already changed). Whole-file only, and it has to be the first step; steps after it change the copy. |
| `delete` | On a file: removes it from the output. On an entry pattern: removes the matching entries from the archive. It cannot be combined with other steps. |

Steps run on the readable text of each file, so anything TKVSC can show as YAML can be edited. If a particle channel can only hold one number for red, green and blue, the tint stores the closest colour it can (red becomes `[1, 0, 0]`).

### Textures

A texture file (`.bntx`) is a file whose entries are textures, so the pattern is a texture name (`*` for all of them):

```yaml
Tex/Npc_Navi_Body_Alb.00.bntx.zs:
  '*': tint $color
```

`tint` gives each pixel the colour at that pixel's own brightness and keeps its alpha, then encodes the texture back in its own format with its mips made again. It works for the colour formats TKVSC can encode; textures with several layers, and data formats like normal maps, are refused. `replace` cannot change a texture.

## Including other recipes

```yaml
include: fairy-body.recipe.yaml    # or a list
color: "#00FF00"
```

Included recipes (paths are relative to this one) run first and share their variables, so one `color` line drives all of them. A variable this recipe sets wins over the same one in an included recipe. Recipes that include each other are refused.

## Previewing

**TKVSC: Preview Recipe Changes** runs the recipe into a temporary folder and shows every file and entry it would change, with the lines or average colours before and after. Nothing is kept. It also reports what a real run would hide: patterns that match nothing, entries that are not an editable type, and files missing from the RomFS. The host command is `preview-recipe` (same arguments as `apply-recipe`).

## Limits

- Only files TKVSC can show as text, and colour textures, are editable. Other binary files can be copied or deleted but not changed.
- A recipe changes data, it cannot tell you which asset the game uses for something. Use the archive views to find that, then write the recipe.
- Steps cannot be conditional, and a variable is always a string.
- `delete` on entries works where the archive format supports deleting entries (SARC and BFRES do).

See [examples/navi-recolour.recipe.yaml](examples/navi-recolour.recipe.yaml).

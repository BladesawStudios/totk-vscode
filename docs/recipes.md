# Recipes

A recipe is a `.recipe.yaml` file that describes a mod as a few variables and the game files to change. Running it copies each file from the game's RomFS, edits it, and writes the result to a `romfs` folder next to the recipe. Every run starts again from the game's own files, so a recipe is safe to run as often as you like, and changing one variable remakes the whole mod.

Run one from the file's right-click menu or **TKVSC: Run Recipe**. It asks for each variable (the file's value is the default), then offers to show the output folder.

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
| `tint COLOR [only REGEX]` | Recolours every colour in the file to `COLOR` and keeps each one's brightness. `only` limits it to colours whose name matches the regex. Works on particle emitters, material parameters (`.fmat`) and material animations (`.fmaa`). |
| `replace FIND -> WITH` | A regex replace over the file's text. `\n` in `WITH` is a new line, `$1` is a regex group, `$name` is a recipe variable. Matches work per line, and `\z` matches the end of the file. |

Steps run on the readable text of each file, so anything TKVSC can show as YAML can be edited. If a particle channel can only hold one number for red, green and blue, the tint stores the closest colour it can (red becomes `[1, 0, 0]`).

## Limits

- Only files TKVSC can show as text are editable. Textures (`.bntx`) and other binary files cannot be changed by a recipe yet.
- A recipe changes data, it cannot tell you which asset the game uses for something. Use the archive views to find that, then write the recipe.
- Steps cannot be conditional, and a variable is always a string.

See [examples/navi-recolour.recipe.yaml](examples/navi-recolour.recipe.yaml).

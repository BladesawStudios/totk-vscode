using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TkvscHost.Formats;
using YamlDotNet.Serialization;

namespace TkvscHost;

/// <summary>
/// Recipes: a YAML file that says which files in the game to change and how, so a whole mod can be made again with a
/// different colour by changing one line.
/// </summary>
/// <remarks>
/// <code>
/// include: shared.recipe.yaml
/// color: "#FF0000"
///
/// Effect/static.Nin_Ounce_NVN2.vsetb.byml.zs:
///   Esets/Npc_Navi_*/*.emitter: tint $color
///
/// Tex/Npc_Navi_Body_Alb.00.bntx.zs:
///   '*': tint $color
///
/// Tex/Copy.bntx.zs: copy Tex/Original.bntx.zs
/// </code>
/// A key with a plain value is a variable (<c>$color</c> or <c>${color}</c> in a step); <c>include</c> names recipes (one
/// or a list) that run first and share their variables with this one. A key with a map under it is a file in the game's
/// RomFS, and each key of the map is a pattern for the entries inside it (<c>*</c> inside a folder, <c>**</c> across
/// folders; a texture file's entries are its textures) with the step, or list of steps, to run on them. A file with steps
/// directly under it (a string or a list) runs them on the file itself. A step is <c>tint COLOR [x TIMES] [only REGEX]</c>,
/// <c>replace FIND -> WITH</c>, <c>copy FILE</c> (the file becomes a copy of another) or <c>delete</c> (the file, or the
/// matching entries, are removed). The changed files are written to a <c>romfs</c> folder next to the recipe, and each run
/// starts again from the game's own files. A preview runs the same recipe into a temporary folder and reports what would
/// change without keeping it.
/// </remarks>
public static partial class RecipeCommands
{
    public static void Register(Dictionary<string, CommandHandler> commands)
    {
        commands["recipe-info"] = Info;
        commands["apply-recipe"] = c => Run(c, preview: false);
        commands["preview-recipe"] = c => Run(c, preview: true);
    }

    private const string OutputFolder = "romfs";

    private sealed record Edit(string File, string? Pattern, List<string> Steps);

    private sealed record Recipe(Dictionary<string, string> Variables, List<Edit> Edits);

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    private static string Scalar(object? value) => value switch
    {
        null => "",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static List<string> StepList(object? value) => value switch
    {
        List<object> list => [.. list.Select(Scalar)],
        _ => [Scalar(value)],
    };

    private static bool IsStep(string text) => Regex.IsMatch(text, @"^(tint|replace|copy|delete)(\s|$)");

    private static Recipe Load(string path, HashSet<string>? loading = null)
    {
        path = Path.GetFullPath(path);
        loading ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!loading.Add(path)) throw new InvalidDataException($"{Path.GetFileName(path)} includes itself (directly or through another recipe)");

        object? root;
        try
        {
            root = Yaml.Deserialize<object?>(File.ReadAllText(path));
        }
        catch (YamlDotNet.Core.YamlException error)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}, line {error.Start.Line}: {error.InnerException?.Message ?? error.Message}");
        }

        if (root is not Dictionary<object, object> top) throw new InvalidDataException($"{Path.GetFileName(path)}: a recipe is a map of variables and files");

        Dictionary<string, string> included = new(StringComparer.Ordinal), own = new(StringComparer.Ordinal);
        List<Edit> edits = [];
        foreach (var (key, value) in top)
        {
            string name = Scalar(key);
            if (name == "include")
            {
                foreach (string other in StepList(value))
                {
                    string target = Path.Combine(Path.GetDirectoryName(path)!, other);
                    if (!File.Exists(target)) throw new FileNotFoundException($"{Path.GetFileName(path)} includes {other}, which does not exist");
                    Recipe sub = Load(target, loading);
                    foreach (var (variable, fallback) in sub.Variables) included[variable] = fallback;
                    edits.AddRange(sub.Edits);
                }
            }
            else if (value is Dictionary<object, object> entries)
            {
                if (entries.TryGetValue("copy", out object? copyTarget))
                    edits.Add(new Edit(name, null, [$"copy {Scalar(copyTarget)}"]));

                foreach (var (pattern, steps) in entries)
                {
                    if (Scalar(pattern) == "copy") continue;
                    edits.Add(new Edit(name, Scalar(pattern), StepList(steps)));
                }
            }
            else if (value is List<object>)
                edits.Add(new Edit(name, null, StepList(value)));
            else if (IsStep(Scalar(value)))
                edits.Add(new Edit(name, null, [Scalar(value)]));
            else
                own[name] = Scalar(value);
        }

        loading.Remove(path);
        // A variable this recipe sets wins over the same one in an included recipe, so one line drives every part.
        foreach (var (variable, fallback) in own) included[variable] = fallback;
        if (edits.Count == 0) throw new InvalidDataException($"{Path.GetFileName(path)}: the recipe has no files to change");
        return new Recipe(included, edits);
    }

    private static Dictionary<string, string> WithOverrides(Recipe recipe, IEnumerable<string> overrides)
    {
        Dictionary<string, string> values = new(recipe.Variables, StringComparer.Ordinal);
        foreach (string given in overrides)
        {
            int at = given.IndexOf('=');
            if (at <= 0) throw new ArgumentException($"\"{given}\" is not name=value");
            string name = given[..at];
            if (!values.ContainsKey(name)) throw new ArgumentException($"The recipe has no variable \"{name}\"; it has: {string.Join(", ", values.Keys)}");
            values[name] = given[(at + 1)..];
        }

        return values;
    }

    private static JsonNode Info(CommandContext c)
    {
        Recipe recipe = Load(c.Arg(0));
        JsonObject variables = [];
        foreach (var (name, value) in recipe.Variables) variables[name] = value;
        return new JsonObject { ["name"] = Path.GetFileName(c.Arg(0)).Replace(".recipe.yaml", "").Replace(".recipe.yml", ""), ["variables"] = variables };
    }

    [GeneratedRegex(@"\$\{([^}]*)\}|\$(\w+)")]
    private static partial Regex Placeholder();

    private static string Substitute(string text, Dictionary<string, string> variables)
        => Placeholder().Replace(text, match =>
        {
            string name = match.Groups[1].Success ? match.Groups[1].Value.Trim() : match.Groups[2].Value;
            // A digit-only name is a regex group ($1), not a variable.
            if (name.All(char.IsDigit)) return match.Value;
            return variables.TryGetValue(name, out string? value) ? value : throw new ArgumentException($"The recipe uses ${name} but has no such variable");
        });

    private static Regex Glob(string pattern)
    {
        StringBuilder regex = new("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*') { regex.Append(".*"); i++; }
            else if (pattern[i] == '*') regex.Append("[^/]*");
            else if (pattern[i] == '?') regex.Append("[^/]");
            else regex.Append(Regex.Escape(pattern[i].ToString()));
        }

        return new Regex(regex.Append('$').ToString(), RegexOptions.CultureInvariant);
    }

    // ---- running ----

    private static JsonNode Run(CommandContext c, bool preview)
    {
        string recipePath = Path.GetFullPath(c.Arg(0));
        Recipe recipe = Load(recipePath);
        Dictionary<string, string> variables = WithOverrides(recipe, c.Args.Skip(1));
        if (c.Romfs.Length == 0) throw new InvalidOperationException("Set the game's RomFS folder first: recipes read the game's own files from it.");

        // A preview is a real run into a folder that is thrown away, so what it reports is exactly what a run would do.
        string outputRoot = preview
            ? Path.Combine(Path.GetTempPath(), "tkvsc-recipe-" + Guid.NewGuid().ToString("N")[..8])
            : Path.Combine(Path.GetDirectoryName(recipePath)!, OutputFolder);

        // The game's files are only ever read: a recipe writes to its own output folder, and that folder can never be
        // the game's RomFS or inside it (or hold it).
        if (Overlaps(c.Romfs, outputRoot))
            throw new InvalidOperationException($"The recipe's output folder ({outputRoot}) is the game's RomFS or inside it (or holds it). Move the recipe somewhere else; recipes never write to the game's files.");

        try
        {
            return Execute(c, recipe, variables, outputRoot, preview);
        }
        finally
        {
            if (preview && Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool Overlaps(string a, string b)
    {
        string x = Full(a), y = Full(b);
        return x.StartsWith(y + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || y.StartsWith(x + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A path from a recipe under a root folder; one that would leave it (<c>..</c>, a drive, a rooted path) is refused.</summary>
    private static string Inside(string root, string relative)
    {
        // Recipes are shared between systems, so a Windows drive or a backslash path is refused on Linux too.
        string portable = relative.Replace('\\', '/');
        bool rooted = Path.IsPathRooted(relative) || portable.StartsWith('/') || (portable.Length >= 2 && portable[1] == ':' && char.IsAsciiLetter(portable[0]));
        string rootFull = Full(root), full = Path.GetFullPath(Path.Combine(rootFull, portable));
        if (rooted || !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"\"{relative}\" is not a path inside the RomFS; recipes cannot use .. or a full path");
        return full;
    }

    private static JsonNode Execute(CommandContext c, Recipe recipe, Dictionary<string, string> variables, string outputRoot, bool preview)
    {
        FileKinds kinds = new(c.Env, c.Containers, c.Plugin);
        FormatHandlers handlers = new(c.Env, c.Containers, kinds, c.Plugin);
        Archives archives = new(c.Containers);

        // Every step is read and its variables filled in before any file is touched, so a mistake stops the run early.
        List<(Edit Edit, List<Op> Ops)> plan = [];
        foreach (Edit edit in recipe.Edits)
        {
            List<Op> ops = [.. edit.Steps.Select(step => Step(step, variables))];
            Validate(edit, ops);
            plan.Add((edit, ops));
        }

        HashSet<string> copied = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> changedByFile = new(StringComparer.OrdinalIgnoreCase);
        JsonArray warnings = [], edited = [];
        Dictionary<string, JsonArray> entriesByFile = new(StringComparer.OrdinalIgnoreCase);
        using IDisposable fit = Vfx3Text.FitToStorage();
        foreach (var (edit, planned) in plan)
        {
            List<Op> ops = planned;
            string relative = edit.File.Replace('\\', '/').TrimStart('/');
            string output = Inside(outputRoot, relative);
            JsonArray details = entriesByFile.TryGetValue(relative, out JsonArray? known) ? known : entriesByFile[relative] = [];

            if (edit.Pattern is null && ops[0] is DeleteOp)
            {
                bool had = File.Exists(output) || File.Exists(Inside(c.Romfs, relative));
                if (File.Exists(output)) File.Delete(output);
                copied.Remove(output);
                if (!had) warnings.Add($"{relative}: there is no such file to delete");
                changedByFile[relative] = changedByFile.GetValueOrDefault(relative);
                edited.Add(Summary(relative, null, had ? 1 : 0, had ? 1 : 0));
                continue;
            }

            if (ops[0] is CopyOp copy)
            {
                string from = copy.From.Replace('\\', '/').TrimStart('/');
                string made = Inside(outputRoot, from), game = Inside(c.Romfs, from);
                string source = File.Exists(made) ? made : game;
                if (!File.Exists(source)) throw new FileNotFoundException($"{from} (copied to {relative}) is not in the RomFS ({c.Romfs})");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(source, output, overwrite: true);
                copied.Add(output);
                changedByFile[relative] = changedByFile.GetValueOrDefault(relative) + 1;
                edited.Add(Summary(relative, null, 1, 1));
                details.Add(new JsonObject { ["name"] = "(whole file)", ["lines"] = new JsonArray(new JsonObject { ["from"] = "", ["to"] = $"a copy of {from}" }) });
                ops = [.. ops.Skip(1)];
                if (ops.Count == 0) continue;
            }
            else
            {
                string source = Inside(c.Romfs, relative);
                if (!File.Exists(source) && !File.Exists(output))
                {
                    // A preview reports a missing file and goes on, so every mistake shows at once.
                    if (!preview) throw new FileNotFoundException($"{relative} is not in the RomFS ({c.Romfs})");
                    warnings.Add($"{relative}: is not in the RomFS");
                    edited.Add(Summary(relative, edit.Pattern, 0, 0));
                    continue;
                }

                if (copied.Add(output))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    File.Copy(source, output, overwrite: true);
                }
            }

            string Transform(string text)
            {
                foreach (var op in ops) if (op is TextOp t) text = t.Text(text);
                return text;
            }

            int changed, matchedCount = 0;
            if (edit.Pattern is { } pattern)
            {
                Regex glob = Glob(pattern);
                if (ops[0] is DeleteOp)
                {
                    List<string> names = [.. archives.ListArchiveFiles(output, "").Where(n => glob.IsMatch(n))];
                    matchedCount = names.Count;
                    foreach (string name in names)
                    {
                        archives.DeleteArchiveEntry(output, name);
                        details.Add(new JsonObject { ["name"] = name, ["lines"] = new JsonArray(new JsonObject { ["from"] = "(entry)", ["to"] = "deleted" }) });
                    }

                    changed = names.Count;
                }
                else if (archives.ResolveBntx(output, "") is { } bntx)
                    changed = EditTextures(c, output, bntx.Data, glob, ops, details, ref matchedCount);
                else
                {
                    int matched = 0;
                    changed = archives.EditEntries(output, (name, data) =>
                    {
                        if (!glob.IsMatch(name)) return null;
                        matched++;
                        string? kind = kinds.KindOf(name, data);
                        if (kind is null)
                        {
                            warnings.Add($"{relative}: {name} is not a type that can be edited");
                            return null;
                        }

                        string text = handlers.ReadContent(data, name);
                        string after = Transform(text);
                        if (after == text) return null;
                        details.Add(new JsonObject { ["name"] = name, ["lines"] = LineChanges(text, after) });
                        return handlers.WriteContent(kind, data, after, name);
                    });
                    matchedCount = matched;
                }

                if (matchedCount == 0) warnings.Add($"{relative}: nothing inside it matched {pattern}");
            }
            else
            {
                matchedCount = 1;
                byte[] data = File.ReadAllBytes(output);
                string kind = kinds.KindOf(output, data) ?? throw new InvalidOperationException($"{relative} is not a type that can be edited");
                string text = handlers.ReadContent(data, output);
                string after = Transform(text);
                changed = after == text ? 0 : 1;
                if (changed > 0)
                {
                    File.WriteAllBytes(output, handlers.WriteContent(kind, data, after, output));
                    details.Add(new JsonObject { ["name"] = "(whole file)", ["lines"] = LineChanges(text, after) });
                }
            }

            changedByFile[relative] = changedByFile.GetValueOrDefault(relative) + changed;
            edited.Add(Summary(relative, edit.Pattern, matchedCount, changed));
        }

        JsonObject result = new()
        {
            ["output"] = outputRoot,
            ["files"] = new JsonArray([.. changedByFile.Select(file => (JsonNode)new JsonObject { ["file"] = file.Key, ["changed"] = file.Value })]),
            ["edits"] = edited,
            ["warnings"] = warnings,
        };
        if (preview)
        {
            result["preview"] = true;
            result["details"] = new JsonObject(entriesByFile.Select(file => KeyValuePair.Create<string, JsonNode?>(file.Key, file.Value)));
        }

        return result;
    }

    private static JsonObject Summary(string file, string? pattern, int matched, int changed)
        => new() { ["file"] = file, ["pattern"] = pattern, ["matched"] = matched, ["changed"] = changed };

    // The lines that differ between two texts, as far as it is clear which line became which.
    private static JsonArray LineChanges(string before, string after)
    {
        const int Limit = 8;
        string[] a = before.Split('\n'), b = after.Split('\n');
        JsonArray lines = [];
        if (a.Length == b.Length)
        {
            for (int i = 0; i < a.Length && lines.Count < Limit; i++)
                if (a[i] != b[i]) lines.Add(new JsonObject { ["from"] = a[i].Trim(), ["to"] = b[i].Trim() });
        }
        else lines.Add(new JsonObject { ["from"] = $"{a.Length} lines", ["to"] = $"{b.Length} lines" });

        return lines;
    }

    private static int EditTextures(CommandContext c, string output, byte[] bntx, Regex glob, List<Op> ops, JsonArray details, ref int matched)
    {
        if (ops.OfType<TextOp>().FirstOrDefault(op => op.Pixels is null) is { } unsupported)
            throw new InvalidDataException($"'{unsupported.Name}' cannot change a texture ({Path.GetFileName(output)}); textures can be tinted");

        int changed = 0;
        foreach (string name in BntxNames.List(bntx).Where(n => glob.IsMatch(n)))
        {
            matched++;
            string? before = null, afterColour = null;
            byte[]? updated = Textures.EditBntxPixels(bntx, name, rgba =>
            {
                before = MeanColour(rgba);
                foreach (var op in ops) if (op is TextOp { Pixels: { } pixels }) rgba = pixels(rgba);
                afterColour = MeanColour(rgba);
                return rgba;
            });
            if (updated is null) continue;
            bntx = updated;
            changed++;
            details.Add(new JsonObject { ["name"] = name, ["lines"] = new JsonArray(new JsonObject { ["from"] = $"average colour {before}", ["to"] = $"average colour {afterColour}" }) });
        }

        if (changed > 0)
        {
            if (c.Env.IsCompressedName(output)) bntx = c.Containers.Compress(bntx, output, wasZstd: true, wasYaz0: false);
            File.WriteAllBytes(output, bntx);
        }

        return changed;
    }

    private static string MeanColour(byte[] rgba)
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] == 0) continue;
            r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; n++;
        }

        return n == 0 ? "#000000" : $"#{r / n:X2}{g / n:X2}{b / n:X2}";
    }

    // ---- the steps ----

    private abstract record Op;

    /// <summary>A change to a file's text, and to the pixels of a texture when the step can do that too.</summary>
    private sealed record TextOp(Func<string, string> Text, Func<byte[], byte[]>? Pixels, string Name) : Op;

    private sealed record CopyOp(string From) : Op;

    private sealed record DeleteOp : Op;

    private static void Validate(Edit edit, List<Op> ops)
    {
        for (int i = 0; i < ops.Count; i++)
        {
            if (ops[i] is CopyOp && (edit.Pattern is not null || i > 0))
                throw new InvalidDataException($"{edit.File}: 'copy' is a step for a whole file and has to come first");
            if (ops[i] is DeleteOp && ops.Count > 1)
                throw new InvalidDataException($"{edit.File}: 'delete' cannot be combined with other steps");
        }
    }

    private static Op Step(string step, Dictionary<string, string> variables)
    {
        step = step.Trim();
        int space = step.IndexOf(' ');
        string name = space < 0 ? step : step[..space], rest = space < 0 ? "" : step[(space + 1)..].Trim();
        switch (name)
        {
            case "tint":
            {
                int only = rest.IndexOf(" only ", StringComparison.Ordinal);
                string color = Substitute(only < 0 ? rest : rest[..only], variables).Trim();
                double times = 1;
                if (Regex.Match(color, @"^(.*?)\s+x\s*([\d.]+)$") is { Success: true } scaled)
                {
                    color = scaled.Groups[1].Value.Trim();
                    times = double.Parse(scaled.Groups[2].Value, CultureInfo.InvariantCulture);
                }

                return new TextOp(Tint.Make(color, only < 0 ? null : Substitute(rest[(only + 6)..], variables).Trim(), times), Tint.Pixels(color, times), "tint");
            }
            case "replace":
            {
                int arrow = rest.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow < 0) throw new InvalidDataException($"\"{step}\" needs ' -> ': replace FIND -> WITH");
                Regex find = new(Substitute(rest[..arrow], variables), RegexOptions.Multiline | RegexOptions.CultureInvariant);
                string with = Substitute(rest[(arrow + 4)..], variables).Replace("\\n", "\n");
                return new TextOp(text => find.Replace(text, with), null, "replace");
            }
            case "copy":
                if (rest.Length == 0) throw new InvalidDataException("\"copy\" needs a file: copy Some/File.ext");
                return new CopyOp(Substitute(rest, variables).Trim().Trim('"', '\''));
            case "delete":
                if (rest.Length > 0) throw new InvalidDataException($"\"{step}\": delete takes no arguments");
                return new DeleteOp();
            default:
                throw new InvalidDataException($"Unknown step \"{step}\"; the steps are 'tint COLOR [x TIMES] [only REGEX]', 'replace FIND -> WITH', 'copy FILE' and 'delete'");
        }
    }

    /// <summary>Recolours the colours in an emitter's or a material's YAML, or the pixels of a texture, keeping each one's brightness.</summary>
    internal static partial class Tint
    {
        [GeneratedRegex(@"\[\s*(-?[\d.eE+-]+)\s*,\s*(-?[\d.eE+-]+)\s*,\s*(-?[\d.eE+-]+)\s*(?:,\s*(-?[\d.eE+-]+)\s*)?\]")]
        private static partial Regex Vector();

        [GeneratedRegex(@"^\s*(?:ParticleColor|GlobalColor)[01]/(?:ParticleColor|Color):")]
        private static partial Regex EmitterChannel();

        [GeneratedRegex(@"name:\s*([^,}]*(?:colou?r|emission|albedo)[^,}]*),\s*(?:type:\s*Float[34]\b|values:)", RegexOptions.IgnoreCase)]
        private static partial Regex MaterialColor();

        [GeneratedRegex(@"^\s*[\w/]+/\w+:|^\s*\w+:\s*$")]
        private static partial Regex OtherKey();

        private static double[] Target(string color)
        {
            string digits = color.Trim().Trim('"', '\'').TrimStart('#');
            if (digits.Length is not (6 or 8) || !digits.All(Uri.IsHexDigit)) throw new ArgumentException($"Colour \"{color}\" is not #RRGGBB");
            return [.. Enumerable.Range(0, 3).Select(i => Convert.ToInt32(digits.Substring(i * 2, 2), 16) / 255.0)];
        }

        /// <summary>The same recolour for the pixels of a texture (RGBA8): each pixel becomes the colour at its own brightness; alpha stays.</summary>
        public static Func<byte[], byte[]> Pixels(string color, double times = 1)
        {
            double[] target = Target(color);
            return rgba =>
            {
                for (int i = 0; i + 3 < rgba.Length; i += 4)
                {
                    double brightness = times * Math.Max(rgba[i], Math.Max(rgba[i + 1], rgba[i + 2])) / 255.0;
                    for (int k = 0; k < 3; k++) rgba[i + k] = (byte)Math.Clamp((int)Math.Round(target[k] * brightness * 255.0), 0, 255);
                }

                return rgba;
            };
        }

        public static Func<string, string> Make(string color, string? only, double times = 1)
        {
            double[] target = Target(color);
            Regex? restriction = only is null ? null : new Regex(only, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            string Number(double value) => Math.Round(value, 6).ToString("R", CultureInfo.InvariantCulture);

            string Recolour(Match match)
            {
                double brightness = times * Enumerable.Range(1, 3).Max(i => double.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture));
                string alpha = match.Groups[4].Success ? ", " + match.Groups[4].Value : "";
                return $"[{string.Join(", ", target.Select(t => Number(t * brightness)))}{alpha}]";
            }

            return text =>
            {
                List<string> lines = [];
                bool inChannel = false;
                string channel = "";
                foreach (string line in text.Split('\n'))
                {
                    string result = line;
                    if (EmitterChannel().IsMatch(line))
                    {
                        inChannel = true;
                        channel = line.Trim().Split(':')[0];
                    }
                    else if (OtherKey().IsMatch(line) && !line.TrimStart().StartsWith('-')) inChannel = false;

                    if (inChannel && line.Contains('[') && (restriction is null || restriction.IsMatch(channel)))
                        result = Vector().Replace(line, Recolour);
                    else if (MaterialColor().Match(line) is { Success: true } material && line.Contains("values:") && (restriction is null || restriction.IsMatch(material.Groups[1].Value)))
                        result = Vector().Replace(line, Recolour);
                    lines.Add(result);
                }

                return string.Join('\n', lines);
            };
        }
    }
}

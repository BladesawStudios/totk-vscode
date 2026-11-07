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
/// color: "#FF0000"
///
/// Effect/static.Nin_Ounce_NVN2.vsetb.byml.zs:
///   Esets/Npc_Navi_*/*.emitter: tint $color
///
/// Model/Npc_Navi.Npc_Navi.bfres.zs:
///   Models/Npc_Navi/Materials/*.fmat: tint $color only ns_param_color[0-5]
/// </code>
/// A key with a plain value is a variable (<c>$color</c> or <c>${color}</c> in a step). A key with a map under it is a file
/// in the game's RomFS, and each key of the map is a pattern for the files inside it (<c>*</c> inside a folder, <c>**</c>
/// across folders) with the step, or a list of steps, to run on them. A file with steps directly under it (a string or a
/// list) runs them on the file itself. A step is <c>tint COLOR [only REGEX]</c> or <c>replace FIND -> WITH</c>. The changed
/// files are written to a <c>romfs</c> folder next to the recipe, and each run starts again from the game's own files.
/// </remarks>
public static partial class RecipeCommands
{
    public static void Register(Dictionary<string, CommandHandler> commands)
    {
        commands["recipe-info"] = Info;
        commands["apply-recipe"] = Apply;
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

    private static Recipe Load(string path)
    {
        object? root;
        try
        {
            root = Yaml.Deserialize<object?>(File.ReadAllText(path));
        }
        catch (YamlDotNet.Core.YamlException error)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}, line {error.Start.Line}: {error.InnerException?.Message ?? error.Message}");
        }

        if (root is not Dictionary<object, object> top) throw new InvalidDataException("A recipe is a map of variables and files");

        Dictionary<string, string> variables = new(StringComparer.Ordinal);
        List<Edit> edits = [];
        foreach (var (key, value) in top)
        {
            string name = Scalar(key);
            if (value is Dictionary<object, object> entries)
                foreach (var (pattern, steps) in entries) edits.Add(new Edit(name, Scalar(pattern), StepList(steps)));
            else if (value is List<object>)
                edits.Add(new Edit(name, null, StepList(value)));
            else if (Scalar(value).StartsWith("tint ") || Scalar(value).StartsWith("replace "))
                edits.Add(new Edit(name, null, [Scalar(value)]));
            else
                variables[name] = Scalar(value);
        }

        if (edits.Count == 0) throw new InvalidDataException("The recipe has no files to change");
        return new Recipe(variables, edits);
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
            if (name.All(char.IsAsciiDigit)) return match.Value; // $1 in a replace is the regex's own group
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

    private static JsonNode Apply(CommandContext c)
    {
        string recipePath = Path.GetFullPath(c.Arg(0));
        Recipe recipe = Load(recipePath);
        Dictionary<string, string> variables = WithOverrides(recipe, c.Args.Skip(1));
        if (c.Romfs.Length == 0) throw new InvalidOperationException("Set the game's RomFS folder first: recipes read the game's own files from it.");

        string outputRoot = Path.Combine(Path.GetDirectoryName(recipePath)!, OutputFolder);
        FileKinds kinds = new(c.Env, c.Containers, c.Plugin);
        FormatHandlers handlers = new(c.Env, c.Containers, kinds, c.Plugin);
        Archives archives = new(c.Containers);

        // Every step is read and its variables filled in before any file is touched, so a mistake stops the run early.
        List<(Edit Edit, List<Func<string, string>> Steps)> plan = [];
        foreach (Edit edit in recipe.Edits) plan.Add((edit, [.. edit.Steps.Select(step => Step(step, variables))]));

        HashSet<string> copied = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> changedByFile = new(StringComparer.OrdinalIgnoreCase);
        JsonArray warnings = [];
        using IDisposable fit = Vfx3Text.FitToStorage();
        foreach (var (edit, steps) in plan)
        {
            string relative = edit.File.Replace('\\', '/').TrimStart('/');
            string source = Path.Combine(c.Romfs, relative), output = Path.Combine(outputRoot, relative);
            if (!File.Exists(source)) throw new FileNotFoundException($"{relative} is not in the RomFS ({c.Romfs})");
            if (copied.Add(output))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(source, output, overwrite: true);
            }

            string Transform(string text)
            {
                foreach (var step in steps) text = step(text);
                return text;
            }

            int changed;
            if (edit.Pattern is { } pattern)
            {
                Regex glob = Glob(pattern);
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
                    string edited = Transform(text);
                    return edited == text ? null : handlers.WriteContent(kind, data, edited, name);
                });
                if (matched == 0) warnings.Add($"{relative}: nothing inside it matched {pattern}");
            }
            else
            {
                byte[] data = File.ReadAllBytes(output);
                string kind = kinds.KindOf(output, data) ?? throw new InvalidOperationException($"{relative} is not a type that can be edited");
                string text = handlers.ReadContent(data, output);
                string edited = Transform(text);
                changed = edited == text ? 0 : 1;
                if (changed > 0) File.WriteAllBytes(output, handlers.WriteContent(kind, data, edited, output));
            }

            changedByFile[relative] = changedByFile.GetValueOrDefault(relative) + changed;
        }

        return new JsonObject
        {
            ["output"] = outputRoot,
            ["files"] = new JsonArray([.. changedByFile.Select(file => (JsonNode)new JsonObject { ["file"] = file.Key, ["changed"] = file.Value })]),
            ["warnings"] = warnings,
        };
    }

    // ---- the steps ----

    private static Func<string, string> Step(string step, Dictionary<string, string> variables)
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
                return Tint.Make(color, only < 0 ? null : Substitute(rest[(only + 6)..], variables).Trim());
            }
            case "replace":
            {
                int arrow = rest.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow < 0) throw new InvalidDataException($"\"{step}\" needs ' -> ': replace FIND -> WITH");
                Regex find = new(Substitute(rest[..arrow], variables), RegexOptions.Multiline | RegexOptions.CultureInvariant);
                string with = Substitute(rest[(arrow + 4)..], variables).Replace("\\n", "\n");
                return text => find.Replace(text, with);
            }
            default:
                throw new InvalidDataException($"Unknown step \"{step}\"; the steps are 'tint COLOR [only REGEX]' and 'replace FIND -> WITH'");
        }
    }

    /// <summary>Recolours the colours in an emitter's or a material's YAML, keeping each one's brightness.</summary>
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

        public static Func<string, string> Make(string color, string? only)
        {
            string digits = color.TrimStart('#');
            if (digits.Length is not (6 or 8) || !digits.All(Uri.IsHexDigit)) throw new ArgumentException($"Colour \"{color}\" is not #RRGGBB");
            double[] target = [.. Enumerable.Range(0, 3).Select(i => Convert.ToInt32(digits.Substring(i * 2, 2), 16) / 255.0)];
            Regex? restriction = only is null ? null : new Regex(only, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            string Number(double value) => Math.Round(value, 6).ToString("R", CultureInfo.InvariantCulture);

            string Recolour(Match match)
            {
                double brightness = Enumerable.Range(1, 3).Max(i => double.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture));
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

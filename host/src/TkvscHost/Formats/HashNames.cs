using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TkvscHost.Formats;

/// <summary>
/// Names for the 32-bit MurmurHash3 values the game keeps its flags and save variables under (the <c>Hash</c> of every entry
/// in <c>GameDataList</c>). The names come from <c>vendor/zelda-totk.hashes.csv</c>, cracked and compiled by MacSpazzy and
/// MrCheeze (as used by Marc Robledo's TotK save editor). The editor shows a name as a comment after the hash; the comment is
/// dropped when the text is saved, so the hash itself is what is written back.
/// </summary>
public sealed partial class HashNames
{
    public const string FileName = "zelda-totk.hashes.csv";

    private readonly Dictionary<uint, string> _names;

    private HashNames(Dictionary<uint, string> names) => _names = names;

    public static readonly HashNames Empty = new([]);

    public int Count => _names.Count;

    public bool TryGet(uint hash, out string name) => _names.TryGetValue(hash, out name!);

    private static readonly object Gate = new();
    private static string? _loadedFrom;
    private static HashNames? _loaded;

    /// <summary>The names from the extension's vendor folder, read once; empty when the file is not there.</summary>
    public static HashNames Load(Env env)
    {
        string path = Path.Combine(env.ExtensionRoot, "vendor", FileName);
        lock (Gate)
        {
            if (_loaded is not null && _loadedFrom == path) return _loaded;
            _loadedFrom = path;
            return _loaded = File.Exists(path) ? Parse(File.ReadAllLines(path, Encoding.UTF8)) : Empty;
        }
    }

    /// <summary>Lines of <c>hash;type;name</c>. Comments, enum value lists, wildcards and placeholder names are skipped.</summary>
    public static HashNames Parse(IEnumerable<string> lines)
    {
        Dictionary<uint, string> names = [];
        foreach (string line in lines)
        {
            if (line.Length == 0 || line[0] == '#') continue;

            string[] parts = line.Split(';');
            if (parts.Length < 3 || parts[0].Length != 8) continue;
            if (!uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash)) continue;

            string name = parts[2].Trim();
            if (name.Length == 0 || name == "Unknown" || name.Contains('*') || name.Contains(' ')) continue;
            names.TryAdd(hash, name);
        }
        return new HashNames(names);
    }

    [GeneratedRegex(@"^(?<head>[ \t]*(?:-[ \t]+)?Hash:[ \t]*!u[ \t]+0x(?<hex>[0-9a-fA-F]{8}))[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex HashLine();

    /// <summary>Adds <c># Name</c> after each <c>Hash: !u 0x...</c> line whose hash has a name.</summary>
    public string Annotate(string yaml)
    {
        if (_names.Count == 0 || !yaml.Contains("Hash:", StringComparison.Ordinal)) return yaml;

        return HashLine().Replace(yaml, m =>
            _names.TryGetValue(uint.Parse(m.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), out string? name)
                ? $"{m.Groups["head"].Value}  # {name}"
                : m.Value);
    }
}

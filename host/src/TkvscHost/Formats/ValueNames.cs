using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TkvscHost.Formats;

/// <summary>
/// Names for hashed BYML values. The game stores an enum value, such as an ocarina note, as the 32-bit MurmurHash3
/// (seed 0) of its name in a <c>!u</c> field. A value whose name is in the game's list (profile <c>valueNamesPath</c>)
/// is shown as <c>!hash Name</c>; any <c>!hash Name</c> in edited text, listed or not, is written back as that name's
/// hash, so a value round-trips exactly.
/// </summary>
public sealed partial class ValueNames
{
    private readonly Dictionary<uint, string> _names;

    private ValueNames(Dictionary<uint, string> names) => _names = names;

    public static readonly ValueNames Empty = new([]);

    public int Count => _names.Count;

    public bool TryGet(uint hash, out string name) => _names.TryGetValue(hash, out name!);

    private static readonly object Gate = new();
    private static string? _loadedFrom;
    private static ValueNames? _loaded;

    /// <summary>The active game's names, read once; empty when it has none or the file is not there.</summary>
    public static ValueNames Load(Env env)
    {
        string path = env.ValueNamesPath;
        lock (Gate)
        {
            if (_loaded is not null && _loadedFrom == path) return _loaded;
            _loadedFrom = path;
            return _loaded = path.Length > 0 && File.Exists(path) ? Parse(File.ReadAllLines(path, Encoding.UTF8)) : Empty;
        }
    }

    /// <summary>One name per line. Blank lines and lines starting with <c>#</c> are skipped; the first name of a hash wins.</summary>
    public static ValueNames Parse(IEnumerable<string> lines)
    {
        Dictionary<uint, string> names = [];
        foreach (string raw in lines)
        {
            string name = raw.Trim();
            if (name.Length == 0 || name[0] == '#' || !NamePattern().IsMatch(name)) continue;
            names.TryAdd(Murmur3(name), name);
        }
        return new ValueNames(names);
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();

    // A tagged unsigned value, as the BYML writer prints it: `!u 0x5e112bf7`.
    [GeneratedRegex(@"!u[ \t]+0x(?<hex>[0-9a-fA-F]{1,8})(?![0-9a-zA-Z_])")]
    private static partial Regex UIntValue();

    // `!hash Name`, `!hash 'Name'` or `!hash "Name"`.
    [GeneratedRegex(@"!hash[ \t]+(?:(?<plain>[A-Za-z_][A-Za-z0-9_]*)(?![A-Za-z0-9_])|'(?<single>[^'\r\n]*)'|""(?<double>[^""\r\n]*)"")")]
    private static partial Regex HashValue();

    /// <summary>Shows each <c>!u</c> value that is a listed name's hash as <c>!hash Name</c>.</summary>
    public string Name(string yaml)
    {
        if (_names.Count == 0 || !yaml.Contains("!u", StringComparison.Ordinal)) return yaml;

        return UIntValue().Replace(yaml, m =>
            _names.TryGetValue(uint.Parse(m.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), out string? name)
                ? $"!hash {name}"
                : m.Value);
    }

    /// <summary>Turns every <c>!hash Name</c> back into <c>!u</c> and the name's hash. Works without a list.</summary>
    public static string Unname(string yaml)
    {
        if (!yaml.Contains("!hash", StringComparison.Ordinal)) return yaml;

        return HashValue().Replace(yaml, m =>
        {
            string name = m.Groups["plain"].Success ? m.Groups["plain"].Value
                : m.Groups["single"].Success ? m.Groups["single"].Value
                : m.Groups["double"].Value;
            return $"!u 0x{Murmur3(name):x8}";
        });
    }

    /// <summary>MurmurHash3 x86 32-bit of the UTF-8 bytes, seed 0.</summary>
    public static uint Murmur3(string text, uint seed = 0)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        const uint c1 = 0xcc9e2d51, c2 = 0x1b873593;
        uint h = seed;
        int blocks = data.Length / 4;

        for (int i = 0; i < blocks; i++)
        {
            uint k = BitConverter.ToUInt32(data, i * 4);
            k *= c1;
            k = uint.RotateLeft(k, 15);
            k *= c2;
            h ^= k;
            h = uint.RotateLeft(h, 13);
            h = h * 5 + 0xe6546b64;
        }

        uint tail = 0;
        int rest = data.Length & 3, at = blocks * 4;
        if (rest >= 3) tail ^= (uint)data[at + 2] << 16;
        if (rest >= 2) tail ^= (uint)data[at + 1] << 8;
        if (rest >= 1)
        {
            tail ^= data[at];
            tail *= c1;
            tail = uint.RotateLeft(tail, 15);
            tail *= c2;
            h ^= tail;
        }

        h ^= (uint)data.Length;
        h ^= h >> 16;
        h *= 0x85ebca6b;
        h ^= h >> 13;
        h *= 0xc2b2ae35;
        h ^= h >> 16;
        return h;
    }
}

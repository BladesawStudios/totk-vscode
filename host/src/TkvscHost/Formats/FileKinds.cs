using System.Text.Json;
using System.Text.Json.Nodes;

namespace TkvscHost.Formats;

/// <summary>
/// Which handler a file gets: the manifest the extension writes maps extensions to handler kinds,
/// and a few kinds are told by their contents. Port of <c>handler_manifest.py</c> and <c>_file_kind</c>.
/// </summary>
public sealed class FileKinds(Env env, Containers containers)
{
    /// <summary>Kinds the host reads and writes. Other kinds in the manifest are for add-ons' own editors, which read raw bytes.</summary>
    public static readonly HashSet<string> Native = ["byml", "msbt", "aamp", "xlnk"];

    private readonly Lazy<Manifest> _manifest = new(() => Manifest.Load(env.HandlerManifestPath));

    private sealed record Manifest(Dictionary<string, string> ExtensionToHandler, HashSet<string> AampExtensions)
    {
        public static Manifest Load(string path)
        {
            Dictionary<string, string> map = new(StringComparer.Ordinal);
            HashSet<string> aamp = new(StringComparer.Ordinal);

            if (path.Length > 0 && File.Exists(path))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject root)
                    {
                        if (root["extensionToHandler"] is JsonObject m)
                            foreach (var (key, value) in m)
                                if (value?.GetValueKind() == JsonValueKind.String) map[key] = value.GetValue<string>();
                        if (root["aampExtensions"] is JsonArray a)
                            foreach (JsonNode? ext in a)
                                if (ext?.GetValueKind() == JsonValueKind.String) aamp.Add(ext.GetValue<string>());
                    }
                }
                catch (Exception e) when (e is IOException or JsonException) { }
            }

            return new Manifest(map, aamp);
        }
    }

    public static string Extension(string logicalPath)
    {
        string lower = logicalPath.ToLowerInvariant().Replace('\\', '/');
        if (lower.EndsWith(".zs", StringComparison.Ordinal)) lower = lower[..^3];
        int dot = lower.LastIndexOf('.');
        return dot < 0 ? "" : lower[(dot + 1)..];
    }

    private string? ExtensionKind(string logicalPath)
    {
        string ext = Extension(logicalPath);
        if (ext.Length == 0) return null;

        Manifest manifest = _manifest.Value;
        if (manifest.AampExtensions.Contains(ext)) return "aamp";
        return manifest.ExtensionToHandler.GetValueOrDefault(ext);
    }

    /// <summary>The handler kind of a file by name, and failing that by what is inside it.</summary>
    public string? KindOf(string logicalPath, byte[]? data = null)
    {
        if (ExtensionKind(logicalPath) is { } byName) return byName;
        if (data is null) return null;

        byte[] plain;
        try
        {
            plain = containers.Decompress(data, logicalPath).Data;
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or FileNotFoundException)
        {
            plain = data;
        }

        if (plain.AsSpan().StartsWith("AAMP"u8)) return "aamp";
        if (plain.AsSpan().StartsWith("XLNK"u8)) return "xlnk";
        return null;
    }
}

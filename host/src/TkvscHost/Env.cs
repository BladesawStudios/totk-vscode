using System.Text.Json;
using System.Text.RegularExpressions;

namespace TkvscHost;

/// <summary>
/// The settings the extension passes in the environment (see <c>getBridgeEnv</c> in the TypeScript side).
/// They keep the names the Python bridge reads.
/// </summary>
public sealed class Env
{
    public string Romfs { get; init; } = "";
    public string GameId { get; init; } = "totk";
    public string CompressionBackend { get; init; } = "totk-zstd";
    public string HandlerManifestPath { get; init; } = "";
    public string AampHashNamesPath { get; init; } = "";
    public string MsbtConfigPath { get; init; } = "";
    public string[] ExtraAampExtensions { get; init; } = [];
    public string[] ArchiveExtensions { get; init; } = [];
    public int BymlInlineContainerMaxCount { get; init; } = 1;
    public string ExtensionRoot { get; init; } = "";
    public bool TagProductYaml { get; init; }
    public bool PtclCompactVectors { get; init; } = true;

    /// <summary>The endings that mark a compressed file's name, each with its leading dot.</summary>
    public string[] CompressedSuffixes { get; init; } = [".zs"];

    /// <summary>The game's zstd dictionary pack, relative to the romfs.</summary>
    public string ZsDicPath { get; init; } = DefaultZsDicPath;

    /// <summary>The file of flag and save-variable hash names for this game; empty when it has none.</summary>
    public string HashNamesPath { get; init; } = "";

    /// <summary>The built-in AAMP name table: <c>totk</c>, <c>botw</c> or <c>none</c>.</summary>
    public string AampNameTable { get; init; } = "totk";

    /// <summary>The game's own AAMP names (same JSON as <c>TKVSC.aampHashNames</c>); empty when it has none.</summary>
    public string ProfileAampNamesPath { get; init; } = "";

    /// <summary>The version a new ELink file is written with; null for the library's default.</summary>
    public uint? XlinkELinkVersion { get; init; }

    /// <summary>The version a new SLink file is written with; null for the library's default.</summary>
    public uint? XlinkSLinkVersion { get; init; }

    /// <summary>The regular expressions (the profile's <c>archivePatterns</c>) that name an archive; empty for the default.</summary>
    public string[] ArchivePatterns { get; init; } = [];

    /// <summary>The active game's plugin DLL; empty when it has none.</summary>
    public string HostPluginPath { get; init; } = "";

    public const string DefaultZsDicPath = "Pack/ZsDic.pack.zs";
    public const string TotkHashNamesFile = "zelda-totk.hashes.csv";

    public static readonly string[] DefaultArchiveExtensions =
    [
        ".pack", ".sarc", ".genvb", ".blarc", ".bfarc", ".bkres", ".bntx",
        ".pack.zs", ".sarc.zs", ".genvb.zs", ".blarc.zs", ".bfarc.zs", ".bkres.zs", ".bntx.zs",
    ];

    private static readonly Regex DefaultArchivePattern = new(@"\.(pack|sarc|genvb|blarc|bfarc|bkres|bntx)(\.zs)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private Regex[]? _archivePatterns;

    /// <summary>Whether a path segment names an archive, by the same patterns as the extension's <c>archiveRegistry</c>, with or without a compressed suffix.</summary>
    public bool IsArchiveName(string name)
    {
        _archivePatterns ??= CompileArchivePatterns(ArchivePatterns);
        string normalized = name.Replace('\\', '/');
        string stripped = StripCompressedSuffix(normalized);
        return _archivePatterns.Any(pattern => pattern.IsMatch(normalized) || pattern.IsMatch(stripped));
    }

    /// <summary>Whether a file's name says it is compressed.</summary>
    public bool IsCompressedName(string name) => CompressedSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The name without its compressed suffix, if it has one.</summary>
    public string StripCompressedSuffix(string name)
    {
        foreach (string suffix in CompressedSuffixes)
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length];
        return name;
    }

    // Like the extension, a pattern that doesn't compile is skipped, and with none left the default applies.
    private static Regex[] CompileArchivePatterns(string[] sources)
    {
        List<Regex> patterns = [];
        foreach (string source in sources)
        {
            try
            {
                patterns.Add(new Regex(source, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }
            catch (ArgumentException)
            {
            }
        }

        return patterns.Count > 0 ? [.. patterns] : [DefaultArchivePattern];
    }

    public static Env FromEnvironment() => From(name => Environment.GetEnvironmentVariable(name));

    public static Env From(Func<string, string?> get)
    {
        string Get(string name) => (get(name) ?? "").Trim();

        string romfs = Get("TKVSC_ROMFS");
        if (romfs.Length == 0) romfs = Get("TOTK_EDITOR_ROMFS");

        string[] List(string value) => value.Length == 0
            ? []
            : [.. value.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0)];

        string[] archiveExtensions = List(Get("TKVSC_ARCHIVE_EXTENSIONS"));
        string gameId = Get("TKVSC_GAME_ID").Length > 0 ? Get("TKVSC_GAME_ID") : "totk";
        string extensionRoot = FindExtensionRoot(Get("TKVSC_EXTENSION_ROOT"));

        // The extension always says which names a game has. Run by hand, TotK still finds its own.
        string hashNames = Get("TKVSC_HASH_NAMES");
        if (hashNames.Length == 0 && get("TKVSC_HASH_NAMES") is null && gameId == "totk")
            hashNames = Path.Combine(extensionRoot, "vendor", TotkHashNamesFile);

        return new Env
        {
            Romfs = romfs,
            GameId = gameId,
            CompressionBackend = Get("TKVSC_COMPRESSION_BACKEND").Length > 0 ? Get("TKVSC_COMPRESSION_BACKEND") : "totk-zstd",
            HandlerManifestPath = Get("TKVSC_HANDLER_MANIFEST"),
            AampHashNamesPath = Get("TKVSC_AAMP_HASH_NAMES"),
            MsbtConfigPath = Get("TKVSC_MSBT_CONFIG"),
            ExtraAampExtensions = [.. List(Get("TOTK_EXTRA_AAMP_EXTS")).Select(e => e.TrimStart('.').ToLowerInvariant())],
            ArchiveExtensions = archiveExtensions.Length > 0 ? archiveExtensions : DefaultArchiveExtensions,
            BymlInlineContainerMaxCount = int.TryParse(Get("TOTK_BYML_INLINE_CONTAINER_MAX_COUNT"), out int max) ? max : 1,
            ExtensionRoot = extensionRoot,
            TagProductYaml = Get("TOTK_TAG_PRODUCT_FORMAT") == "yaml",
            PtclCompactVectors = Get("TOTK_PTCL_VECTOR_FORMAT") != "expanded",
            CompressedSuffixes = List(Get("TKVSC_COMPRESSED_SUFFIXES")) is { Length: > 0 } suffixes ? [.. suffixes.Select(s => s.StartsWith('.') ? s : "." + s)] : [".zs"],
            ZsDicPath = Get("TKVSC_ZSDIC_PATH").Length > 0 ? Get("TKVSC_ZSDIC_PATH") : DefaultZsDicPath,
            HashNamesPath = hashNames,
            AampNameTable = Get("TKVSC_AAMP_NAME_TABLE").Length > 0 ? Get("TKVSC_AAMP_NAME_TABLE").ToLowerInvariant() : "totk",
            ProfileAampNamesPath = Get("TKVSC_PROFILE_AAMP_NAMES"),
            XlinkELinkVersion = ParseVersion(Get("TKVSC_XLINK_ELINK_VERSION")),
            XlinkSLinkVersion = ParseVersion(Get("TKVSC_XLINK_SLINK_VERSION")),
            ArchivePatterns = ParseStringArray(Get("TKVSC_ARCHIVE_PATTERNS")),
            HostPluginPath = Get("TKVSC_HOST_PLUGIN"),
        };
    }

    private static uint? ParseVersion(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint hex) ? hex : null;
        return uint.TryParse(text, out uint value) ? value : null;
    }

    // A JSON array, since a regular expression can hold any separator.
    private static string[] ParseStringArray(string text)
    {
        if (text.Length == 0) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(text) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // The folder with config/ and vendor/. The extension sets it; otherwise look upward from the executable.
    private static string FindExtensionRoot(string given)
    {
        if (given.Length > 0 && Directory.Exists(given)) return given;

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "config", "aamp-extensions.json"))) return dir.FullName;

        return "";
    }
}

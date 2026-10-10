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

    public static readonly string[] DefaultArchiveExtensions =
    [
        ".pack", ".sarc", ".genvb", ".blarc", ".bfarc", ".bkres", ".bntx",
        ".pack.zs", ".sarc.zs", ".genvb.zs", ".blarc.zs", ".bfarc.zs", ".bkres.zs", ".bntx.zs",
    ];

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

        return new Env
        {
            Romfs = romfs,
            GameId = Get("TKVSC_GAME_ID").Length > 0 ? Get("TKVSC_GAME_ID") : "totk",
            CompressionBackend = Get("TKVSC_COMPRESSION_BACKEND").Length > 0 ? Get("TKVSC_COMPRESSION_BACKEND") : "totk-zstd",
            HandlerManifestPath = Get("TKVSC_HANDLER_MANIFEST"),
            AampHashNamesPath = Get("TKVSC_AAMP_HASH_NAMES"),
            MsbtConfigPath = Get("TKVSC_MSBT_CONFIG"),
            ExtraAampExtensions = [.. List(Get("TOTK_EXTRA_AAMP_EXTS")).Select(e => e.TrimStart('.').ToLowerInvariant())],
            ArchiveExtensions = archiveExtensions.Length > 0 ? archiveExtensions : DefaultArchiveExtensions,
            BymlInlineContainerMaxCount = int.TryParse(Get("TOTK_BYML_INLINE_CONTAINER_MAX_COUNT"), out int max) ? max : 1,
            ExtensionRoot = FindExtensionRoot(Get("TKVSC_EXTENSION_ROOT")),
            TagProductYaml = Get("TOTK_TAG_PRODUCT_FORMAT") == "yaml",
            PtclCompactVectors = Get("TOTK_PTCL_VECTOR_FORMAT") != "expanded",
        };
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

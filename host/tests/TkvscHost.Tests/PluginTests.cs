using System.Text.Json.Nodes;
using SarcSharp;
using Tkvsc.Plugins;
using TkvscHost.Plugins;

namespace TkvscHost.Tests;

public class PluginTests
{
    private static string SampleFolder => Path.Combine(AppContext.BaseDirectory, "plugins", "sample");
    private static string SampleDll => Path.Combine(SampleFolder, "SamplePlugin.dll");

    private static Env Env(string plugin, string? manifest = null) => TkvscHost.Env.From(name => name switch
    {
        "TKVSC_GAME_ID" => "sample",
        "TKVSC_COMPRESSION_BACKEND" => "plugin",
        "TKVSC_HOST_PLUGIN" => plugin,
        "TKVSC_HANDLER_MANIFEST" => manifest,
        "TKVSC_ARCHIVE_PATTERNS" => """["\\.spack$", "\\.pack$"]""",
        "TKVSC_COMPRESSED_SUFFIXES" => ".lz,.zs",
        _ => null,
    });

    // A file in the sample's archive format: "SPAK", the count, then each name and its bytes.
    private static byte[] Spak(params (string Name, byte[] Data)[] files)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("SPAK"u8);
        writer.Write(files.Length);
        foreach (var (name, data) in files)
        {
            writer.Write(name);
            writer.Write(data.Length);
            writer.Write(data);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] Smpl(string text) => [.. "SMPL"u8.ToArray(), .. Workspace.Text(text)];

    private static string[] Listed(JsonNode? result) => [.. result!.AsArray().Select(n => n!.GetValue<string>())];

    private static JsonNode List(Workspace w, string archive, string inner = "")
    {
        Env env = Env(SampleDll, Manifest(w));
        GamePlugin plugin = PluginLoader.Load(env);
        return Commands.Find("list")!(new CommandContext("list", [archive, inner], env, () => [], plugin));
    }

    [Fact]
    public void A_plugin_archive_lists_reads_writes_deletes_and_renames()
    {
        using Workspace w = new();
        string archive = w.Path("data.spack");
        File.WriteAllBytes(archive, Spak(("a/one.smpl", Smpl("one")), ("b/two.smpl", Smpl("two"))));

        Assert.Equal(["a/one.smpl", "b/two.smpl"], Listed(List(w, archive)));
        Assert.Equal("two", Run(w, "read", "", archive, "b/two.smpl")["content"]!.GetValue<string>());

        Run(w, "write", "edited", archive, "a/one.smpl");
        Assert.Equal("edited", Run(w, "read", "", archive, "a/one.smpl")["content"]!.GetValue<string>());
        Assert.True(File.ReadAllBytes(archive).AsSpan().StartsWith("SPAK"u8));

        Run(w, "rename-entry", "", archive, "a/one.smpl", "a/renamed.smpl");
        Run(w, "delete-entry", "", archive, "b/two.smpl");
        Assert.Equal(["a/renamed.smpl"], Listed(List(w, archive)));
    }

    [Fact]
    public void A_plugin_archive_nests_with_sarc_both_ways_and_keeps_its_compression()
    {
        using Workspace w = new();

        // A compressed plugin archive holding a SARC holding another compressed plugin archive.
        byte[] innermost = Compressed(Spak(("deep.smpl", Smpl("deep"))));
        byte[] middle = Workspace.Sarc(("in/inner.spack", innermost)).Write();
        string archive = w.Path("outer.spack");
        File.WriteAllBytes(archive, Compressed(Spak(("mid.pack", middle))));

        const string path = "mid.pack/in/inner.spack/deep.smpl";
        Assert.Equal("deep", Run(w, "read", "", archive, path)["content"]!.GetValue<string>());

        Run(w, "write", "changed", archive, path);

        Assert.True(File.ReadAllBytes(archive).AsSpan().StartsWith("SMZ1"u8));
        Assert.Equal("changed", Run(w, "read", "", archive, path)["content"]!.GetValue<string>());
    }

    [Fact]
    public void A_read_only_plugin_archive_reports_it_and_leaves_the_file_alone()
    {
        using Workspace w = new();
        string archive = w.Path("data.spack");
        byte[] before = Spak(("a.smpl", Smpl("one")));
        File.WriteAllBytes(archive, before);

        Env env = Env(SampleDll, Manifest(w));
        GamePlugin plugin = new(env);
        plugin.AddArchiveFormat(new ReadOnlyArchive());

        NotSupportedException e = Assert.Throws<NotSupportedException>(() => new Archives(new Containers(env, plugin)).DeleteArchiveEntry(archive, "a.smpl"));
        Assert.Contains("read-only", e.Message);
        Assert.Equal(before, File.ReadAllBytes(archive));
    }

    private sealed class ReadOnlyArchive : IArchiveFormat
    {
        public bool Matches(ReadOnlySpan<byte> data) => data.StartsWith("SPAK"u8);
        public IReadOnlyList<ArchiveFile> Read(byte[] data, string logicalPath) => [new("a.smpl", [1])];
        public byte[] Write(IReadOnlyList<ArchiveFile> files, byte[] original, string logicalPath) => throw new NotSupportedException("This format is read-only.");
    }

    [Fact]
    public void A_custom_suffix_finds_the_format_and_recompresses_on_save()
    {
        using Workspace w = new();
        string file = w.Path("thing.smpl.lz");
        File.WriteAllBytes(file, Compressed(Smpl("hello")));

        Assert.Equal("hello", Run(w, "read-disk", "", file)["content"]!.GetValue<string>());

        Run(w, "write-disk", "edited", file);
        Assert.Equal(Compressed(Smpl("edited")), File.ReadAllBytes(file));
    }

    [Fact]
    public void A_new_file_with_a_custom_suffix_comes_out_compressed()
    {
        using Workspace w = new();
        string archive = w.Path("data.spack");
        File.WriteAllBytes(archive, Spak(("a.smpl", Smpl("one"))));

        Run(w, "write", "fresh", archive, "new.smpl.lz");

        string stored = Run(w, "export-stored", "", archive, "new.smpl.lz")["path"]!.GetValue<string>();
        Assert.True(File.ReadAllBytes(stored).AsSpan().StartsWith("SMZ1"u8));
        File.Delete(stored);
        Assert.Equal("fresh", Run(w, "read", "", archive, "new.smpl.lz")["content"]!.GetValue<string>());
    }

    [Fact]
    public void An_archive_pattern_also_matches_with_a_custom_suffix()
    {
        Env env = Env("");
        Assert.True(env.IsArchiveName("data.spack.lz"));
        Assert.True(env.IsArchiveName("data.spack.ZS"));
        Assert.False(env.IsArchiveName("data.lz"));
        Assert.Equal("data.spack", env.StripCompressedSuffix("data.spack.LZ"));
        Assert.False(env.IsCompressedName("data.spack"));
    }

    [Fact]
    public void Without_a_setting_the_suffix_is_zs()
    {
        Env env = new();
        Assert.True(env.IsCompressedName("a.byml.zs"));
        Assert.False(env.IsCompressedName("a.byml.lz"));
        Assert.Equal("a.zs", TkvscHost.Env.From(_ => null).StripCompressedSuffix("a.zs.zs"));
    }

    private static string Manifest(Workspace w)
    {
        string path = w.Path("manifest.json");
        File.WriteAllText(path, """{ "version": 1, "extensionToHandler": { "smpl": "sample-text" }, "aampExtensions": [] }""");
        return path;
    }

    private static JsonObject Run(Workspace w, string command, string stdin, params string[] args)
    {
        Env env = Env(SampleDll, Manifest(w));
        GamePlugin plugin = PluginLoader.Load(env);
        CommandHandler handler = plugin.FindCommand(command) ?? Commands.Find(command) ?? throw new InvalidOperationException(command);
        return (JsonObject)handler(new CommandContext(command, args, env, () => Workspace.Text(stdin), plugin));
    }

    // The sample's own compression: "SMZ1" followed by the bytes inverted.
    private static byte[] Compressed(byte[] data) => [.. "SMZ1"u8.ToArray(), .. data.Select(b => (byte)~b)];

    [Fact]
    public void A_game_without_a_plugin_gets_none()
    {
        Assert.Same(GamePlugin.None, PluginLoader.Load(new Env()));
    }

    [Fact]
    public void A_private_dependency_loads_from_the_plugin_folder_and_commands_get_their_input()
    {
        using Workspace w = new();
        JsonObject result = Run(w, "sample-info", "from stdin", "one", "two");

        Assert.Equal("sample", result["gameId"]!.GetValue<string>());
        Assert.Equal("private dependency loaded", result["helper"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(SampleFolder), result["pluginDirectory"]!.GetValue<string>());
        Assert.Equal(["one", "two"], result["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal("from stdin", result["stdin"]!.GetValue<string>());
    }

    [Fact]
    public void A_plugin_command_can_replace_a_built_in_one()
    {
        using Workspace w = new();
        Assert.Equal("sample plugin", Run(w, "list-bars", "")["handledBy"]!.GetValue<string>());
    }

    [Fact]
    public void A_plugin_format_reads_and_writes_on_disk_through_its_compression()
    {
        using Workspace w = new();
        string file = w.Path("thing.smpl");
        File.WriteAllBytes(file, Compressed([.. "SMPL"u8.ToArray(), .. Workspace.Text("hello")]));

        Assert.Equal("hello", Run(w, "read-disk", "", file)["content"]!.GetValue<string>());

        Run(w, "write-disk", "edited", file);

        Assert.Equal(Compressed([.. "SMPL"u8.ToArray(), .. Workspace.Text("edited")]), File.ReadAllBytes(file));
    }

    [Fact]
    public void A_plugin_format_is_told_by_its_contents_when_the_extension_doesnt_say()
    {
        using Workspace w = new();
        string file = w.Path("thing.unknown");
        File.WriteAllBytes(file, [.. "SMPL"u8.ToArray(), .. Workspace.Text("found")]);

        Assert.Equal("found", Run(w, "read-disk", "", file)["content"]!.GetValue<string>());
    }

    [Fact]
    public void An_archive_in_the_plugins_compression_opens_and_saves_the_same_way()
    {
        using Workspace w = new();
        string archive = w.Path("data.spack");
        byte[] entry = [.. "SMPL"u8.ToArray(), .. Workspace.Text("inside")];
        File.WriteAllBytes(archive, Compressed(Workspace.Sarc(("a/thing.smpl", entry)).Write()));

        Assert.Equal("inside", Run(w, "read", "", archive, "a/thing.smpl")["content"]!.GetValue<string>());

        Run(w, "write", "changed", archive, "a/thing.smpl");

        byte[] saved = File.ReadAllBytes(archive);
        Assert.True(saved.AsSpan().StartsWith("SMZ1"u8));
        Assert.Equal("changed", Run(w, "read", "", archive, "a/thing.smpl")["content"]!.GetValue<string>());
    }

    [Fact]
    public void The_plugin_gets_the_hosts_contract_even_when_it_ships_its_own_copy()
    {
        using Workspace w = new();
        string folder = w.Path("plugin");
        Directory.CreateDirectory(folder);
        foreach (string file in Directory.GetFiles(SampleFolder)) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));

        // Ship a copy of the contract and list it in .deps.json, as forgetting ExcludeAssets would.
        string copy = Path.Combine(folder, "Tkvsc.PluginApi.dll");
        File.Copy(typeof(IGamePlugin).Assembly.Location, copy);

        string depsPath = Path.Combine(folder, "SamplePlugin.deps.json");
        JsonObject deps = JsonNode.Parse(File.ReadAllText(depsPath))!.AsObject();
        deps["targets"]![".NETCoreApp,Version=v10.0"]!["Tkvsc.PluginApi/1.0.0"] = JsonNode.Parse("""{ "runtime": { "Tkvsc.PluginApi.dll": {} } }""");
        deps["libraries"]!["Tkvsc.PluginApi/1.0.0"] = JsonNode.Parse("""{ "type": "project", "serviceable": false, "sha512": "" }""");
        File.WriteAllText(depsPath, deps.ToJsonString());

        // The plugin's own resolver would pick the copy, whose types wouldn't match the host's IGamePlugin...
        string dll = Path.Combine(folder, "SamplePlugin.dll");
        string? resolved = new System.Runtime.Loader.AssemblyDependencyResolver(dll).ResolveAssemblyToPath(new("Tkvsc.PluginApi"));
        Assert.Equal(copy, resolved, ignoreCase: true);

        // ...but the host's copy wins, so the plugin still starts.
        Assert.NotNull(PluginLoader.Load(Env(dll)).FindCommand("sample-info"));
    }

    [Fact]
    public void A_missing_plugin_fails_rather_than_running_without_it()
    {
        PluginLoadException e = Assert.Throws<PluginLoadException>(() => PluginLoader.Load(Env("Z:/nowhere/Plugin.dll")));
        Assert.Contains("was not found", e.Message);
    }

    [Fact]
    public void A_file_that_isnt_an_assembly_is_reported()
    {
        using Workspace w = new();
        string file = w.Path("Bad.dll");
        File.WriteAllText(file, "not a dll");

        PluginLoadException e = Assert.Throws<PluginLoadException>(() => PluginLoader.Load(Env(file)));
        Assert.Contains("could not be loaded", e.Message);
    }

    [Fact]
    public void An_assembly_without_the_attribute_is_reported()
    {
        // The sample's helper is a real assembly that isn't a plugin.
        PluginLoadException e = Assert.Throws<PluginLoadException>(() => PluginLoader.Load(Env(Path.Combine(SampleFolder, "SamplePlugin.Helper.dll"))));
        Assert.Contains("is not a TKVSC plugin", e.Message);
    }

    [Fact]
    public void A_mismatch_always_says_what_went_wrong()
    {
        GamePlugin plugin = new(new Env(), "P.dll");
        Assert.Contains("Method not found", PluginLoader.DescribeMismatch(plugin, new MissingMethodException("Method not found")));

        string text = PluginLoader.DescribeMismatch(plugin, new MessagelessException());
        Assert.EndsWith(nameof(MessagelessException), text);
    }

    private sealed class MessagelessException : Exception
    {
        public override string Message => "";
    }

    [Fact]
    public void Contract_versions_outside_the_supported_range_are_refused()
    {
        Assert.Null(PluginLoader.CheckApiVersion(PluginApi.Version));
        Assert.Contains("Update TKVSC", PluginLoader.CheckApiVersion(PluginApi.Version + 1));
        Assert.Contains("Update the add-on", PluginLoader.CheckApiVersion(PluginApi.MinimumVersion - 1));
    }
}

public class ProfileDataTests
{
    private static Env Env(Workspace w, params (string Name, string Value)[] settings) => TkvscHost.Env.From(name =>
    {
        foreach (var (key, value) in settings) if (key == name) return value;
        return name == "TKVSC_COMPRESSION_BACKEND" ? "plain-zstd-yaz0" : name == "TKVSC_HANDLER_MANIFEST" ? w.Manifest() : null;
    });

    [Fact]
    public void Default_archive_names_are_unchanged()
    {
        Env env = new();
        foreach (string name in new[] { "a.pack", "a.pack.zs", "a.sarc", "a.genvb.zs", "a.blarc", "a.bfarc", "a.bkres", "a.bntx", "A.PACK" })
            Assert.True(env.IsArchiveName(name), name);
        Assert.False(env.IsArchiveName("a.byml"));
    }

    [Fact]
    public void A_profiles_patterns_decide_what_is_an_archive()
    {
        using Workspace w = new();
        Env env = Env(w, ("TKVSC_ARCHIVE_PATTERNS", """["\\.sbactorpack$", "(unclosed"]"""));

        Assert.True(env.IsArchiveName("Link.SBACTORPACK"));
        Assert.False(env.IsArchiveName("a.pack"));

        var split = Archives.SplitFsPath("C:/game/Link.sbactorpack/Actor/x.bgyml", env);
        Assert.Equal("Actor/x.bgyml", split?.Locator);
    }

    [Fact]
    public void Patterns_that_all_fail_to_compile_leave_the_default()
    {
        using Workspace w = new();
        Assert.True(Env(w, ("TKVSC_ARCHIVE_PATTERNS", """["(unclosed"]""")).IsArchiveName("a.pack"));
    }

    [Fact]
    public void A_yaz0_archive_on_disk_opens_and_stays_yaz0_when_saved()
    {
        using Workspace w = new();
        string archive = w.Path("Link.sbactorpack");
        Env env = Env(w, ("TKVSC_ARCHIVE_PATTERNS", """["\\.sbactorpack$"]"""));
        File.WriteAllBytes(archive, Yaz0Sharp.Yaz0.Compress(Workspace.Sarc(("a.txt", Workspace.Text("one"))).Write()));

        Archives archives = new(new Containers(env));
        Assert.Equal(["a.txt"], archives.ListArchiveFiles(archive, ""));

        archives.WriteArchiveFileBytes(archive, "a.txt", Workspace.Text("two"));

        byte[] saved = File.ReadAllBytes(archive);
        Assert.True(saved.AsSpan().StartsWith("Yaz0"u8));
        Assert.Equal(Workspace.Text("two"), archives.ReadArchiveFileBytes(archive, "a.txt"));
    }

    [Fact]
    public void The_dictionary_pack_location_comes_from_the_profile()
    {
        using Workspace w = new();
        Env env = TkvscHost.Env.From(name => name switch
        {
            "TKVSC_ROMFS" => w.Root,
            "TKVSC_ZSDIC_PATH" => "Custom/Dictionaries.pack.zs",
            _ => null,
        });

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => new Containers(env).Compress([1, 2, 3], "x.zs", true, false));
        Assert.Contains("Custom/Dictionaries.pack.zs", e.Message);
    }

    [Fact]
    public void Hash_names_only_apply_to_the_game_that_declares_them()
    {
        using Workspace w = new();

        // No game other than TotK falls back to TotK's list.
        Assert.Equal("", TkvscHost.Env.From(name => name == "TKVSC_GAME_ID" ? "splatoon3" : null).HashNamesPath);

        string names = w.Path("names.csv");
        File.WriteAllText(names, "deadbeef;bool;MyFlag\n");
        Env env = TkvscHost.Env.From(name => name switch { "TKVSC_GAME_ID" => "splatoon3", "TKVSC_HASH_NAMES" => names, _ => null });
        Assert.Equal(names, env.HashNamesPath);

        // The extension sends an empty value for a game without a list, TotK included.
        Assert.Equal("", TkvscHost.Env.From(name => name == "TKVSC_HASH_NAMES" ? "" : null).HashNamesPath);
    }

    [Fact]
    public void Xlink_versions_read_as_decimal_or_hex()
    {
        using Workspace w = new();
        Env env = Env(w, ("TKVSC_XLINK_ELINK_VERSION", "30"), ("TKVSC_XLINK_SLINK_VERSION", "0x1C"));
        Assert.Equal(30u, env.XlinkELinkVersion);
        Assert.Equal(28u, env.XlinkSLinkVersion);
        Assert.Null(new Env().XlinkELinkVersion);
    }
}

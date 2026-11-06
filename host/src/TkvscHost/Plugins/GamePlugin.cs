using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Tkvsc.Plugins;

namespace TkvscHost.Plugins;

/// <summary>A plugin that can't be used. The command fails with this rather than running without the game's code, which could write files wrongly.</summary>
public sealed class PluginLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What the active game's plugin registered. <see cref="None"/> when the game has no plugin.</summary>
public sealed class GamePlugin : IPluginBuilder
{
    public static readonly GamePlugin None = new(new Env());

    private readonly Dictionary<string, IFormatHandler> _formats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginCommand> _commands = new(StringComparer.Ordinal);
    private readonly List<IArchiveFormat> _archiveFormats = [];

    public GamePlugin(Env env, string path = "")
    {
        Path = path;
        Host = new PluginHostServices(env, this);
    }

    /// <summary>The plugin's DLL; empty for <see cref="None"/>.</summary>
    public string Path { get; }

    public IPluginHost Host { get; }

    public ICompressionBackend? Compression { get; private set; }

    public IFormatHandler? Format(string kind) => _formats.GetValueOrDefault(kind);

    public IEnumerable<KeyValuePair<string, IFormatHandler>> Formats => _formats;

    public void AddFormat(string kind, IFormatHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        _formats[kind] = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public void UseCompression(ICompressionBackend backend) => Compression = backend ?? throw new ArgumentNullException(nameof(backend));

    public IReadOnlyList<IArchiveFormat> ArchiveFormats => _archiveFormats;

    public void AddArchiveFormat(IArchiveFormat format) => _archiveFormats.Add(format ?? throw new ArgumentNullException(nameof(format)));

    public void AddCommand(string name, PluginCommand handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _commands[name] = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>The plugin's command of this name, as the host runs commands.</summary>
    public CommandHandler? FindCommand(string name)
    {
        if (!_commands.TryGetValue(name, out PluginCommand? command)) return null;
        return context => command(new CommandAdapter(context));
    }

    private sealed class CommandAdapter(CommandContext context) : IPluginCommand
    {
        public IReadOnlyList<string> Args => context.Args;
        public byte[] StdinBytes => context.StdinBytes;
        public string StdinText => context.StdinText;
    }
}

/// <summary>The host's side of <see cref="IPluginHost"/>.</summary>
internal sealed class PluginHostServices(Env env, GamePlugin plugin) : IPluginHost
{
    private Containers? _containers;

    private Containers Containers => _containers ??= new Containers(env, plugin);

    private Archives Archives => new(Containers);

    public string GameId => env.GameId;

    public string RomfsPath => env.Romfs;

    public string PluginDirectory => System.IO.Path.GetDirectoryName(plugin.Path) ?? "";

    public bool IsArchiveName(string name) => env.IsArchiveName(name);

    public byte[] Decompress(byte[] data, string logicalPath) => Containers.Decompress(data, logicalPath).Data;

    public byte[] CompressLike(byte[] original, byte[] data, string logicalPath)
    {
        bool yaz0 = original.AsSpan().StartsWith("Yaz0"u8);
        return Containers.Compress(data, logicalPath, !yaz0 && Containers.IsCompressed(original), yaz0);
    }

    public IReadOnlyList<string> ListArchive(string archivePath, string innerPath = "") => Archives.ListArchiveFiles(archivePath, innerPath);

    public byte[] ReadArchiveFile(string archivePath, string innerPath) => Archives.ReadArchiveFileBytes(archivePath, innerPath);

    public void WriteArchiveFile(string archivePath, string innerPath, byte[] data) => Archives.WriteArchiveFileBytes(archivePath, innerPath, data);
}

/// <summary>Finds and starts the active game's plugin.</summary>
public static class PluginLoader
{
    public static GamePlugin Load(Env env)
    {
        if (env.HostPluginPath.Length == 0) return GamePlugin.None;

        string path = Path.GetFullPath(env.HostPluginPath);
        if (!File.Exists(path)) throw new PluginLoadException($"The plugin for game '{env.GameId}' was not found at {path}.");

        Assembly assembly;
        try
        {
            assembly = new PluginLoadContext(path).LoadFromAssemblyPath(path);
        }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException or IOException)
        {
            throw new PluginLoadException($"The plugin {path} could not be loaded: {e.Message}", e);
        }

        TkvscPluginAttribute attribute = assembly.GetCustomAttribute<TkvscPluginAttribute>()
            ?? throw new PluginLoadException($"{path} is not a TKVSC plugin: it has no [assembly: TkvscPlugin] attribute.");

        if (CheckApiVersion(attribute.ApiVersion) is { } problem)
            throw new PluginLoadException($"The plugin {path} can't run: {problem}");

        GamePlugin plugin = new(env, path);
        try
        {
            if (Activator.CreateInstance(attribute.PluginType) is not IGamePlugin entry)
                throw new PluginLoadException($"{attribute.PluginType} in {path} does not implement {nameof(IGamePlugin)}.");

            entry.Configure(plugin);
        }
        catch (Exception e) when (e is not PluginLoadException)
        {
            throw new PluginLoadException($"The plugin {path} failed to start: {e.Message}", e);
        }

        return plugin;
    }

    /// <summary>What is wrong with a plugin built for this contract version; null when it can run.</summary>
    public static string? CheckApiVersion(int version)
    {
        if (version > PluginApi.Version)
            return $"it needs plugin API {version}, and this TKVSC has {PluginApi.Version}. Update TKVSC.";
        if (version < PluginApi.MinimumVersion)
            return $"it was built for plugin API {version}, and this TKVSC needs at least {PluginApi.MinimumVersion}. Update the add-on.";
        return null;
    }

    /// <summary>The plugin's own error when it was compiled against something this host no longer has (usually a format library).</summary>
    public static string DescribeMismatch(GamePlugin plugin, Exception e)
    {
        // A TypeLoadException can come with no message, only the name of the type.
        string detail = e.Message.Length > 0 ? e.Message : e is TypeLoadException { TypeName.Length: > 0 } load ? $"Missing: {load.TypeName}" : e.GetType().Name;
        return $"The plugin {plugin.Path} doesn't match this version of TKVSC; update the add-on or TKVSC. {detail}";
    }

    /// <summary>
    /// Anything the host has (the contract, the format libraries, the framework) comes from the host, so a plugin that ships
    /// its own copy still shares the host's types. Only what the host lacks is taken from the plugin's folder.
    /// </summary>
    private sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext("tkvsc-plugin")
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName name)
        {
            try
            {
                return Default.LoadFromAssemblyName(name);
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException)
            {
                string? path = _resolver.ResolveAssemblyToPath(name);
                return path is null ? null : LoadFromAssemblyPath(path);
            }
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}

using System.Text.Json.Nodes;

namespace Tkvsc.Plugins;

/// <summary>The version of this contract. A host loads plugins built for any version from <see cref="MinimumVersion"/> to <see cref="Version"/>.</summary>
public static class PluginApi
{
    /// <summary>The contract this build provides. Compiled into a plugin through <see cref="TkvscPluginAttribute"/>.</summary>
    public const int Version = 1;

    /// <summary>The oldest contract this build still runs. Raised only by a breaking change.</summary>
    public const int MinimumVersion = 1;
}

/// <summary>Marks an assembly as a game plugin and names its entry type: <c>[assembly: TkvscPlugin(typeof(MyPlugin))]</c>.</summary>
/// <param name="pluginType">A public class with a parameterless constructor that implements <see cref="IGamePlugin"/>.</param>
/// <param name="apiVersion">Leave as the default, which records the contract the plugin was compiled against.</param>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class TkvscPluginAttribute(Type pluginType, int apiVersion = PluginApi.Version) : Attribute
{
    /// <summary>The plugin's entry type.</summary>
    public Type PluginType { get; } = pluginType;

    /// <summary>The contract the plugin was compiled against.</summary>
    public int ApiVersion { get; } = apiVersion;
}

/// <summary>
/// A game's code. The host starts a new process for every command and loads only the active game's plugin,
/// so <see cref="Configure"/> runs each time: keep it fast and do expensive work when a handler is called.
/// </summary>
public interface IGamePlugin
{
    /// <summary>Registers what the plugin provides.</summary>
    void Configure(IPluginBuilder builder);
}

/// <summary>What a plugin registers with the host.</summary>
public interface IPluginBuilder
{
    /// <summary>The host's services, for handlers to keep and use later.</summary>
    IPluginHost Host { get; }

    /// <summary>
    /// Handles a file kind: the <c>handler</c> of a format the add-on registers with the extension. A kind the host
    /// has built in (<c>byml</c>, <c>msbt</c>, <c>aamp</c>, <c>xlnk</c>) is replaced for this game.
    /// </summary>
    void AddFormat(string kind, IFormatHandler handler);

    /// <summary>Replaces zstd for this game. Every file the host finds compressed goes through this backend; Yaz0 stays available.</summary>
    void UseCompression(ICompressionBackend backend);

    /// <summary>Adds an archive format, told from a file's decompressed contents. SARC stays available for anything no format matches.</summary>
    void AddArchiveFormat(IArchiveFormat format);

    /// <summary>Adds a host command, or replaces a built-in one for this game (for example <c>list-bars</c>).</summary>
    void AddCommand(string name, PluginCommand handler);
}

/// <summary>Converts a file kind to the text the editor shows and back. The host handles compression on both sides.</summary>
public interface IFormatHandler
{
    /// <summary>The text for the editor.</summary>
    /// <param name="data">The file, decompressed.</param>
    /// <param name="logicalPath">The file's path, which may be inside an archive.</param>
    string Read(byte[] data, string logicalPath);

    /// <summary>The file for the edited text, not compressed.</summary>
    /// <param name="original">The file being replaced, decompressed; empty for a new file.</param>
    /// <param name="text">The edited text.</param>
    /// <param name="logicalPath">The file's path, which may be inside an archive.</param>
    byte[] Write(byte[] original, string text, string logicalPath);

    /// <summary>Whether decompressed contents are this kind, for files whose extension doesn't say. Optional.</summary>
    bool Matches(ReadOnlySpan<byte> data) => false;
}

/// <summary>A game's compression. Calls can arrive from several threads at once while the host builds an index.</summary>
public interface ICompressionBackend
{
    /// <summary>Whether the data is compressed in this format, told from its contents.</summary>
    bool IsCompressed(ReadOnlySpan<byte> data);

    /// <summary>The decompressed data.</summary>
    byte[] Decompress(byte[] data, string logicalPath);

    /// <summary>The compressed data.</summary>
    byte[] Compress(byte[] data, string logicalPath);
}

/// <summary>A file in an archive. <paramref name="Name"/> is its path inside the archive, with forward slashes.</summary>
public sealed record ArchiveFile(string Name, byte[] Data);

/// <summary>
/// An archive format other than SARC. The host keeps the file list and handles compression, nesting and path lookups, so
/// listing, reading, writing, deleting and renaming all work once a format can read and write its files.
/// </summary>
public interface IArchiveFormat
{
    /// <summary>Whether decompressed contents are this format.</summary>
    bool Matches(ReadOnlySpan<byte> data);

    /// <summary>The files in the archive.</summary>
    /// <param name="data">The archive, decompressed.</param>
    /// <param name="logicalPath">The archive's path, which may be inside another archive.</param>
    IReadOnlyList<ArchiveFile> Read(byte[] data, string logicalPath);

    /// <summary>The archive holding <paramref name="files"/>, not compressed. For a read-only format, throw an exception saying so.</summary>
    /// <param name="files">The files, in order; ones added since reading come last.</param>
    /// <param name="original">The archive as it was read, for keeping its header or alignment.</param>
    /// <param name="logicalPath">The archive's path, which may be inside another archive.</param>
    byte[] Write(IReadOnlyList<ArchiveFile> files, byte[] original, string logicalPath);
}

/// <summary>A command the extension or an add-on can run. It returns the JSON written back; a thrown exception becomes an error result.</summary>
public delegate JsonNode PluginCommand(IPluginCommand command);

/// <summary>One run of a plugin's command.</summary>
public interface IPluginCommand
{
    /// <summary>The arguments after the command name.</summary>
    IReadOnlyList<string> Args { get; }

    /// <summary>Standard input as bytes, read on first use.</summary>
    byte[] StdinBytes { get; }

    /// <summary>Standard input as UTF-8 text, read on first use.</summary>
    string StdinText { get; }
}

/// <summary>What the host offers a plugin.</summary>
public interface IPluginHost
{
    /// <summary>The active game's id.</summary>
    string GameId { get; }

    /// <summary>The game's RomFS folder; empty when it isn't set.</summary>
    string RomfsPath { get; }

    /// <summary>The folder holding the plugin, for data files shipped beside it.</summary>
    string PluginDirectory { get; }

    /// <summary>Whether a path segment names an archive for this game.</summary>
    bool IsArchiveName(string name);

    /// <summary>Removes the game's compression or Yaz0, if the data has either.</summary>
    byte[] Decompress(byte[] data, string logicalPath);

    /// <summary>Compresses <paramref name="data"/> the way <paramref name="original"/> is compressed.</summary>
    byte[] CompressLike(byte[] original, byte[] data, string logicalPath);

    /// <summary>The paths in an archive on disk, optionally under a path inside it (which can lead into nested archives).</summary>
    IReadOnlyList<string> ListArchive(string archivePath, string innerPath = "");

    /// <summary>A file in an archive on disk.</summary>
    byte[] ReadArchiveFile(string archivePath, string innerPath);

    /// <summary>Writes a file into an archive on disk.</summary>
    void WriteArchiveFile(string archivePath, string innerPath, byte[] data);
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TkvscHost.Formats;

namespace TkvscHost;

/// <summary>The bridge commands the host runs itself. Everything else goes to the Python bridge.</summary>
public static partial class Cmd
{
    private const int LargeContentBytes = 8 * 1024 * 1024;

    public static void Register(Dictionary<string, CommandHandler> commands)
    {
        commands["read-disk"] = ReadDisk;
        commands["write-disk"] = WriteDisk;
        commands["list"] = List;
        commands["read"] = Read;
        commands["write"] = Write;
        commands["write-raw"] = WriteRaw;
        commands["delete-entry"] = DeleteEntry;
        commands["rename-entry"] = RenameEntry;
        commands["export-temp"] = ExportTemp;
        commands["export-stored"] = ExportStored;
        commands["decompress-file"] = DecompressFile;
        commands["compress-file"] = CompressFile;
        commands["build-romfs-index"] = c => IndexBuilders.BuildRomfsIndex(c);
        commands["build-canonical-path-index"] = c => IndexBuilders.BuildCanonicalPathIndex(c);
        TextureCommands.Register(commands);
        FontCommands.Register(commands);
        AudioCommands.Register(commands);
        HexpatCommands.Register(commands);
    }

    private static readonly JsonNode Success = new JsonObject { ["success"] = true };

    private static JsonNode Ok() => new JsonObject { ["success"] = true };

    private static string RandomSuffix()
    {
        Span<byte> bytes = stackalloc byte[5];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes)[..8];
    }

    private static string SafeName(string name)
    {
        if (name.Length == 0) name = "file.bin";
        return string.Concat(name.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_'));
    }

    private static string WriteTemp(string prefix, string suffix, byte[] data)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{prefix}{RandomSuffix()}{suffix}");
        File.WriteAllBytes(path, data);
        return path;
    }

    // A big text goes in a file the extension reads, so it does not travel through the pipe.
    private static JsonNode ReadPayload(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > LargeContentBytes)
        {
            string path = Path.Combine(Path.GetTempPath(), $"tmp{RandomSuffix()}.yaml");
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return new JsonObject { ["contentPath"] = path };
        }

        return new JsonObject { ["content"] = content };
    }

    private static (Env, Containers, Archives, FileKinds, FormatHandlers) Services(CommandContext c)
    {
        FileKinds kinds = new(c.Env, c.Containers);
        return (c.Env, c.Containers, new Archives(c.Containers), kinds, new FormatHandlers(c.Env, c.Containers, kinds));
    }

    // ---- files on disk ----

    private static JsonNode ReadDisk(CommandContext c)
    {
        string path = c.Arg(0);
        var (_, _, _, _, handlers) = Services(c);
        return ReadPayload(handlers.ReadContent(File.ReadAllBytes(path), path));
    }

    private static JsonNode WriteDisk(CommandContext c)
    {
        string path = c.Arg(0);
        string text = c.StdinText;
        var (_, _, _, kinds, handlers) = Services(c);

        string? kind = kinds.KindOf(path) ?? kinds.KindOf(path, File.ReadAllBytes(path));
        if (kind is null) throw new InvalidOperationException($"Cannot write file type: {path}");

        byte[] written = handlers.WriteContent(kind, File.ReadAllBytes(path), text, path);
        File.WriteAllBytes(path, written);
        return Ok();
    }

    // ---- archives ----

    private static JsonNode List(CommandContext c)
    {
        var (_, _, archives, _, _) = Services(c);
        List<string> names = archives.ListArchiveFiles(c.Arg(0), c.ArgOr(1, ""));
        return new JsonArray([.. names.Select(n => (JsonNode)n)]);
    }

    private static JsonNode Read(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.Arg(1);
        var (_, _, archives, _, handlers) = Services(c);

        // A texture inside a BNTX gets the viewer's metadata and a picture instead of text.
        if (archives.ResolveBntx(archive, internalPath) is { Remainder.Length: > 0 } bntx)
            return Textures.BntxTextureResult(bntx.Data, bntx.Remainder, TextureCommands.LayerArg(c, 2));

        archives.LoadSarcFile(archive);
        byte[] data = archives.ReadArchiveFileBytes(archive, internalPath);
        return ReadPayload(handlers.ReadContent(data, internalPath));
    }

    private static JsonNode Write(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.Arg(1);
        string text = c.StdinText;
        var (env, _, archives, kinds, handlers) = Services(c);

        archives.LoadSarcFile(archive);
        string? kind = kinds.KindOf(internalPath);
        if (kind is null)
        {
            try { kind = kinds.KindOf(internalPath, archives.ReadArchiveFileBytes(archive, internalPath)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        if (kind is null) throw new InvalidOperationException($"Cannot write file type: {internalPath}");

        byte[] original = OriginalBytes(archives, env, archive, internalPath);
        archives.WriteArchiveFileBytes(archive, internalPath, handlers.WriteContent(kind, original, text, internalPath));
        return Ok();
    }

    [GeneratedRegex(@"_\d+(\..+)?$")]
    private static partial Regex NumberSuffix();

    // The entry as stored, or for a new entry named like a variant (Name_01.ext) the game's own Name.ext.
    private static byte[] OriginalBytes(Archives archives, Env env, string archive, string internalPath)
    {
        try
        {
            return archives.ReadArchiveFileBytes(archive, internalPath);
        }
        catch (Exception e) when (e is IOException or FileNotFoundException)
        {
            string name = Path.GetFileName(internalPath);
            string parent = internalPath.Replace('\\', '/').Contains('/') ? internalPath[..internalPath.Replace('\\', '/').LastIndexOf('/')] : "";
            string cleaned = NumberSuffix().Replace(name, "$1");
            string fallback = Path.Combine(env.Romfs, parent, cleaned);
            return env.Romfs.Length > 0 && File.Exists(fallback) ? File.ReadAllBytes(fallback) : [];
        }
    }

    private static JsonNode WriteRaw(CommandContext c)
    {
        string encoded = c.StdinText.Trim();
        byte[] raw = encoded.Length == 0 ? [] : Convert.FromBase64String(encoded);
        new Archives(c.Containers).WriteArchiveFileBytes(c.Arg(0), c.Arg(1), raw);
        return Ok();
    }

    private static JsonNode DeleteEntry(CommandContext c)
    {
        new Archives(c.Containers).DeleteArchiveEntry(c.Arg(0), c.Arg(1));
        return Ok();
    }

    private static JsonNode RenameEntry(CommandContext c)
    {
        new Archives(c.Containers).RenameArchiveEntry(c.Arg(0), c.Arg(1), c.Arg(2));
        return Ok();
    }

    // ---- files for other tools ----

    private static JsonNode ExportTemp(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.Arg(1);
        byte[] data;
        string name;
        if (internalPath.Length == 0)
        {
            data = File.ReadAllBytes(archive);
            name = Path.GetFileName(archive);
        }
        else
        {
            data = new Archives(c.Containers).ReadArchiveFileBytes(archive, internalPath);
            name = Path.GetFileName(internalPath.Replace('\\', '/'));
        }

        return new JsonObject { ["path"] = WriteTemp("totk-tool-", "-" + SafeName(name), data) };
    }

    private static JsonNode ExportStored(CommandContext c)
    {
        byte[] data = new Archives(c.Containers).ReadArchiveEntryStoredBytes(c.Arg(0), c.Arg(1));
        return new JsonObject { ["path"] = WriteTemp("totk-tool-", "-stored.bin", data) };
    }

    private static JsonNode DecompressFile(CommandContext c)
    {
        string input = c.Arg(0);
        string logical = c.ArgOr(1, input);
        byte[] data = c.Containers.Decompress(File.ReadAllBytes(input), logical).Data;
        string name = Path.GetFileName(logical.Replace('\\', '/')).Replace(".zs", "");
        return new JsonObject { ["path"] = WriteTemp("totk-decomp-", "-" + name, data) };
    }

    private static JsonNode CompressFile(CommandContext c)
    {
        string input = c.Arg(0);
        string logical = c.ArgOr(1, input);
        byte[] data = c.Containers.Compress(File.ReadAllBytes(input), logical, wasZstd: true, wasYaz0: false);
        return new JsonObject { ["path"] = WriteTemp("totk-comp-", "-" + Path.GetFileName(logical.Replace('\\', '/')), data) };
    }
}

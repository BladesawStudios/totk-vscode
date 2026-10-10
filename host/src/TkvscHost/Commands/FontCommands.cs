using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BFontSharp;

namespace TkvscHost;

/// <summary>Reading and replacing the game's fonts (BFTTF/BFOTF), through BFontSharp.</summary>
public static class FontCommands
{
    public static void Register(Dictionary<string, CommandHandler> commands)
    {
        commands["read-font-disk"] = ReadFontDisk;
        commands["prepare-font-replacement"] = PrepareFontReplacement;
    }

    private static string SafeName(string name)
    {
        if (name.Length == 0) name = "font.bin";
        return string.Concat(name.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_'));
    }

    private static JsonNode TempResult(string prefix, string name, byte[] data)
    {
        Span<byte> random = stackalloc byte[5];
        RandomNumberGenerator.Fill(random);
        string path = Path.Combine(Path.GetTempPath(), $"{prefix}{Convert.ToHexString(random).ToLowerInvariant()}-{SafeName(name)}");
        File.WriteAllBytes(path, data);
        return new JsonObject { ["path"] = path };
    }

    // A font file on disk as a font program: decompressed if it is .zs, decrypted if it is a BFTTF/BFOTF.
    private static JsonNode ReadFontDisk(CommandContext c)
    {
        string filePath = c.Arg(0);
        byte[] data = File.ReadAllBytes(filePath);
        string logical = filePath.Replace('\\', '/');
        if (logical.EndsWith(".zs", StringComparison.OrdinalIgnoreCase))
        {
            data = c.Containers.Decompress(data, logical).Data;
            logical = logical[..^3];
        }

        if (BFont.IsEncryptedFontPath(logical)) data = BFont.ToOpenFont(data);
        return TempResult("totk-font-", Path.GetFileName(logical), data);
    }

    // What to write over a font: the imported file, encrypted the way the file it replaces is (or the way the game
    // makes that name) when the target is a BFTTF/BFOTF.
    private static JsonNode PrepareFontReplacement(CommandContext c)
    {
        string importPath = c.Arg(0), targetPath = c.Arg(1);
        byte[] imported = File.ReadAllBytes(importPath);

        // The target as stored, still encrypted, so the key it uses can be told. It may not exist yet.
        byte[]? existing = null;
        try
        {
            existing = Archives.SplitFsPath(targetPath) is var (archive, locator)
                ? new Archives(c.Containers).ReadArchiveEntryStoredBytes(archive, locator)
                : File.ReadAllBytes(targetPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or KeyNotFoundException)
        {
        }

        byte[] output;
        try
        {
            output = BFont.PrepareReplacement(imported, targetPath, existing);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException($"Not a valid font file: {importPath}");
        }

        return TempResult("totk-font-out-", Path.GetFileName(targetPath.Replace('\\', '/')), output);
    }
}

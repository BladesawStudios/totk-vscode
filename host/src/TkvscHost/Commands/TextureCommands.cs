using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TkvscHost.Formats;

namespace TkvscHost;

/// <summary>The commands for BNTX and TXTG textures: what the texture viewer shows, and what it saves.</summary>
public static class TextureCommands
{
    public static void Register(Dictionary<string, CommandHandler> commands)
    {
        commands["render-bntx-texture"] = RenderBntx;
        commands["render-txtg"] = RenderTxtg;
        commands["update-bntx-metadata"] = UpdateBntxMetadata;
        commands["update-metadata"] = UpdateBntxMetadata;
        commands["update-txtg-metadata"] = UpdateTxtgMetadata;
        commands["replace-bntx-payload"] = ReplaceBntxPayload;
        commands["replace-txtg-payload"] = ReplaceTxtgPayload;
        commands["export-converted"] = ExportConverted;
        commands["rename-bntx-texture"] = _ => throw new InvalidOperationException("Texture renaming is temporarily disabled!");
        commands["delete-bntx-texture"] = _ => throw new InvalidOperationException("Deleting a texture from a BNTX is not supported yet.");
    }

    /// <summary>The layer of an array texture a command is about: an optional number after the usual arguments.</summary>
    public static int LayerArg(CommandContext c, int index)
        => c.Args.Length > index && int.TryParse(c.Args[index], out int layer) ? layer : 0;

    private static JsonObject Success(JsonObject? extra = null)
    {
        JsonObject result = new() { ["success"] = true };
        if (extra is not null)
            foreach (var (key, value) in extra.ToArray())
            {
                extra.Remove(key);
                result[key] = value;
            }

        return result;
    }

    private static JsonObject MetadataFrom(CommandContext c, int argIndex)
    {
        string text = c.Args.Length > argIndex ? c.Arg(argIndex) : c.StdinText;
        return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("The metadata is not a JSON object.");
    }

    // The texture file an archive path points at: from inside the archive, or the file itself when the path is empty.
    private static (byte[] Data, string Logical) Source(CommandContext c, Archives archives, string archive, string internalPath)
        => internalPath.Length > 0
            ? (archives.ReadArchiveFileBytes(archive, internalPath), internalPath)
            : (File.ReadAllBytes(archive), archive);

    private static (byte[] Payload, bool IsZstd) Unwrap(CommandContext c, byte[] data, string logical)
    {
        try
        {
            Unwrapped unwrapped = c.Containers.Decompress(data, logical);
            return (unwrapped.Data, unwrapped.WasZstd);
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or FileNotFoundException)
        {
            return (data, false);
        }
    }

    // Writes an edited file back where it came from, compressed again if it was.
    private static void Save(CommandContext c, Archives archives, string archive, string logical, byte[] bytes, bool wasZstd)
    {
        if (wasZstd) bytes = c.Containers.Compress(bytes, logical.Length > 0 ? logical : archive, wasZstd: true, wasYaz0: false);

        if (logical != archive) archives.WriteArchiveFileBytes(archive, logical, bytes);
        else File.WriteAllBytes(archive, bytes);
    }

    // ---- BNTX ----

    private static Archives.BntxView RequireBntx(Archives archives, string archive, string internalPath)
        => archives.ResolveBntx(archive, internalPath) is { Remainder.Length: > 0 } view
            ? view
            : throw new InvalidOperationException("Could not resolve BNTX data");

    private static JsonNode RenderBntx(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.Arg(1);
        Archives archives = new(c.Containers);

        if (archives.ResolveBntx(archive, internalPath) is not { Remainder.Length: > 0 } view)
            return new JsonObject { ["error"] = "Not a BNTX texture path" };

        return Textures.RenderBntxPng(view.Data, view.Remainder, LayerArg(c, 2)) is { } png
            ? new JsonObject { ["path"] = png }
            : new JsonObject { ["error"] = $"Failed to render texture: {view.Remainder}" };
    }

    private static void SaveBntx(CommandContext c, Archives archives, string archive, Archives.BntxView view, byte[] bntx)
    {
        bool zstd = view.Prefix.Length > 0
            ? view.Prefix.EndsWith(".zs", StringComparison.Ordinal)
            : archive.EndsWith(".zs", StringComparison.Ordinal);
        if (zstd) bntx = c.Containers.Compress(bntx, view.Prefix.Length > 0 ? view.Prefix : archive, wasZstd: true, wasYaz0: false);

        if (view.Prefix.Length > 0) archives.WriteArchiveFileBytes(archive, view.Prefix, bntx);
        else File.WriteAllBytes(archive, bntx);
    }

    private static JsonNode UpdateBntxMetadata(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.Arg(1);
        JsonObject metadata = MetadataFrom(c, 2);
        Archives archives = new(c.Containers);
        Archives.BntxView view = RequireBntx(archives, archive, internalPath);

        SaveBntx(c, archives, archive, view, Textures.UpdateBntxMetadata(view.Data, view.Remainder, metadata));
        return Success();
    }

    private static JsonNode ReplaceBntxPayload(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.Arg(1);
        string encoded = c.StdinText.Trim();
        byte[] dds = encoded.Length == 0 ? [] : Convert.FromBase64String(encoded);

        Archives archives = new(c.Containers);
        Archives.BntxView view = RequireBntx(archives, archive, internalPath);
        var (bntx, info) = Textures.ReplaceBntxPayload(view.Data, view.Remainder, dds, LayerArg(c, 2));
        SaveBntx(c, archives, archive, view, bntx);
        return Success(info);
    }

    // ---- TXTG ----

    private static JsonNode RenderTxtg(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.ArgOr(1, "");
        Archives archives = new(c.Containers);
        var (data, logical) = Source(c, archives, archive, internalPath);
        string name = Path.GetFileName(logical.Replace('\\', '/'));
        if (name.Length == 0) name = "texture";

        return Textures.TxtgTextureResult(Unwrap(c, data, logical).Payload, name, LayerArg(c, 2));
    }

    private static JsonNode UpdateTxtgMetadata(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.ArgOr(1, "");
        JsonObject metadata = MetadataFrom(c, 2);
        Archives archives = new(c.Containers);
        var (data, logical) = Source(c, archives, archive, internalPath);
        var (payload, zstd) = Unwrap(c, data, logical);

        Save(c, archives, archive, logical, Textures.UpdateTxtgMetadata(payload, metadata), zstd);
        return Success();
    }

    private static JsonNode ReplaceTxtgPayload(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.ArgOr(1, "");
        string encoded = c.StdinText.Trim();
        byte[] dds = encoded.Length == 0 ? [] : Convert.FromBase64String(encoded);

        Archives archives = new(c.Containers);
        var (data, logical) = Source(c, archives, archive, internalPath);
        var (payload, zstd) = Unwrap(c, data, logical);

        var (txtg, info) = Textures.ReplaceTxtgPayload(payload, dds, LayerArg(c, 2));
        Save(c, archives, archive, logical, txtg, zstd);
        return Success(info);
    }

    // ---- converting for other programs ----

    private static readonly HashSet<string> ImageTargets = [".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds"];

    private static string Temp(string suffix, byte[] data) => Textures.TempFile("totk-cvt-", suffix, data);

    private static JsonNode ExportConverted(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.Arg(1), target = c.Arg(2).ToLowerInvariant();
        int layer = LayerArg(c, 3);
        Archives archives = new(c.Containers);
        var (data, logical) = Source(c, archives, archive, internalPath);

        FileKinds kinds = new(c.Env, c.Containers);
        FormatHandlers handlers = new(c.Env, c.Containers, kinds);
        string? kind = kinds.KindOf(logical, data);

        if (ImageTargets.Contains(target))
        {
            bool isTxtg = logical.EndsWith(".txtg", StringComparison.OrdinalIgnoreCase) || Textures.IsTxtg(data);
            byte[]? dds = null;
            (byte[] Rgba, int Width, int Height)? pixels = null;

            if (isTxtg)
            {
                byte[] payload = Unwrap(c, data, logical).Payload;
                pixels = Textures.TxtgPixels(payload, layer);
                if (target == ".dds") dds = Textures.TxtgDds(payload, layer);
            }
            else if (archives.ResolveBntx(archive, internalPath) is { Remainder.Length: > 0 } view)
            {
                pixels = Textures.BntxPixels(view.Data, view.Remainder, layer);
                if (target == ".dds") dds = Textures.BntxDds(view.Data, view.Remainder, layer);
            }

            if (pixels is { } image)
            {
                byte[] encoded = target switch
                {
                    ".png" => Textures.Png(image.Rgba, image.Width, image.Height),
                    ".bmp" => Textures.Bmp(image.Rgba, image.Width, image.Height),
                    ".tga" => Textures.Tga(image.Rgba, image.Width, image.Height),
                    ".jpg" or ".jpeg" => Textures.Jpeg(image.Rgba, image.Width, image.Height),
                    _ => dds ?? new TexSharp.DdsImage(image.Width, image.Height, TexSharp.TextureFormat.Rgba8, [image.Rgba]).ToBytes(),
                };
                return new JsonObject { ["path"] = Temp(target, encoded) };
            }
        }
        else if (target is ".yaml" or ".yml" && kind is "byml" or "bgyml" or "aamp")
        {
            string text = handlers.ReadContent(data, logical);
            return new JsonObject { ["path"] = Temp(target, new UTF8Encoding(false).GetBytes(text)) };
        }
        else if (target is ".json" or ".txt" && kind == "msbt")
        {
            string text = handlers.ReadContent(data, logical);
            if (target == ".json")
                text = JsonSerializer.Serialize(MsbtMessages(text), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            return new JsonObject { ["path"] = Temp(target, new UTF8Encoding(false).GetBytes(text)) };
        }

        // Anything else is handed over as it is.
        string name = Path.GetFileName(logical.Replace('\\', '/'));
        return new JsonObject
        {
            ["path"] = Textures.TempFile("totk-tool-", "-" + string.Concat((name.Length == 0 ? "file.bin" : name).Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_')), data),
        };
    }

    // "Label: text" lines to a label-to-text map.
    private static SortedDictionary<string, string> MsbtMessages(string editorText)
    {
        SortedDictionary<string, string> messages = new(StringComparer.Ordinal);
        foreach (string line in editorText.Split('\n'))
        {
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (line.Length == 0 || line[0] == '#' || colon < 0) continue;
            messages[line[..colon]] = line[(colon + 2)..];
        }

        return messages;
    }
}

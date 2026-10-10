using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using BntxSharp;
using TexSharp;
using TxtgSharp;
using ZstdSharp;

namespace TkvscHost.Formats;

/// <summary>
/// BNTX and TXTG textures for the viewer: the metadata it shows, a PNG to look at, DDS files to edit in an image
/// editor, and the edits it saves. Port of <c>bntx_renderer.py</c>, <c>txtg_reader.py</c> and the editors, on top
/// of BntxSharp, TxtgSharp and TexSharp.
/// </summary>
public static class Textures
{
    // ---- BNTX metadata ----

    private static readonly Dictionary<uint, string> FormatNames = new()
    {
        [0x01] = "R4G4_UNORM", [0x02] = "R8_UNORM", [0x03] = "R4G4B4A4", [0x04] = "A4B4G4R4", [0x05] = "R5G5B5A1",
        [0x06] = "A1B5G5R5", [0x07] = "R5G6B5_UNORM", [0x08] = "B5G6R5_UNORM", [0x09] = "R8G8_UNORM", [0x0B] = "R8G8B8A8",
        [0x0C] = "B8G8R8A8", [0x0E] = "R10G10B10A2", [0x1A] = "BC1", [0x1B] = "BC2", [0x1C] = "BC3", [0x1D] = "BC4",
        [0x1E] = "BC5", [0x1F] = "BC6H", [0x20] = "BC7", [0x2D] = "ASTC_4x4", [0x2E] = "ASTC_5x4", [0x2F] = "ASTC_5x5",
        [0x30] = "ASTC_6x5", [0x31] = "ASTC_6x6", [0x32] = "ASTC_8x5", [0x33] = "ASTC_8x6", [0x34] = "ASTC_8x8",
        [0x35] = "ASTC_10x5", [0x36] = "ASTC_10x6", [0x37] = "ASTC_10x8", [0x38] = "ASTC_10x10", [0x39] = "ASTC_12x10",
        [0x3A] = "ASTC_12x12", [0x3B] = "B5G5R5A1",
    };

    public static string BntxFormatName(uint formatId)
    {
        if (!FormatNames.TryGetValue(formatId >> 8, out string? name)) return $"Unknown(0x{formatId:X4})";
        return (formatId & 0xFF) switch
        {
            0x06 => name + "_SRGB",
            0x02 => name + "_SNORM",
            0x01 => name + "_UNORM",
            _ => name,
        };
    }

    private static string ChannelName(int value) => value switch
    {
        0 => "Zero", 1 => "One", 2 => "Red", 3 => "Green", 4 => "Blue", 5 => "Alpha",
        _ => $"Unknown({value})",
    };

    private static string DimName(int value) => value switch
    {
        1 => "Dim1D", 2 => "Dim2D", 3 => "Dim3D", 6 => "DimCube",
        _ => $"Unknown({value})",
    };

    public static JsonObject BntxMetadata(BntxNames.Info t)
    {
        string format = BntxFormatName(t.FormatId);
        string formatId = $"0x{t.FormatId:X4}";
        string tile = t.TileMode == 1 ? "Linear" : "Default";
        string dim = DimName(t.Dims);
        bool srgb = (t.FormatId & 0xFF) == 0x06;

        return new JsonObject
        {
            ["name"] = t.Name,
            ["channels"] = new JsonObject
            {
                ["red"] = ChannelName(t.ChannelR),
                ["green"] = ChannelName(t.ChannelG),
                ["blue"] = ChannelName(t.ChannelB),
                ["alpha"] = ChannelName(t.ChannelA),
            },
            ["imageInfo"] = new JsonObject
            {
                ["width"] = t.Width,
                ["height"] = t.Height,
                ["mipCount"] = t.MipCount,
                ["format"] = format,
                ["formatId"] = formatId,
                ["useSRGB"] = srgb ? "True" : "False",
                ["name"] = t.Name,
                ["path"] = t.Path,
                ["accessFlags"] = t.AccessFlags == 0x20 ? "Texture" : $"0x{t.AccessFlags:X2}",
            },
            ["misc"] = new JsonObject
            {
                ["depth"] = t.Depth,
                ["tileMode"] = tile,
                ["swizzle"] = t.Swizzle,
                ["alignment"] = t.Alignment,
                ["pitch"] = t.Pitch,
                ["dims"] = dim,
                ["surfaceShape"] = dim,
                ["flags"] = t.Flags,
                ["imageSize"] = t.ImageSize,
                ["sampleCount"] = t.SampleCount,
            },
            ["width"] = t.Width,
            ["height"] = t.Height,
            ["format"] = format,
            ["formatId"] = formatId,
            ["mipCount"] = t.MipCount,
            ["arrayCount"] = t.ArrayCount,
            ["dataSize"] = t.DataSize,
            ["tileMode"] = tile,
            ["blockH"] = 1 << t.BlockHeightLog2,
            ["blockHLog2"] = t.BlockHeightLog2,
        };
    }

    // ---- files made along the way ----

    private static string SafeName(string name)
        => string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_'));

    public static string TempFile(string prefix, string suffix, byte[] data)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{prefix}{Guid.NewGuid().ToString("N")[..8]}{suffix}");
        File.WriteAllBytes(path, data);
        return path;
    }

    // ---- BNTX ----

    private static BntxTexture? Find(BntxFile file, string name) => file.Textures.FirstOrDefault(t => t.Name == name);

    /// <summary>A PNG of the named texture in a temp file, or null if there is no such texture or it cannot be shown.</summary>
    public static string? RenderBntxPng(byte[] bntx, string name, int layer = 0)
    {
        BntxNames.Info? info = BntxNames.ParseInfo(bntx).FirstOrDefault(t => t.Name == name);
        if (info is null || info.DataOffset <= 0 || info.DataSize <= 0) return null;

        BntxFile file;
        try { file = BntxFile.Load(bntx); }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        {
            throw new InvalidDataException($"The BNTX could not be read: {e.Message}", e);
        }

        BntxTexture? texture = Find(file, name);
        if (texture is null || !texture.TryGetTextureFormat(out _, out _, out _)) return null;
        CheckLayer(layer, texture.ArrayLength);

        return TempFile("totk-tex-", $"-{SafeName(name)}.png", texture.ToPng(0, layer));
    }

    /// <summary>What <c>read</c> gives for a texture inside a BNTX: its metadata and a PNG of it.</summary>
    public static JsonObject BntxTextureResult(byte[] bntx, string name, int layer = 0)
    {
        JsonObject result = new() { ["bntxTexture"] = true, ["layer"] = layer };
        if (BntxNames.ParseInfo(bntx).FirstOrDefault(t => t.Name == name) is { } info) result["metadata"] = BntxMetadata(info);
        if (RenderBntxPng(bntx, name, layer) is { } png) result["pngPath"] = png;
        return result;
    }

    private static void CheckLayer(int layer, int layers)
    {
        if (layer < 0 || layer >= Math.Max(1, layers))
            throw new ArgumentOutOfRangeException(nameof(layer), $"Layer {layer} does not exist; the texture has {Math.Max(1, layers)}.");
    }

    private static readonly string[] ChannelOrder = ["Zero", "One", "Red", "Green", "Blue", "Alpha"];

    /// <summary>Applies the viewer's edits to one texture and returns the file as it should be saved.</summary>
    public static byte[] UpdateBntxMetadata(byte[] bntx, string name, JsonObject metadata)
    {
        BntxFile file = BntxFile.Load(bntx);
        BntxTexture texture = Find(file, name) ?? throw new InvalidOperationException($"Texture {name} not found.");
        BntxNames.Info info = BntxNames.ParseInfo(bntx).First(t => t.Name == name);

        string[] keys = ["red", "green", "blue", "alpha"];
        for (int i = 0; i < keys.Length; i++)
            if (metadata[keys[i]] is JsonValue v && v.TryGetValue(out string? channel) && Array.IndexOf(ChannelOrder, channel) is var code and >= 0)
                texture.ChannelTypes[i] = (ChannelType)code;

        if (metadata["swizzle"] is JsonValue swizzle && swizzle.TryGetValue(out int swizzleValue)) texture.Swizzle = (ushort)swizzleValue;

        if (metadata["name"] is JsonValue n && n.TryGetValue(out string? newName) && newName is not null && newName != name)
            throw new InvalidOperationException("Texture renaming is temporarily disabled!");

        if (metadata["path"] is JsonValue p && p.TryGetValue(out string? newPath) && newPath is not null && newPath != info.Path)
            throw new InvalidOperationException("Changing a texture's path is not supported yet.");

        if (metadata["useSRGB"] is JsonValue s && s.TryGetValue(out bool useSrgb))
        {
            SurfaceFormatVariant variant = SurfaceFormatInfo.VariantOf(texture.Format);
            if (useSrgb && variant != SurfaceFormatVariant.Srgb) texture.Format = SurfaceFormatInfo.WithVariant(texture.Format, SurfaceFormatVariant.Srgb);
            else if (!useSrgb && variant == SurfaceFormatVariant.Srgb) texture.Format = SurfaceFormatInfo.WithVariant(texture.Format, SurfaceFormatVariant.UNorm);
        }

        return file.Save();
    }

    /// <summary>Puts the pixels of a DDS in the named texture and returns the file as it should be saved.</summary>
    public static (byte[] Bntx, JsonObject Info) ReplaceBntxPayload(byte[] bntx, string name, byte[] dds, int layer = 0)
    {
        BntxFile file = BntxFile.Load(bntx);
        BntxTexture texture = Find(file, name) ?? throw new InvalidOperationException($"Texture not found in BNTX: '{name}'");
        DdsImage image = DdsImage.Parse(dds);

        // An array is changed a layer at a time, converting the DDS to the size, format and mips of the texture if it
        // differs; a single texture is replaced whole.
        IReadOnlyList<string> changes = [];
        if (texture.ArrayLength > 1) changes = texture.ReplaceLayerFromDds(image, layer);
        else
        {
            CheckLayer(layer, 1);
            texture.ReplaceFromDds(image);
        }

        JsonObject info = new()
        {
            ["width"] = texture.Width,
            ["height"] = texture.Height,
            ["mipCount"] = texture.MipCount,
            ["format"] = BntxFormatName((uint)texture.Format),
        };
        NoteConversion(info, changes, layer);
        return (file.Save(), info);
    }

    /// <summary>The first mip of a texture as a DDS, which is what the viewer's "export" gives.</summary>
    public static byte[]? BntxDds(byte[] bntx, string name, int layer = 0)
    {
        BntxFile file = BntxFile.Load(bntx);
        BntxTexture? texture = Find(file, name);
        if (texture is null || !texture.TryGetTextureFormat(out _, out _, out _)) return null;
        CheckLayer(layer, texture.ArrayLength);

        DdsImage all = texture.ToDds(layer, editable: true);
        // A single texture is exported as its first mip (as the Python bridge did) and resized to fit on import. A layer
        // of an array has to go back with every mip, since all layers share them.
        if (texture.ArrayLength > 1) return all.ToBytes();
        return new DdsImage(all.Width, all.Height, all.Format, [all.Mips[0]], all.IsSrgb, all.IsSnorm).ToBytes();
    }

    // ---- TXTG ----

    private sealed record TxtgFormatEntry(string Name, int BytesPerBlock, int BlockWidth, int BlockHeight, string Decoder);

    private static readonly Dictionary<int, TxtgFormatEntry> TxtgFormats = new()
    {
        [0x101] = new("ASTC_4x4_UNORM", 16, 4, 4, "astc"), [0x102] = new("ASTC_8x8_UNORM", 16, 8, 8, "astc"),
        [0x105] = new("ASTC_8x8_SRGB", 16, 8, 8, "astc"), [0x109] = new("ASTC_4x4_SRGB", 16, 4, 4, "astc"),
        [0x202] = new("BC1_UNORM", 8, 4, 4, "bc1"), [0x203] = new("BC1_SRGB", 8, 4, 4, "bc1"),
        [0x302] = new("BC1_UNORM", 8, 4, 4, "bc1"), [0x303] = new("BC1_SRGB", 8, 4, 4, "bc1"),
        [0x505] = new("BC3_SRGB", 16, 4, 4, "bc3"), [0x602] = new("BC4_UNORM", 8, 4, 4, "bc4"),
        [0x606] = new("BC4_UNORM", 8, 4, 4, "bc4"), [0x607] = new("BC4_UNORM", 8, 4, 4, "bc4"),
        [0x702] = new("BC5_UNORM", 16, 4, 4, "bc5"), [0x703] = new("BC5_UNORM", 16, 4, 4, "bc5"),
        [0x707] = new("BC5_UNORM", 16, 4, 4, "bc5"), [0x901] = new("BC7_UNORM", 16, 4, 4, "bc7"),
        [0x0B0B] = new("R8G8B8A8_UNORM", 4, 1, 1, "rgba8"), [0x0C0C] = new("B8G8R8A8_UNORM", 4, 1, 1, "bgra8"),
    };

    private static TxtgFormatEntry ResolveTxtgFormat(int formatId, uint setting2)
    {
        if (!TxtgFormats.TryGetValue(formatId, out TxtgFormatEntry? entry)) return new($"Unknown(0x{formatId:X4})", 0, 1, 1, "unknown");
        if (formatId == 0x101 && setting2 == 32628) return new("ASTC_8x5_UNORM", 16, 8, 5, "astc");
        if (formatId == 0x101 && setting2 == 32631) return new("ASTC_8x8_UNORM", 16, 8, 8, "astc");
        return entry;
    }

    // Formats the old table lacks (such as 0x010A) but TxtgSharp knows, named the way the table names them.
    private static TxtgFormatEntry? LibraryFormat(byte[] data)
    {
        try
        {
            TxtgFile file = TxtgFile.FromBytes(data);
            string name = file.Format switch
            {
                TxtgFormat.Astc4x4Srgb => "ASTC_4x4_SRGB", TxtgFormat.Astc4x4Unorm => "ASTC_4x4_UNORM",
                TxtgFormat.Astc8x5Unorm => "ASTC_8x5_UNORM", TxtgFormat.Astc8x8Unorm => "ASTC_8x8_UNORM",
                TxtgFormat.Astc8x8Srgb => "ASTC_8x8_SRGB", TxtgFormat.Bc1Unorm => "BC1_UNORM",
                TxtgFormat.Bc1UnormSrgb => "BC1_SRGB", TxtgFormat.Bc3UnormSrgb => "BC3_SRGB",
                TxtgFormat.Bc4Unorm => "BC4_UNORM", TxtgFormat.Bc5Unorm => "BC5_UNORM", TxtgFormat.Bc7Unorm => "BC7_UNORM",
                TxtgFormat.R8Unorm => "R8_UNORM", TxtgFormat.R8G8Unorm => "R8G8_UNORM", TxtgFormat.R8G8B8A8Unorm => "R8G8B8A8_UNORM",
                _ => "",
            };
            if (name.Length == 0) return null;
            return new TxtgFormatEntry(name, file.BlockInfo.BytesPerBlock, file.BlockInfo.Width, file.BlockInfo.Height, name.StartsWith("ASTC", StringComparison.Ordinal) ? "astc" : "lib");
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool IsTxtg(byte[] data) => data.Length >= 8 && data.AsSpan(4, 4).SequenceEqual("6PK0"u8);

    // The block height (in GOBs) the Switch uses for the first mip, from the surface height in blocks.
    private static int BlockHeightMip0(int heightInBlocks)
    {
        int h = heightInBlocks + heightInBlocks / 2;
        return h >= 128 ? 16 : h >= 64 ? 8 : h >= 32 ? 4 : h >= 16 ? 2 : 1;
    }

    private static int FirstSurfaceSize(byte[] data, int headerSize, int surfaceCount)
    {
        if (surfaceCount <= 0) return 0;
        int cursor = headerSize;
        if (cursor + surfaceCount * 4 + surfaceCount * 8 > data.Length) return 0;

        cursor += surfaceCount * 4;
        int firstSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor));
        cursor += surfaceCount * 8;
        if (firstSize <= 0 || cursor + firstSize > data.Length) return 0;

        try
        {
            using Decompressor decompressor = new();
            return decompressor.Unwrap(data.AsSpan(cursor, firstSize)).Length;
        }
        catch (ZstdException)
        {
            return firstSize;
        }
    }

    /// <summary>The viewer's result for a TXTG: metadata, and a PNG when it can be decoded.</summary>
    public static JsonObject TxtgTextureResult(byte[] data, string textureName, int layer = 0)
    {
        if (!IsTxtg(data)) throw new InvalidDataException("Not a TXTG file.");

        int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data) is var h and not 0 ? h : 0x50;
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x08));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x0A));
        int arrayCount = Math.Max((int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x0C)), 1);
        int mipCount = Math.Max((int)data[0x0E], 1);
        byte compR = data[0x18], compG = data[0x19], compB = data[0x1A], compA = data[0x1B];
        int formatId = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x3C));
        uint setting2 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x44));
        uint setting4 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x4C));
        int blockHeightLog2 = (int)(setting4 & 0xFF);

        TxtgFormatEntry format = ResolveTxtgFormat(formatId, setting2);
        if (format.Decoder == "unknown" && LibraryFormat(data) is { } known) format = known;
        int imageSize = FirstSurfaceSize(data, headerSize, mipCount * arrayCount);

        string? error = null;
        string? pngPath = null;

        if (imageSize == 0) error = "TXTG has no readable surface payload.";
        else if (width <= 0 || height <= 0) error = $"Invalid texture dimensions ({width}x{height}).";
        else if (format.Decoder == "unknown" || format.BytesPerBlock <= 0) error = $"Unsupported TXTG format id 0x{formatId:X4}.";
        else
        {
            // Some ASTC 8x8 files are labelled 4x4; the surface is then half the size in each direction.
            bool astc8x8 = format.Name.Contains("ASTC_8x8", StringComparison.Ordinal) && format.Decoder == "astc";
            int blockHeight = astc8x8 ? 4 : format.BlockHeight;
            int renderHeight = astc8x8 ? height / 2 : height;
            try
            {
                TxtgFile file = TxtgFile.FromBytes(data);
                CheckLayer(layer, file.LayerCount);
                byte[] png = file.ToPng(layer);
                pngPath = TempFile("totk-txtg-", $"-{SafeName(textureName)}.png", png);
                blockHeightLog2 = int.Log2(BlockHeightMip0((renderHeight + blockHeight - 1) / blockHeight));
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                error = $"Decode failed ({format.Decoder}): {e.Message}";
            }
        }

        string Channel(byte c) => c switch { 0 => "Red", 1 => "Green", 2 => "Blue", 3 => "Alpha", 4 => "Zero", 5 => "One", _ => $"Unknown({c})" };
        string formatIdText = $"0x{formatId:X4}";

        JsonObject result = new()
        {
            ["bntxTexture"] = true,
            ["layer"] = layer,
            ["metadata"] = new JsonObject
            {
                ["name"] = textureName,
                ["channels"] = new JsonObject { ["red"] = Channel(compR), ["green"] = Channel(compG), ["blue"] = Channel(compB), ["alpha"] = Channel(compA) },
                ["imageInfo"] = new JsonObject
                {
                    ["width"] = width,
                    ["height"] = height,
                    ["mipCount"] = mipCount,
                    ["format"] = format.Name,
                    ["formatId"] = formatIdText,
                    ["useSRGB"] = format.Name.Contains("SRGB", StringComparison.Ordinal) ? "True" : "False",
                    ["name"] = textureName,
                    ["accessFlags"] = "Texture",
                },
                ["misc"] = new JsonObject
                {
                    ["depth"] = 1,
                    ["tileMode"] = "Default",
                    ["swizzle"] = 0,
                    ["alignment"] = 0x200,
                    ["pitch"] = 0,
                    ["dims"] = "Dim2D",
                    ["surfaceShape"] = "Dim2D",
                    ["flags"] = 0,
                    ["imageSize"] = imageSize,
                    ["sampleCount"] = 1,
                },
                ["width"] = width,
                ["height"] = height,
                ["format"] = format.Name,
                ["formatId"] = formatIdText,
                ["mipCount"] = mipCount,
                ["arrayCount"] = arrayCount,
                ["dataSize"] = imageSize,
                ["tileMode"] = "Default",
                ["blockH"] = 1 << Math.Clamp(blockHeightLog2, 0, 5),
                ["blockHLog2"] = blockHeightLog2,
            },
        };

        if (pngPath is not null) result["pngPath"] = pngPath;
        else if (error is not null) result["error"] = error;
        return result;
    }

    /// <summary>Edits a TXTG's colour space and channel selectors, as the viewer's save does.</summary>
    public static byte[] UpdateTxtgMetadata(byte[] txtg, JsonObject metadata)
    {
        byte[] data = (byte[])txtg.Clone();
        int format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x3C));
        uint setting2 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x44));

        if (metadata["useSRGB"] is JsonValue s && s.TryGetValue(out bool useSrgb))
        {
            if (format == 0x101 && setting2 == 32631) format = 0x102;

            Dictionary<int, int> toSrgb = new()
            {
                [0x101] = 0x109, [0x109] = 0x109, [0x102] = 0x105, [0x105] = 0x105, [0x202] = 0x203, [0x203] = 0x203,
                [0x302] = 0x303, [0x303] = 0x303, [0x505] = 0x505, [0x602] = 0x602, [0x606] = 0x606, [0x607] = 0x607,
                [0x702] = 0x703, [0x703] = 0x703,
            };
            Dictionary<int, int> toUnorm = new()
            {
                [0x109] = 0x101, [0x101] = 0x101, [0x105] = 0x102, [0x102] = 0x102, [0x203] = 0x202, [0x202] = 0x202,
                [0x303] = 0x302, [0x302] = 0x302, [0x505] = 0x505, [0x602] = 0x602, [0x606] = 0x606, [0x607] = 0x607,
                [0x703] = 0x702, [0x702] = 0x702,
            };

            if (useSrgb)
            {
                if (format == 0x101 && setting2 is not (0 or 32631)) { }
                else if (toSrgb.TryGetValue(format, out int mapped)) format = mapped;
            }
            else if (toUnorm.TryGetValue(format, out int mapped)) format = mapped;

            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x3C), (ushort)format);
        }

        string[] selectors = ["Red", "Green", "Blue", "Alpha", "Zero", "One"];
        string[] keys = ["red", "green", "blue", "alpha"];
        for (int i = 0; i < keys.Length; i++)
            if (metadata[keys[i]] is JsonValue v && v.TryGetValue(out string? channel) && Array.IndexOf(selectors, channel) is var code and >= 0)
                data[0x18 + i] = (byte)code;

        return data;
    }

    /// <summary>Puts the pixels of a DDS in a TXTG, keeping the rest of its header.</summary>
    public static (byte[] Txtg, JsonObject Info) ReplaceTxtgPayload(byte[] txtg, byte[] dds, int layer = 0)
    {
        TxtgFile original = TxtgFile.FromBytes(txtg);
        DdsImage image = DdsImage.Parse(dds);
        TxtgFile edited;
        IReadOnlyList<string> changes = [];
        if (original.LayerCount > 1)
        {
            changes = original.ReplaceLayerFromDds(image, layer);
            edited = original;
        }
        else
        {
            CheckLayer(layer, 1);
            edited = original.WithDds(image);
        }
        JsonObject info = new()
        {
            ["width"] = edited.Width,
            ["height"] = edited.Height,
            ["mipCount"] = edited.MipCount,
            ["format"] = edited.FormatName,
        };
        NoteConversion(info, changes, layer);
        return (edited.ToBytes(), info);
    }

    // What the reply says when an image had to be converted to fit a layer, for the editor to show.
    private static void NoteConversion(JsonObject info, IReadOnlyList<string> changes, int layer)
    {
        if (changes.Count == 0) return;
        info["converted"] = new JsonArray([.. changes.Select(c => (JsonNode)JsonValue.Create(c)!)]);
        info["note"] = $"Layer {layer} was converted to fit: {string.Join(", ", changes)}.";
    }

    public static byte[]? TxtgDds(byte[] txtg, int layer = 0)
    {
        TxtgFile file = TxtgFile.FromBytes(txtg);
        CheckLayer(layer, file.LayerCount);
        return file.TryGetTextureFormat(out _, out _) ? file.ToDds(layer).ToBytes() : null;
    }

    // ---- other image files ----

    /// <summary>A 32-bit BMP of RGBA pixels (with the alpha channel, in a V4 header).</summary>
    public static byte[] Bmp(byte[] rgba, int width, int height)
    {
        const int header = 14 + 108;
        int imageSize = width * height * 4;
        byte[] file = new byte[header + imageSize];
        "BM"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), header);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(14), 108);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(28), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(30), 3); // BI_BITFIELDS
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(34), (uint)imageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(54), 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(58), 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(62), 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(66), 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(70), 0x73524742); // sRGB

        for (int y = 0; y < height; y++)
        {
            int source = (height - 1 - y) * width * 4;
            int target = header + y * width * 4;
            for (int x = 0; x < width; x++)
            {
                file[target + x * 4 + 0] = rgba[source + x * 4 + 2];
                file[target + x * 4 + 1] = rgba[source + x * 4 + 1];
                file[target + x * 4 + 2] = rgba[source + x * 4 + 0];
                file[target + x * 4 + 3] = rgba[source + x * 4 + 3];
            }
        }

        return file;
    }

    /// <summary>A 32-bit uncompressed TGA of RGBA pixels, top row first.</summary>
    public static byte[] Tga(byte[] rgba, int width, int height)
    {
        byte[] file = new byte[18 + width * height * 4];
        file[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(14), (ushort)height);
        file[16] = 32;
        file[17] = 0x28; // 8 alpha bits, origin at the top left
        for (int i = 0; i < width * height; i++)
        {
            file[18 + i * 4 + 0] = rgba[i * 4 + 2];
            file[18 + i * 4 + 1] = rgba[i * 4 + 1];
            file[18 + i * 4 + 2] = rgba[i * 4 + 0];
            file[18 + i * 4 + 3] = rgba[i * 4 + 3];
        }

        return file;
    }

    // ---- pixels, for formats that are not PNG ----

    /// <summary>The texture as the viewer shows it, as RGBA bytes: BNTX.</summary>
    public static (byte[] Rgba, int Width, int Height)? BntxPixels(byte[] bntx, string name, int layer = 0)
    {
        BntxTexture? texture = Find(BntxFile.Load(bntx), name);
        if (texture is null || !texture.TryGetTextureFormat(out _, out _, out _)) return null;
        CheckLayer(layer, texture.ArrayLength);
        return (texture.Render(0, layer), texture.Width, texture.Height);
    }

    /// <summary>The texture as the viewer shows it, as RGBA bytes: TXTG.</summary>
    public static (byte[] Rgba, int Width, int Height)? TxtgPixels(byte[] txtg, int layer = 0)
    {
        TxtgFile file = TxtgFile.FromBytes(txtg);
        CheckLayer(layer, file.LayerCount);
        return file.TryGetTextureFormat(out _, out _) ? (file.Render(layer), file.Width, file.Height) : null;
    }

    public static byte[] Png(byte[] rgba, int width, int height) => PngWriter.Encode(rgba, width, height);

    /// <summary>Quality 75 with the alpha dropped, as converting RGBA to RGB and saving does in Pillow.</summary>
    public static byte[] Jpeg(byte[] rgba, int width, int height) => JpegWriter.Encode(rgba, width, height);
}

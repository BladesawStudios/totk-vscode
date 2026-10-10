using System.Buffers.Binary;
using System.Text;

namespace TkvscHost.Formats;

/// <summary>
/// The textures in a .bntx as archive entries: their names, and each one's raw (still swizzled) bytes.
/// Port of the listing part of <c>bntx_reader.py</c>; decoding and editing textures is the BNTX libraries' job.
/// </summary>
public static class BntxNames
{
    private static readonly byte[] Magic = "BNTX\0\0\0\0"u8.ToArray();

    public static bool IsBntx(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(Magic);

    public sealed record Texture(string Name, long DataOffset, long DataSize);

    /// <summary>What a texture's header says, as <c>_parse_textures</c> reads it.</summary>
    public sealed record Info(
        string Name, string Path, int Width, int Height, uint FormatId, int MipCount, int Depth, int Dims, int TileMode,
        int Swizzle, int SampleCount, uint AccessFlags, int Flags, uint Alignment, uint ImageSize, int BlockHeightLog2,
        byte ChannelR, byte ChannelG, byte ChannelB, byte ChannelA, long DataOffset, long DataSize, int Pitch, int ArrayCount)
    {
        public Texture ToTexture() => new(Name, DataOffset, DataSize);
    }

    private static long I64(byte[] d, long at, bool le) => le ? BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan((int)at)) : BinaryPrimitives.ReadInt64BigEndian(d.AsSpan((int)at));
    private static int I32(byte[] d, long at, bool le) => le ? BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan((int)at)) : BinaryPrimitives.ReadInt32BigEndian(d.AsSpan((int)at));
    private static uint U32(byte[] d, long at, bool le) => le ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)at)) : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)at));
    private static ushort U16(byte[] d, long at, bool le) => le ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)at)) : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)at));

    // A name is a 16-bit length, the text and a zero; failing that, a plain C string.
    private static string ReadString(byte[] d, long offset, bool le)
    {
        if (offset < 0 || offset >= d.Length) return "";
        if (offset + 3 <= d.Length && d.AsSpan((int)offset, 3).SequenceEqual("NX "u8)) return "";

        if (offset + 2 <= d.Length)
        {
            int length = U16(d, offset, le);
            if (length <= 256 && offset + 2 + length < d.Length && d[offset + 2 + length] == 0)
                return Encoding.UTF8.GetString(d, (int)offset + 2, length);
        }

        int end = Array.IndexOf(d, (byte)0, (int)offset);
        if (end < 0) end = (int)Math.Min(offset + 256, d.Length);
        return Encoding.UTF8.GetString(d, (int)offset, end - (int)offset);
    }

    // Bytes per pixel of the uncompressed formats, for the pitch of a linear texture.
    private static int PixelBytes(uint formatId) => (formatId >> 8) switch
    {
        0x01 or 0x02 => 1,
        >= 0x03 and <= 0x09 => 2,
        _ => 4,
    };

    public static List<Texture> Parse(byte[] data) => [.. ParseInfo(data).Select(i => i.ToTexture())];

    public static List<Info> ParseInfo(byte[] data)
    {
        List<Info> textures = [];
        long length = data.Length;
        if (length < 0x58 || !IsBntx(data)) return textures;

        bool le = data[0x0C] == 0xFF && data[0x0D] == 0xFE;
        int count = I32(data, 0x24, le);
        if (count <= 0) return textures;

        long pointers = I64(data, 0x28, le);
        if (pointers < 0 || pointers + 8L * count > length) return textures;

        for (int i = 0; i < count; i++)
        {
            long brti = I64(data, pointers + 8L * i, le);
            if (brti < 0 || brti + 0x70 > length) continue;
            if (!data.AsSpan((int)brti, 4).SequenceEqual("BRTI"u8)) continue;

            long d = brti + 0x10;
            byte At(long offset) => d + offset < length ? data[d + offset] : (byte)0;

            int tileMode = U16(data, d + 0x02, le);
            int width = I32(data, d + 0x14, le);
            uint formatId = U32(data, d + 0x0C, le);
            uint imageSize = U32(data, d + 0x40, le);

            long nameAddress = I64(data, d + 0x50, le);
            string name = nameAddress > 0 && nameAddress < length ? ReadString(data, nameAddress, le) : $"texture_{i}";

            long pathAddress = I64(data, d + 0x58, le);
            string path = pathAddress > 0 && pathAddress < length ? ReadString(data, pathAddress, le) : "";

            long mipPointers = I64(data, d + 0x60, le);
            long firstMip = mipPointers > 0 && mipPointers < length ? I64(data, mipPointers, le) : 0;

            textures.Add(new Info(
                name, path, width, I32(data, d + 0x18, le), formatId, U16(data, d + 0x06, le), I32(data, d + 0x1C, le),
                At(0x01), tileMode, U16(data, d + 0x04, le), U16(data, d + 0x08, le), U32(data, d + 0x10, le), At(0x00),
                U32(data, d + 0x44, le), imageSize, (int)(U32(data, d + 0x24, le) & 7), At(0x48), At(0x49), At(0x4A), At(0x4B),
                firstMip, imageSize, tileMode == 1 && width > 0 ? width * PixelBytes(formatId) : 0, Math.Max(1, I32(data, d + 0x20, le))));
        }

        return textures;
    }

    public static List<string> List(byte[] data) => [.. ParseInfo(data).Select(t => t.Name)];

    public static byte[] ReadTextureData(byte[] data, string textureName)
    {
        foreach (Texture texture in Parse(data))
        {
            if (texture.Name != textureName) continue;
            if (texture.DataOffset <= 0 || texture.DataSize <= 0)
                throw new InvalidDataException($"Texture '{textureName}' has no extractable data");
            return data.AsSpan((int)texture.DataOffset, (int)texture.DataSize).ToArray();
        }

        throw new FileNotFoundException($"Texture not found in BNTX: '{textureName}'");
    }
}

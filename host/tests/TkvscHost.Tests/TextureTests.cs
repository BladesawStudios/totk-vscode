using System.Buffers.Binary;
using System.Text.Json.Nodes;
using TkvscHost.Formats;

namespace TkvscHost.Tests;

public class TextureTests
{
    [Theory]
    [InlineData(0x1A01u, "BC1_UNORM")]
    [InlineData(0x1A06u, "BC1_SRGB")]
    [InlineData(0x1D02u, "BC4_SNORM")]
    [InlineData(0x1F05u, "BC6H")]
    [InlineData(0x2D06u, "ASTC_4x4_SRGB")]
    [InlineData(0x0B01u, "R8G8B8A8_UNORM")]
    [InlineData(0x7701u, "Unknown(0x7701)")]
    public void BntxFormatsAreNamedAsTheViewerShowsThem(uint formatId, string expected)
        => Assert.Equal(expected, Textures.BntxFormatName(formatId));

    private static byte[] SampleBntx(uint format = 0x1A06, byte tileMode = 0)
    {
        byte[] data = new byte[0x400];
        "BNTX"u8.CopyTo(data);
        data[0x0C] = 0xFF; data[0x0D] = 0xFE;
        BitConverter.GetBytes(1).CopyTo(data, 0x24);
        BitConverter.GetBytes(0x198L).CopyTo(data, 0x28);
        BitConverter.GetBytes(0x200L).CopyTo(data, 0x198);

        int d = 0x200 + 0x10;
        "BRTI"u8.CopyTo(data.AsSpan(0x200));
        data[d + 1] = 2;                                          // Dim2D
        BitConverter.GetBytes((ushort)tileMode).CopyTo(data, d + 2);
        BitConverter.GetBytes((ushort)3).CopyTo(data, d + 6);     // mips
        BitConverter.GetBytes(format).CopyTo(data, d + 0x0C);
        BitConverter.GetBytes(0x20u).CopyTo(data, d + 0x10);      // access flags
        BitConverter.GetBytes(64).CopyTo(data, d + 0x14);
        BitConverter.GetBytes(32).CopyTo(data, d + 0x18);
        BitConverter.GetBytes(1).CopyTo(data, d + 0x1C);
        BitConverter.GetBytes(4u).CopyTo(data, d + 0x24);         // block height 2^4
        BitConverter.GetBytes(0x2000u).CopyTo(data, d + 0x40);
        BitConverter.GetBytes(0x200u).CopyTo(data, d + 0x44);
        data[d + 0x48] = 2; data[d + 0x49] = 3; data[d + 0x4A] = 4; data[d + 0x4B] = 5;
        BitConverter.GetBytes(0x2E0L).CopyTo(data, d + 0x50);
        BitConverter.GetBytes((ushort)3).CopyTo(data, 0x2E0);
        "Mat"u8.CopyTo(data.AsSpan(0x2E2));
        BitConverter.GetBytes(0x300L).CopyTo(data, d + 0x58);
        BitConverter.GetBytes((ushort)4).CopyTo(data, 0x300);
        "a/b."u8.CopyTo(data.AsSpan(0x302));
        return data;
    }

    [Fact]
    public void ABntxTextureGivesTheViewersMetadata()
    {
        BntxNames.Info info = Assert.Single(BntxNames.ParseInfo(SampleBntx()));

        JsonObject meta = Textures.BntxMetadata(info);

        Assert.Equal("Mat", meta["name"]!.GetValue<string>());
        Assert.Equal("Red", meta["channels"]!["red"]!.GetValue<string>());
        Assert.Equal("Alpha", meta["channels"]!["alpha"]!.GetValue<string>());
        JsonNode image = meta["imageInfo"]!;
        Assert.Equal(64, image["width"]!.GetValue<int>());
        Assert.Equal("BC1_SRGB", image["format"]!.GetValue<string>());
        Assert.Equal("0x1A06", image["formatId"]!.GetValue<string>());
        Assert.Equal("True", image["useSRGB"]!.GetValue<string>());
        Assert.Equal("a/b.", image["path"]!.GetValue<string>());
        Assert.Equal("Texture", image["accessFlags"]!.GetValue<string>());
        Assert.Equal("Default", meta["tileMode"]!.GetValue<string>());
        Assert.Equal(16, meta["blockH"]!.GetValue<int>());
        Assert.Equal(4, meta["blockHLog2"]!.GetValue<int>());
        Assert.Equal("Dim2D", meta["misc"]!["dims"]!.GetValue<string>());
    }

    [Fact]
    public void ALinearBntxTextureHasAPitch()
    {
        BntxNames.Info info = Assert.Single(BntxNames.ParseInfo(SampleBntx(format: 0x0B01, tileMode: 1)));

        Assert.Equal(64 * 4, info.Pitch);
        Assert.Equal("Linear", Textures.BntxMetadata(info)["tileMode"]!.GetValue<string>());
    }

    private static byte[] SampleTxtgHeader(ushort format, byte red = 0, byte green = 1, byte blue = 2, byte alpha = 3)
    {
        byte[] data = new byte[0x50];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 0x50);
        "6PK0"u8.CopyTo(data.AsSpan(4));
        data[0x18] = red; data[0x19] = green; data[0x1A] = blue; data[0x1B] = alpha;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x3C), format);
        return data;
    }

    [Fact]
    public void TxtgChannelsAndColourSpaceAreChangedInTheHeader()
    {
        byte[] header = SampleTxtgHeader(0x202);
        JsonObject edit = new() { ["red"] = "Blue", ["blue"] = "Red", ["alpha"] = "One", ["useSRGB"] = true };

        byte[] changed = Textures.UpdateTxtgMetadata(header, edit);

        Assert.Equal([2, 1, 0, 5], changed[0x18..0x1C]);
        Assert.Equal(0x203, BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(0x3C)));
        Assert.Equal(0x202, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x3C))); // the input is not touched

        byte[] back = Textures.UpdateTxtgMetadata(changed, new JsonObject { ["useSRGB"] = false });
        Assert.Equal(0x202, BinaryPrimitives.ReadUInt16LittleEndian(back.AsSpan(0x3C)));
    }

    [Fact]
    public void ATxtgWithNoPayloadSaysSoInsteadOfFailing()
    {
        JsonObject result = Textures.TxtgTextureResult(SampleTxtgHeader(0x202), "x.txtg");

        Assert.Equal("BC1_UNORM", result["metadata"]!["format"]!.GetValue<string>());
        Assert.Equal("Blue", Textures.TxtgTextureResult(SampleTxtgHeader(0x202, blue: 2), "x.txtg")["metadata"]!["channels"]!["blue"]!.GetValue<string>());
        Assert.Contains("no readable surface", result["error"]!.GetValue<string>());
    }

    [Fact]
    public void BmpAndTgaHoldTheSamePixels()
    {
        // Two pixels wide, two high: red, green / blue, translucent white.
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 128];

        byte[] tga = Textures.Tga(rgba, 2, 2);
        byte[] bmp = Textures.Bmp(rgba, 2, 2);

        Assert.Equal(18 + 16, tga.Length);
        Assert.Equal([0, 0, 255, 255], tga[18..22]);      // top row first, BGRA
        Assert.Equal([255, 255, 255, 128], tga[30..34]);
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal(122 + 16, bmp.Length);
        Assert.Equal([255, 0, 0, 255], bmp[122..126]);    // bottom row first: blue, then white
        Assert.Equal([0, 0, 255, 255], bmp[130..134]);
    }
}

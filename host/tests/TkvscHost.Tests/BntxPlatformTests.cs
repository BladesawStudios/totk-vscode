using System.Text;
using BntxSharp;
using BntxSharp.Tegra;
using TexSharp;
using TkvscHost.Formats;

namespace TkvscHost.Tests;

public class BntxPlatformTests
{
    private const int Size = 64;
    private const int TagOffset = 0x20;

    // Every pixel differs, so a wrong GOB order can't reproduce it.
    private static byte[] Pixels()
    {
        byte[] pixels = new byte[Size * Size * 4];
        for (int i = 0; i < Size * Size; i++)
        {
            pixels[i * 4] = (byte)i;
            pixels[i * 4 + 1] = (byte)(i >> 8);
            pixels[i * 4 + 2] = 0x40;
            pixels[i * 4 + 3] = 0xFF;
        }
        return pixels;
    }

    private static byte[] Build(BntxPlatform platform)
    {
        BntxTexture texture = new()
        {
            Name = "Tex",
            Format = SurfaceFormat.R8_G8_B8_A8_UNORM,
            Depth = 1,
            ArrayLength = 1,
            GobLayout = platform == BntxPlatform.Switch2 ? GobLayout.Switch2 : GobLayout.TegraX1,
        };
        texture.ReplaceFromDds(new DdsImage(Size, Size, TextureFormat.Rgba8, [Pixels()]));

        BntxFile file = new() { Platform = platform };
        file.Textures.Add(texture);
        return file.Save();
    }

    private static string Tag(byte[] bntx) => Encoding.ASCII.GetString(bntx, TagOffset, 4);

    [Theory]
    [InlineData(BntxPlatform.Switch, "NX  ", GobLayout.TegraX1)]
    [InlineData(BntxPlatform.Switch2, "Ounc", GobLayout.Switch2)]
    public void TheTagSetsThePlatformAndGobOrderAndIsSavedBack(BntxPlatform platform, string tag, GobLayout layout)
    {
        byte[] bntx = Build(platform);
        Assert.Equal(tag, Tag(bntx));

        BntxFile loaded = BntxFile.Load(bntx);
        Assert.Equal(platform, loaded.Platform);
        Assert.Equal(layout, loaded.Textures[0].GobLayout);
        Assert.Equal(Pixels(), loaded.Textures[0].GetDeswizzledData(0, 0));
        Assert.Equal(bntx, loaded.Save());
    }

    [Fact]
    public void TheTwoGobOrdersStoreDifferentBytes()
    {
        BntxTexture switch1 = BntxFile.Load(Build(BntxPlatform.Switch)).Textures[0];
        BntxTexture switch2 = BntxFile.Load(Build(BntxPlatform.Switch2)).Textures[0];

        Assert.NotEqual(switch1.Data, switch2.Data);
    }

    [Fact]
    public void ReplacingATextureInASwitch2FileKeepsItsGobOrder()
    {
        BntxFile file = BntxFile.Load(Build(BntxPlatform.Switch2));
        BntxTexture texture = file.Textures[0];
        byte[] replacement = Pixels();
        replacement[0] = 0x7F;

        texture.ReplaceFromDds(new DdsImage(Size, Size, TextureFormat.Rgba8, [replacement]));
        byte[] saved = file.Save();

        Assert.Equal("Ounc", Tag(saved));
        Assert.Equal(replacement, BntxFile.Load(saved).Textures[0].GetDeswizzledData(0, 0));
    }

    [Fact]
    public void AnUnknownTagIsRefusedByName()
    {
        byte[] bntx = Build(BntxPlatform.Switch);
        "Wiiu"u8.CopyTo(bntx.AsSpan(TagOffset));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => BntxFile.Load(bntx));
        Assert.Contains("Wiiu", error.Message);
    }

    [Theory]
    [InlineData(0x0A01u, "R16_UNORM")]
    [InlineData(0x0A03u, "R16_UINT")]
    [InlineData(0x0A05u, "R16_FLOAT")]
    [InlineData(0x1505u, "R16G16B16A16_FLOAT")]
    [InlineData(0x1F0Au, "BC6H")]
    [InlineData(0x0202u, "R8_SNORM")]
    [InlineData(0x0701u, "R5G6B5_UNORM")]
    [InlineData(0x2D06u, "ASTC_4x4_SRGB")]
    [InlineData(0xFF01u, "Unknown(0xFF01)")]
    public void FormatsAreNamedForTheViewer(uint formatId, string name)
        => Assert.Equal(name, Textures.BntxFormatName(formatId));
}

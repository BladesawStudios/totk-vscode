using System.Text.Json.Nodes;
using BntxSharp;
using TexSharp;
using TkvscHost.Formats;
using TxtgSharp;

namespace TkvscHost.Tests;

public class ArrayTextureTests
{
    private static byte[] Fill(byte value, int pixels = 8 * 8) => Enumerable.Repeat(value, pixels * 4).ToArray();

    private static byte[] Dds(byte fill) => new DdsImage(8, 8, TextureFormat.Rgba8, [Fill(fill)]).ToBytes();

    // ---- TXTG ----

    // Three 8x8 RGBA layers, each filled with its own value (10, 50, 90).
    private static byte[] ThreeLayerTxtg()
    {
        List<TxtgSurfaceData> surfaces = [];
        for (int layer = 0; layer < 3; layer++) surfaces.Add(new TxtgSurfaceData(layer, 0, Fill((byte)(10 + 40 * layer))));
        return TxtgFile.Create(8, 8, TxtgFormat.R8G8B8A8Unorm, surfaces).ToBytes();
    }

    [Fact]
    public void ATxtgArrayReportsItsLayersAndShowsTheOneAskedFor()
    {
        byte[] txtg = ThreeLayerTxtg();

        JsonObject second = Textures.TxtgTextureResult(txtg, "x.txtg", 1);
        JsonObject missing = Textures.TxtgTextureResult(txtg, "x.txtg", 3);

        Assert.Equal(3, second["metadata"]!["arrayCount"]!.GetValue<int>());
        Assert.Equal(1, second["layer"]!.GetValue<int>());
        string png = second["pngPath"]!.GetValue<string>();
        try { Assert.True(File.Exists(png)); }
        finally { File.Delete(png); }
        Assert.Null(missing["pngPath"]);
        Assert.Contains("Layer 3 does not exist", missing["error"]!.GetValue<string>());
    }

    [Fact]
    public void ReplacingATxtgLayerLeavesTheOthers()
    {
        var (changed, info) = Textures.ReplaceTxtgPayload(ThreeLayerTxtg(), Dds(200), 1);

        TxtgFile back = TxtgFile.FromBytes(changed);
        Assert.Equal(3, back.LayerCount);
        Assert.All(back.Surface(0, 0)!.Data, b => Assert.Equal(10, b));
        Assert.All(back.Surface(1, 0)!.Data, b => Assert.Equal(200, b));
        Assert.All(back.Surface(2, 0)!.Data, b => Assert.Equal(90, b));
        Assert.Equal(1, info["mipCount"]!.GetValue<int>());
    }

    [Fact]
    public void ATxtgLayerThatIsNotThereIsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Textures.ReplaceTxtgPayload(ThreeLayerTxtg(), Dds(1), 3));

    [Fact]
    public void ALayerDdsOfTheWrongSizeIsConvertedAndTheReplyHasANote()
    {
        byte[] wrong = new DdsImage(4, 4, TextureFormat.Rgba8, [Fill(70, 16)]).ToBytes();

        var (changed, info) = Textures.ReplaceTxtgPayload(ThreeLayerTxtg(), wrong, 0);

        TxtgFile back = TxtgFile.FromBytes(changed);
        Assert.All(back.Surface(0, 0)!.Data, b => Assert.InRange(b, 69, 71));
        Assert.All(back.Surface(1, 0)!.Data, b => Assert.Equal(50, b));
        Assert.Equal("resized from 4x4 to 8x8", info["converted"]![0]!.GetValue<string>());
        Assert.Contains("Layer 0 was converted to fit", info["note"]!.GetValue<string>());
    }

    [Fact]
    public void ALayerThatFitsHasNoNote()
    {
        var (_, info) = Textures.ReplaceTxtgPayload(ThreeLayerTxtg(), Dds(5), 0);

        Assert.Null(info["note"]);
    }

    [Fact]
    public void ATxtgLayerExportsAsADdsOfThatLayer()
    {
        byte[] dds = Textures.TxtgDds(ThreeLayerTxtg(), 2)!;

        DdsImage image = DdsImage.Parse(dds);
        Assert.All(image.Mips[0], b => Assert.Equal(90, b));
    }

    // ---- BNTX ----

    private static byte[] ThreeLayerBntx()
    {
        BntxTexture texture = new()
        {
            Name = "Layers",
            Format = SurfaceFormat.R8_G8_B8_A8_UNORM,
            Width = 8,
            Height = 8,
            Depth = 1,
            ArrayLength = 3,
            TextureLayout = 0,
            Alignment = 0x200,
        };
        texture.MipOffsets.Add(0);
        texture.Data = new byte[texture.ArraySliceStride * 3];
        for (int layer = 0; layer < 3; layer++) texture.SetDeswizzledData(Fill((byte)(10 + 40 * layer)), 0, layer);

        BntxFile file = new();
        file.Textures.Add(texture);
        return file.Save();
    }

    [Fact]
    public void ABntxArrayReportsItsLayers()
    {
        byte[] bntx = ThreeLayerBntx();

        BntxNames.Info info = Assert.Single(BntxNames.ParseInfo(bntx));
        JsonObject result = Textures.BntxTextureResult(bntx, "Layers", 2);

        Assert.Equal(3, info.ArrayCount);
        Assert.Equal(3, result["metadata"]!["arrayCount"]!.GetValue<int>());
        Assert.Equal(2, result["layer"]!.GetValue<int>());
        string png = result["pngPath"]!.GetValue<string>();
        File.Delete(png);
    }

    [Fact]
    public void ReplacingABntxLayerLeavesTheOthers()
    {
        var (changed, _) = Textures.ReplaceBntxPayload(ThreeLayerBntx(), "Layers", Dds(200), 0);

        BntxTexture back = BntxFile.Load(changed).Textures[0];
        Assert.All(back.GetDeswizzledData(0, 0), b => Assert.Equal(200, b));
        Assert.All(back.GetDeswizzledData(0, 1), b => Assert.Equal(50, b));
        Assert.All(back.GetDeswizzledData(0, 2), b => Assert.Equal(90, b));
    }

    [Fact]
    public void ABntxArrayExportsEveryMipSoALayerCanGoBack()
    {
        byte[] bntx = ThreeLayerBntx();

        byte[] dds = Textures.BntxDds(bntx, "Layers", 1)!;
        var (changed, _) = Textures.ReplaceBntxPayload(bntx, "Layers", dds, 2);

        BntxTexture back = BntxFile.Load(changed).Textures[0];
        Assert.All(back.GetDeswizzledData(0, 2), b => Assert.Equal(50, b));
        Assert.Throws<ArgumentOutOfRangeException>(() => Textures.ReplaceBntxPayload(bntx, "Layers", dds, 3));
    }

    [Fact]
    public void ABntxLayerDdsOfAnotherSizeIsConverted()
    {
        byte[] big = new DdsImage(16, 16, TextureFormat.Rgba8, [Fill(120, 256)]).ToBytes();

        var (changed, info) = Textures.ReplaceBntxPayload(ThreeLayerBntx(), "Layers", big, 1);

        BntxTexture back = BntxFile.Load(changed).Textures[0];
        Assert.All(back.GetDeswizzledData(0, 1), b => Assert.InRange(b, 119, 121));
        Assert.All(back.GetDeswizzledData(0, 2), b => Assert.Equal(90, b));
        Assert.Contains("resized from 16x16 to 8x8", info["note"]!.GetValue<string>());
    }
}

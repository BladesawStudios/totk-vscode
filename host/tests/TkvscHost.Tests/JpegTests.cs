using TkvscHost.Formats;

namespace TkvscHost.Tests;

public class JpegTests
{
    [Fact]
    public void AJpegIsAWellFormedBaselineFileOfTheRightSize()
    {
        byte[] rgba = new byte[20 * 10 * 4];
        for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = (byte)(i % 251); rgba[i + 1] = 90; rgba[i + 2] = 200; rgba[i + 3] = 0; }

        byte[] jpeg = Textures.Jpeg(rgba, 20, 10);

        Assert.Equal([0xFF, 0xD8], jpeg[..2]);
        Assert.Equal([0xFF, 0xD9], jpeg[^2..]);
        int sof = Array.IndexOf(jpeg, (byte)0xC0, 2);
        while (jpeg[sof - 1] != 0xFF) sof = Array.IndexOf(jpeg, (byte)0xC0, sof + 1);
        Assert.Equal(10, (jpeg[sof + 4] << 8) | jpeg[sof + 5]);
        Assert.Equal(20, (jpeg[sof + 6] << 8) | jpeg[sof + 7]);
    }

    [Fact]
    public void ALowerQualityMakesASmallerFile()
    {
        byte[] rgba = new byte[64 * 64 * 4];
        new Random(1).NextBytes(rgba);

        Assert.True(TexSharp.JpegWriter.Encode(rgba, 64, 64, 20).Length < TexSharp.JpegWriter.Encode(rgba, 64, 64, 90).Length);
    }

    [Fact]
    public void ABadSizeIsRefused()
        => Assert.Throws<ArgumentException>(() => TexSharp.JpegWriter.Encode(new byte[8], 4, 4));
}

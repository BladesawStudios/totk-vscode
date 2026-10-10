using BymlSharp;
using TkvscHost.Formats;

namespace TkvscHost.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(0.0, "0.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(1.0, "1.0")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(0.5, "0.5")]
    [InlineData(0.1, "0.1")]
    [InlineData(0.10000000149011612, "0.10000000149011612")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1.5e300, "1.5e+300")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(123456789.125, "123456789.125")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(double.PositiveInfinity, "inf")]
    [InlineData(double.NaN, "nan")]
    public void FloatsPrintLikePythonRepr(double value, string expected)
        => Assert.Equal(expected, PyFloat.Repr(value));

    [Fact]
    public void ABymlShowsAsTheEditorText()
    {
        // Added out of order on purpose: the text keeps oead's order, which is by key bytes.
        Byml doc = Byml.Map(new Dictionary<string, Byml>
        {
            ["Tilde"] = "-dash",
            ["Key: odd"] = 7,
            ["D"] = 0.5,
            ["Neg"] = -5L,
            ["Hash"] = 0xDEADBEEFu,
            ["Big"] = 0xFFFFFFFFFFFFFFF0UL,
            ["Flag"] = true,
            ["Pos"] = Byml.Map(new Dictionary<string, Byml> { ["X"] = 1.5f, ["Y"] = 2f, ["Z"] = 0.1f }),
            ["Name"] = "x y",
            ["List"] = Byml.Array(
            [
                Byml.Map(new Dictionary<string, Byml> { ["A"] = 1, ["B"] = "y" }),
                Byml.Map(new Dictionary<string, Byml> { ["A"] = 2, ["B"] = "multi\nline" }),
                Byml.Map(new Dictionary<string, Byml>
                {
                    ["A"] = 3,
                    ["C"] = Byml.Map(new Dictionary<string, Byml> { ["P"] = 1, ["Q"] = 2 }),
                }),
            ]),
            ["One"] = Byml.Array([5]),
            ["Empty"] = Byml.Array(),
        });

        const string expected = """
            Big: !ul 0xfffffffffffffff0
            D: !f64 0.5
            Empty: []
            Flag: true
            Hash: !u 0xdeadbeef
            Key: odd: 7
            List:
              - A: 1
                B: y
              - A: 2
                B: |-
                  multi
                  line
              - A: 3
                C:
                  P: 1
                  Q: 2
            Name: x y
            Neg: !sl 0xfffffffffffffffb
            One: [5]
            Pos:
              X: 1.5
              Y: 2.0
              Z: 0.10000000149011612
            Tilde: "-dash"

            """;

        Assert.Equal(expected.Replace("\r\n", "\n"), BymlEditorFormat.ToEditorText(doc, 1));
    }

    [Fact]
    public void TheEditorTextReadsBackAsTheSameDocument()
    {
        Byml doc = Byml.Map(new Dictionary<string, Byml>
        {
            ["Big"] = 0xFFFFFFFFFFFFFFF0UL,
            ["Hash"] = 0xDEADBEEFu,
            ["Neg"] = -5L,
            ["Pos"] = Byml.Map(new Dictionary<string, Byml> { ["X"] = 1.5f, ["Z"] = 0.1f }),
            ["List"] = Byml.Array([Byml.Map(new Dictionary<string, Byml> { ["A"] = 1, ["B"] = "multi\nline" })]),
        });

        string text = BymlEditorFormat.ToEditorText(doc);
        byte[] binary = BymlFile.FromYaml(text).Write();

        Assert.Equal(text, BymlEditorFormat.ToEditorText(BymlFile.FromBinary(binary).Root));
    }

    [Theory]
    [InlineData("12345678901", "12345678901")]
    [InlineData("a: 18446744073709551615", "a: !ul 0xffffffffffffffff")]
    [InlineData("[1, 9223372036854775808]", "[1, !ul 0x8000000000000000]")]
    [InlineData("x: 9223372036854775807", "x: 9223372036854775807")]
    public void BigDecimalsKeepTheirUnsignedType(string text, string expected)
        => Assert.Equal(expected, FormatHandlers.NormalizeU64Literals(text));

    [Fact]
    public void ABfttfFontDecryptsBackToItsOpenFont()
    {
        // 0x36 f8 1a 1e is the Switch scrambling. The text after the 8-byte header is XORed with a key made of the
        // file size, so a small OTTO font can be built the same way.
        byte[] open = [.. "OTTO"u8, .. new byte[28]];
        uint key = 1231165446u ^ (uint)open.Length;
        byte[] keyBytes = [(byte)(key >> 24), (byte)(key >> 16), (byte)(key >> 8), (byte)key];

        byte[] file = new byte[8 + open.Length];
        new byte[] { 0x36, 0xF8, 0x1A, 0x1E }.CopyTo(file, 0);
        for (int i = 0; i < open.Length; i++) file[8 + i] = (byte)(open[i] ^ keyBytes[(8 + i) % 4]);

        Assert.Equal(open, BFontSharp.BFont.ToOpenFont(file));
    }

    [Fact]
    public void ABntxListsItsTexturesByName()
    {
        // The smallest file the lister accepts: a header, one pointer, one BRTI block with a name.
        byte[] data = new byte[0x300];
        "BNTX"u8.CopyTo(data);
        data[0x0C] = 0xFF; data[0x0D] = 0xFE;
        BitConverter.GetBytes(1).CopyTo(data, 0x24);
        BitConverter.GetBytes(0x198L).CopyTo(data, 0x28);
        BitConverter.GetBytes(0x200L).CopyTo(data, 0x198);
        "BRTI"u8.CopyTo(data.AsSpan(0x200));
        BitConverter.GetBytes(0x2E0L).CopyTo(data, 0x200 + 0x10 + 0x50);
        BitConverter.GetBytes((ushort)3).CopyTo(data, 0x2E0);
        "Mat"u8.CopyTo(data.AsSpan(0x2E2));

        Assert.True(BntxNames.IsBntx(data));
        Assert.Equal(["Mat"], BntxNames.List(data));
    }
}

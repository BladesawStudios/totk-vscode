using System.Text;
using TkvscHost.Formats;

namespace TkvscHost.Tests;

public class Vfx3TextTests
{
    private const string Prefix = "Fx/Vfx2ParticleRenderer/";
    private const long Section = 0x100, Block = 0x110, EmitterList = 0x140, Emitter = 0x150, TableList = 0x1B0, Names = 0x400;
    private const long RendererList = 0x1D0, Constant = 0x200, Keys = 0x220, Unset = 0x2B0, Renderer = 0x2E0;
    private const long KeyRange = Keys + 0x28, KeyData = Keys + 0x48;

    // Names in tests are checked against hashes read out of Ocarina of Time's particle files.
    [Theory]
    [InlineData("_0", 0x40a8cb417495ecf1UL)]
    [InlineData("ParticleMesh", 0xfd8a84267faf1e3aUL)]
    [InlineData("NnVfx3SamplerTexture0", 0x2e5b9d3a370a8d46UL)]
    public void NamesHashAsInTheGamesFiles(string name, ulong expected) => Assert.Equal(expected, Vfx3Hash.Of(name));

    [Fact]
    public void ATooLongNameHasNoHash() => Assert.Null(Vfx3Hash.Of(new string('x', 257)));

    private static void Put(byte[] d, long at, ulong value) => BitConverter.TryWriteBytes(d.AsSpan((int)at), value);
    private static void Put(byte[] d, long at, uint value) => BitConverter.TryWriteBytes(d.AsSpan((int)at), value);
    private static void Put(byte[] d, long at, float value) => BitConverter.TryWriteBytes(d.AsSpan((int)at), value);

    private static float Float(byte[] d, long at) => BitConverter.ToSingle(d, (int)at);

    private static byte[] Sample()
    {
        byte[] d = new byte[0x500];
        "BFFH"u8.CopyTo(d);
        Put(d, 0xAC, (uint)Section);
        Put(d, Section, (ulong)Block);

        "VFX3"u8.CopyTo(d.AsSpan((int)Block));
        Put(d, Block + 0x10, (ulong)EmitterList);
        Put(d, Block + 0x18, 1u);
        Put(d, EmitterList, (ulong)Emitter);

        "Fx\0"u8.CopyTo(d.AsSpan((int)Names));
        Put(d, Emitter, (ulong)Names);
        Put(d, Emitter + 8, (ulong)TableList);
        Put(d, Emitter + 0x10, 3u);
        Put(d, TableList, (ulong)Constant);
        Put(d, TableList + 8, (ulong)Keys);
        Put(d, TableList + 16, (ulong)Unset);

        // Renderers name themselves; a channel is looked up under its renderer's name.
        "Vfx2ParticleRenderer\0"u8.CopyTo(d.AsSpan((int)Names + 0x10));
        Put(d, Emitter + 8 + 4 * 16, (ulong)RendererList);
        Put(d, Emitter + 0x10 + 4 * 16, 1u);
        Put(d, RendererList, (ulong)Renderer);
        Put(d, Renderer + 0x20, (ulong)(Names + 0x10));

        Put(d, Constant, Vfx3Hash.Of(Prefix + "ParticleColor0/ParticleColor")!.Value);
        Put(d, Constant + 12, 0x0Du);
        float[] color = [0.5f, 0.25f, 0.125f, 1f];
        for (int i = 0; i < 4; i++) Put(d, Constant + 16 + i * 4, color[i]); // The game reads three; the fourth isn't part of it.

        Put(d, Keys, Vfx3Hash.Of(Prefix + "ParticleColor1/ParticleColor")!.Value);
        Put(d, Keys + 12, 0x51u);
        Put(d, Keys + 0x18, (ulong)KeyData);
        Put(d, Keys + 0x20, 2u);
        Put(d, Keys + 0x24, 16u);
        float[] range = [1f, 0.5f, 0.75f, 0f, 0.25f, 0f];
        for (int i = 0; i < range.Length; i++) Put(d, KeyRange + i * 4, range[i]);
        float[] keys = [0f, 0.5f, 0.75f, 0f, 1f, 0.25f, 0f, 1f];
        for (int i = 0; i < keys.Length; i++) Put(d, KeyData + i * 4, keys[i]);

        Put(d, Unset, Vfx3Hash.Of(Prefix + "GlobalColor0/Color")!.Value);
        Put(d, Unset + 12, 0x4Cu); // An expression.
        return d;
    }

    // The sample with its unset channel given a kind and the 16 bits the kind reads.
    private static byte[] WithKind(uint kind, ushort bits, float inFile = 0)
    {
        byte[] d = Sample();
        Put(d, Unset + 12, kind | (uint)bits << 13);
        Put(d, Unset + 16, inFile);
        return d;
    }

    private static string Set(byte[] d, string oldValue, string newValue)
        => PtclText.ToText(d).Replace("GlobalColor0/Color: " + oldValue, "GlobalColor0/Color: " + newValue);

    [Theory]
    [InlineData(0x14u, (ushort)1, 0f, "[1.0, 1.0, 1.0]")]    // A whole number on all three.
    [InlineData(0x0Eu, (ushort)1, 0f, "[1.0, 0.0, 0.0]")]    // On the first only.
    [InlineData(0x0Fu, (ushort)2, 0f, "[0.0, 2.0, 0.0]")]    // On the second only.
    [InlineData(0x10u, (ushort)2, 0f, "[0.0, 0.0, 2.0]")]    // On the third only.
    [InlineData(0x11u, (ushort)1, 0f, "[1.0, 1.0, 0.0]")]    // On the first two.
    [InlineData(0x12u, (ushort)1, 0f, "[0.0, 1.0, 1.0]")]    // On the last two.
    [InlineData(0x13u, (ushort)1, 0f, "[1.0, 0.0, 1.0]")]    // On the outer two.
    [InlineData(0x14u, (ushort)0xFFFF, 0f, "[-1.0, -1.0, -1.0]")] // The whole number is signed.
    [InlineData(0x1Bu, (ushort)99, 0f, "[0.5, 0.5, 0.5]")]  // Common number 99 on all three.
    [InlineData(0x15u, (ushort)5, 0f, "[-0.5, 0.0, 0.0]")]   // Common number 5 on the first only.
    [InlineData(0x22u, (ushort)0, 0.625f, "[0.625, 0.625, 0.625]")] // A number in the file on all three.
    [InlineData(0x1Cu, (ushort)0, 0.625f, "[0.625, 0.0, 0.0]")]
    public void AThreeNumberKindShowsWhatTheGameReadsFromIt(uint kind, ushort bits, float inFile, string shown)
    {
        byte[] d = WithKind(kind, bits, inFile);

        Assert.Contains("GlobalColor0/Color: " + shown, PtclText.ToText(d));
        Assert.Equal(d, PtclText.ApplyText(d, PtclText.ToText(d)));
    }

    [Fact]
    public void AValueInTheRecordsBitsIsRewrittenThere()
    {
        byte[] d = WithKind(0x14u, 1);

        byte[] edited = PtclText.ApplyText(d, Set(d, "[1.0, 1.0, 1.0]", "[3, 3, 0]"));

        Assert.Equal(0x11u | 3u << 13, BitConverter.ToUInt32(edited, (int)Unset + 12));
        Assert.Contains("GlobalColor0/Color: [3.0, 3.0, 0.0]", PtclText.ToText(edited));
    }

    [Fact]
    public void AWholeNumberKindMovesToTheCommonNumbersWhenItHasTo()
    {
        byte[] d = WithKind(0x14u, 1);

        byte[] edited = PtclText.ApplyText(d, Set(d, "[1.0, 1.0, 1.0]", "[0.5, 0.5, 0.5]"));

        Assert.Equal(0x1Bu | 99u << 13, BitConverter.ToUInt32(edited, (int)Unset + 12));
        Assert.Contains("GlobalColor0/Color: [0.5, 0.5, 0.5]", PtclText.ToText(edited));
    }

    [Fact]
    public void ACommonNumberKindMovesToAWholeNumberWhenItHasTo()
    {
        byte[] d = WithKind(0x1Bu, 99);

        byte[] edited = PtclText.ApplyText(d, Set(d, "[0.5, 0.5, 0.5]", "[1, 1, 1]"));

        Assert.Equal(0x14u | 1u << 13, BitConverter.ToUInt32(edited, (int)Unset + 12));
    }

    [Fact]
    public void ANumberTheRecordsBitsCantHoldIsRefused()
    {
        byte[] d = WithKind(0x14u, 1);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PtclText.ApplyText(d, Set(d, "[1.0, 1.0, 1.0]", "[0.123, 0.123, 0.123]")));
        Assert.Contains("no room", error.Message);
    }

    [Fact]
    public void ThreeDifferentNumbersInAOneNumberRecordAreRefused()
    {
        byte[] d = WithKind(0x22u, 0, 0.5f);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PtclText.ApplyText(d, Set(d, "[0.5, 0.5, 0.5]", "[0.5, 0.25, 0.5]")));
        Assert.Contains("must match", error.Message);
    }

    [Fact]
    public void ANumberInTheFileCanBeAnythingAndFillAnyOfTheThree()
    {
        byte[] d = WithKind(0x22u, 0, 0.5f);

        byte[] edited = PtclText.ApplyText(d, Set(d, "[0.5, 0.5, 0.5]", "[0.0, 0.3, 0.3]"));

        Assert.Equal(0x20u, BitConverter.ToUInt32(edited, (int)Unset + 12));
        Assert.Equal(0.3f, Float(edited, Unset + 16));
    }

    [Fact]
    public void AllZeroKeepsTheChannelsItFilled()
    {
        byte[] d = WithKind(0x14u, 1);

        byte[] edited = PtclText.ApplyText(d, Set(d, "[1.0, 1.0, 1.0]", "[0, 0, 0]"));

        Assert.Equal(0x14u, BitConverter.ToUInt32(edited, (int)Unset + 12));
    }

    [Fact]
    public void OneNumberKindsReadTheirBitsAndKeepTheRestOfTheWord()
    {
        byte[] d = Sample();
        Put(d, Unset, Vfx3Hash.Of(Prefix + "GlobalColor0/Alpha")!.Value);
        Put(d, Unset + 12, 0x02u | 99u << 13 | 0x400u); // Common number 99, with a bit outside the kind and the number set.

        string text = PtclText.ToText(d);
        Assert.Contains("GlobalColor0/Alpha: 0.5", text);

        byte[] edited = PtclText.ApplyText(d, text.Replace("GlobalColor0/Alpha: 0.5", "GlobalColor0/Alpha: 1"));
        Assert.Equal(0x01u | 1u << 13 | 0x400u, BitConverter.ToUInt32(edited, (int)Unset + 12));
    }

    [Fact]
    public void TheTextNamesEachChannelAndItsValues()
    {
        string text = PtclText.ToText(Sample());

        Assert.Contains("Fx:\n    Vfx2ParticleRenderer:\n", text);
        Assert.Contains("ParticleColor0/ParticleColor: [0.5, 0.25, 0.125]\n", text);
        Assert.Contains("-   value: [1.0, 0.25, 0.0]\n            keyframe: 1.0", text);
        Assert.Contains("GlobalColor0/Color: not_editable", text);
    }

    [Fact]
    public void AnUneditedTextChangesNothing()
    {
        byte[] data = Sample();

        Assert.Equal(data, PtclText.ApplyText(data, PtclText.ToText(data)));
    }

    [Fact]
    public void EditsChangeTheValuesAndKeepTheKeysRangeRight()
    {
        byte[] data = Sample();
        string text = PtclText.ToText(data)
            .Replace("[0.5, 0.25, 0.125]", "[0.1, 0.2, 0.3]")
            .Replace("-   value: [1.0, 0.25, 0.0]", "-   value: [2.0, 0.25, 0.0]");

        byte[] edited = PtclText.ApplyText(data, text);

        Assert.Equal(0.1f, Float(edited, Constant + 16));
        Assert.Equal(2f, Float(edited, KeyData + 16));
        Assert.Equal(2f, Float(edited, KeyRange));
        Assert.Equal(0f, Float(edited, KeyRange + 12));
        Assert.Equal(edited.Length, data.Length);
    }

    [Fact]
    public void ChangingTheNumberOfKeysIsRefused()
    {
        byte[] data = Sample();
        string oneKey = PtclText.ToText(data).Replace("        -   value: [1.0, 0.25, 0.0]\n            keyframe: 1.0\n", "");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PtclText.ApplyText(data, oneKey));
        Assert.Contains("adding or removing keys", error.Message);
    }

    [Fact]
    public void ANoteOnAChannelThatCantBeEditedIsIgnored()
    {
        byte[] data = Sample();
        string text = PtclText.ToText(data).Replace("GlobalColor0/Color: not_editable", "GlobalColor0/Color: [1, 2, 3, 4]");

        Assert.Equal(data, PtclText.ApplyText(data, text));
    }
}

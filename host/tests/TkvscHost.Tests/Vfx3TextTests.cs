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
        for (int i = 0; i < 4; i++) Put(d, Constant + 16 + i * 4, color[i]);

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
        Put(d, Unset + 12, 0x14u);
        return d;
    }

    [Fact]
    public void TheTextNamesEachChannelAndItsValues()
    {
        string text = PtclText.ToText(Sample());

        Assert.Contains("Fx:\n    Vfx2ParticleRenderer:\n", text);
        Assert.Contains("ParticleColor0/ParticleColor: [0.5, 0.25, 0.125, 1.0]", text);
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
            .Replace("[0.5, 0.25, 0.125, 1.0]", "[0.1, 0.2, 0.3, 0.4]")
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

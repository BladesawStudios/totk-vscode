using System.Buffers.Binary;
using System.Text;
using BymlSharp;
using TkvscHost.Formats;

namespace TkvscHost.Tests;

public class PtclAndTagTests
{
    // ---- particle data ----

    private const int EmitterBody = 0x20;
    private const int EmitterSize = EmitterBody + 0xF48 + 32 + 0x20;

    private static void Header(byte[] d, int pos, string signature, uint subsection, uint next, uint section)
    {
        Encoding.ASCII.GetBytes(signature).CopyTo(d, pos);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(pos + 8), subsection);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(pos + 12), next);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(pos + 20), section);
    }

    private static void Float(byte[] d, int pos, float value) => BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(pos), value);

    // A file with one emitter set ("Set") holding one emitter ("Glow"): constant colours and a two-key colour animation.
    private static byte[] Particles()
    {
        const int estaAt = 0x20, esetAt = 0x40, emitterAt = 0x120;
        byte[] d = new byte[emitterAt + EmitterSize];
        Encoding.ASCII.GetBytes("VFXB    ").CopyTo(d, 0);
        d[9] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(10), 0x33);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(12), 0xFEFF);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x16), estaAt);

        Header(d, estaAt, "ESTA", 0xFFFFFFFF, 0xFFFFFFFF, esetAt - estaAt);
        Header(d, esetAt, "ESET", emitterAt - esetAt, 0xFFFFFFFF, 0x20);
        Encoding.UTF8.GetBytes("Set").CopyTo(d, esetAt + 0x20 + 0x10);

        Header(d, emitterAt, "EMTR", 0xFFFFFFFF, 0xFFFFFFFF, EmitterBody);
        int body = emitterAt + EmitterBody;
        Encoding.UTF8.GetBytes("Glow").CopyTo(d, body + 0x10);
        float[] colours = [1f, 0.5f, 0.25f, 1f, 0f, 0.125f, 2f, 0.75f];
        for (int i = 0; i < colours.Length; i++) Float(d, body + 0xF48 + i * 4, colours[i]);

        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(body + 0x80), 1); // two colour keys for animation 0
        float[][] keys = [[0f, 0f, 0f, 0f], [0.5f, 0.5f, 0.5f, 0f], [1f, 0.25f, 0f, 1f]];
        for (int i = 0; i < keys.Length; i++)
            for (int c = 0; c < 4; c++) Float(d, body + 0x680 + i * 16 + c * 4, keys[i][c]);
        return d;
    }

    [Fact]
    public void ParticleDataBecomesTheYamlOfItsEmitterColours()
    {
        string text = PtclText.ToText(Particles());

        Assert.StartsWith("Set:\n    Glow:\n        const_color0: [1.0, 0.5, 0.25, 1.0]\n        const_color1: [0.0, 0.125, 2.0, 0.75]\n", text);
        Assert.Contains("        color_anim0:\n        -   value: [0.0, 0.0, 0.0]\n            keyframe: 0.0\n        -   value: [0.5, 0.5, 0.5]\n            keyframe: 0.0\n", text);
        Assert.Contains("        alpha_anim0:\n        -   value: [0.0, 0.0, 0.0]\n", text);
    }

    [Fact]
    public void EditedParticleYamlIsWrittenBackAndTheRestOfTheFileIsKept()
    {
        byte[] original = Particles();
        string text = PtclText.ToText(original).Replace("[1.0, 0.5, 0.25, 1.0]", "[0.1, 0.2, 0.3, 0.4]");

        byte[] edited = PtclText.ApplyText(original, text);

        Assert.Equal(original.Length, edited.Length);
        Assert.Equal(0.1f, BinaryPrimitives.ReadSingleLittleEndian(edited.AsSpan(0x120 + EmitterBody + 0xF48)));
        Assert.Equal(0.4f, BinaryPrimitives.ReadSingleLittleEndian(edited.AsSpan(0x120 + EmitterBody + 0xF48 + 12)));
        Assert.Contains("[0.10000000149011612, 0.20000000298023224, 0.30000001192092896, 0.4000000059604645]", PtclText.ToText(edited));
        // Nothing but the colour bytes changed.
        Assert.InRange(Enumerable.Range(0, original.Length).Count(i => original[i] != edited[i]), 1, 16);
    }

    [Fact]
    public void ExpandedParticleVectorsListEachNumber()
    {
        string text = PtclText.ToText(Particles(), compact: false);

        Assert.Contains("        const_color0:\n        - 1.0\n        - 0.5\n", text);
        Assert.Equal(PtclText.ToText(Particles()), PtclText.ToText(PtclText.ApplyText(Particles(), PtclText.ToText(Particles()))));
    }

    [Fact]
    public void DataThatIsNotParticlesIsRefused()
        => Assert.Throws<InvalidDataException>(() => PtclText.ToText(new byte[64]));

    [Fact]
    public void FloatsAreWrittenTheWayPyYamlWritesThem()
    {
        Assert.Equal("1.0e-05", PyYamlScalar.Float(0.00001));
        Assert.Equal("0.10000000149011612", PyYamlScalar.Float(0.1f));
        Assert.Equal(".inf", PyYamlScalar.Float(double.PositiveInfinity));
    }

    [Theory]
    [InlineData("Plain_Name", "Plain_Name")]
    [InlineData("123", "'123'")]
    [InlineData("yes", "'yes'")]
    [InlineData("a: b", "'a: b'")]
    [InlineData("it's", "it's")]
    [InlineData("-", "'-'")]
    [InlineData("", "''")]
    [InlineData("tab\there", "\"tab\\there\"")]
    public void StringsAreQuotedWhenYamlWouldReadThemAsSomethingElse(string value, string expected)
        => Assert.Equal(expected, PyYamlScalar.Write(value));

    // ---- the tag table ----

    private static Byml TagTable()
    {
        // Two actors and three tags. Bits (LSB first): actor 0 has tags 0 and 2, actor 1 has tag 1.
        return Byml.Map(new Dictionary<string, Byml>
        {
            ["PathList"] = Byml.Array(["Work", "ActorA", "x.gyml", "Work", "ActorB", "y.gyml"]),
            ["BitTable"] = Byml.From(new byte[] { 0b0010_0101, 0, 0, 0 }),
            ["RankTable"] = "",
            ["TagList"] = Byml.Array(["Alpha", "Beta", "Gamma"]),
        });
    }

    [Fact]
    public void ATagTableListsEachActorsTags()
    {
        // 0b00100101: bits 0, 2 (actor 0: Alpha, Gamma) and 5 (actor 1, tag index 2 = Gamma).
        string json = TagProduct.ToEditorText(TagTable(), yaml: false);

        Assert.Equal("""
            {
                "PathList": {
                    "Work|ActorA|x.gyml": [
                        "Alpha",
                        "Gamma"
                    ],
                    "Work|ActorB|y.gyml": [
                        "Gamma"
                    ]
                },
                "TagList": [
                    "Alpha",
                    "Beta",
                    "Gamma"
                ]
            }
            """.Replace("\r\n", "\n"), json);
    }

    [Fact]
    public void ATagTableCanBeShownAsYaml()
    {
        string yaml = TagProduct.ToEditorText(TagTable(), yaml: true);

        Assert.Equal("PathList:\n  Work|ActorA|x.gyml:\n  - Alpha\n  - Gamma\n  Work|ActorB|y.gyml:\n  - Gamma\nTagList:\n- Alpha\n- Beta\n- Gamma\n", yaml);
    }

    [Fact]
    public void ANonAsciiNameIsEscapedInTheJson()
    {
        Byml table = Byml.Map(new Dictionary<string, Byml>
        {
            ["PathList"] = Byml.Array(["a", "b", "c"]),
            ["BitTable"] = Byml.From(new byte[4]),
            ["RankTable"] = "",
            ["TagList"] = Byml.Array(["Ünï\"x"]),
        });

        Assert.Contains("\"\\u00dcn\\u00ef\\\"x\"", TagProduct.ToEditorText(table, yaml: false));
    }

    [Fact]
    public void EditedTagTextBuildsTheTableBack()
    {
        string json = TagProduct.ToEditorText(TagTable(), yaml: false);
        json = json[..json.IndexOf("\"Alpha\",", StringComparison.Ordinal)] + "\"Alpha\", \"Beta\"," + json[(json.IndexOf("\"Alpha\",", StringComparison.Ordinal) + 8)..];

        Assert.True(TagProduct.IsEditorText(json, out Byml document));
        Byml rebuilt = TagProduct.FromEditorText(document);

        Assert.Equal(["Work", "ActorA", "x.gyml", "Work", "ActorB", "y.gyml"], rebuilt["PathList"]!.AsArray.Select(x => x.String));
        Assert.Equal(["Alpha", "Beta", "Gamma"], rebuilt["TagList"]!.AsArray.Select(x => x.String));
        // Actor 0: Alpha, Beta, Gamma (bits 0-2); actor 1: Gamma (bit 5) -- Beta was added to actor 0 only.
        Assert.Equal(new byte[] { 0b0010_0111, 0, 0, 0 }, rebuilt["BitTable"]!.Binary);
        Assert.Equal("", rebuilt["RankTable"]!.String);
    }

    [Fact]
    public void OrdinaryBymlYamlIsNotATagTable()
        => Assert.False(TagProduct.IsEditorText("PathList:\n- a\n- b\nTagList: []\n", out _));
}

using BymlSharp;
using TkvscHost.Formats;

namespace TkvscHost.Tests;

public class HashNamesTests
{
    private static readonly string[] Lines =
    [
        "# Zelda TOTK hashes",
        "EnumValues;Foo.*;A,B",
        "63696a32;Bool;IsGet.Armor_001",
        "fb2499bf;Int;PlayerStatus.CurrentRupee",
        "00000001;Bool;Unknown",
        "00000002;Bool;Has.*",
    ];

    [Fact]
    public void OnlyRealNamesAreKept()
    {
        HashNames names = HashNames.Parse(Lines);

        Assert.Equal(2, names.Count);
        Assert.True(names.TryGet(0x63696a32, out string name));
        Assert.Equal("IsGet.Armor_001", name);
        Assert.False(names.TryGet(1, out _));
        Assert.False(names.TryGet(2, out _));
    }

    [Fact]
    public void HashLinesGetTheirNameAsAComment()
    {
        HashNames names = HashNames.Parse(Lines);
        string yaml = "Data:\n  Bool:\n    - DefaultValue: false\n      Hash: !u 0x63696a32\n    - DefaultValue: true\n      Hash: !u 0x12345678\n";

        string annotated = names.Annotate(yaml);

        Assert.Contains("Hash: !u 0x63696a32  # IsGet.Armor_001", annotated);
        Assert.Contains("Hash: !u 0x12345678\n", annotated);
    }

    [Fact]
    public void ANamedHashStillSavesAsTheSameNumber()
    {
        using Workspace w = new();
        string file = w.Path("List.byml");
        Byml doc = Byml.Map(new Dictionary<string, Byml>
        {
            ["Bool"] = Byml.Array([Byml.Map(new Dictionary<string, Byml> { ["Hash"] = (Byml)0x63696a32u, ["DefaultValue"] = false })]),
        });
        File.WriteAllBytes(file, doc.ToBinary());

        string text = HashNames.Parse(Lines).Annotate(w.Run("read-disk", [file])["content"]!.GetValue<string>());
        Assert.Contains("# IsGet.Armor_001", text);

        w.Run("write-disk", [file], text);
        Byml back = Byml.FromBinary(File.ReadAllBytes(file));
        Assert.Equal(0x63696a32u, back["Bool"]![0]!["Hash"]!.UInt);
    }
}

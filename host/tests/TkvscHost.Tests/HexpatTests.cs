namespace TkvscHost.Tests;

public class HexpatTests
{
    [Fact]
    public void APatternIsEvaluatedOverAFile()
    {
        using Workspace w = new();
        string file = w.Path("data.bin");
        File.WriteAllBytes(file, [0x41, 0x42, 0x43, 0x44, 1, 0, 0, 0]);

        var result = w.Run("evaluate-hexpat", [file], "struct H { char magic[4]; u32 version; };\nH h @ 0;\n");

        var h = result["ast"]![0]!;
        Assert.Equal("h", h["name"]!.GetValue<string>());
        Assert.Equal(8, h["size"]!.GetValue<int>());
        Assert.Equal("ABCD", h["children"]![0]!["value"]!.GetValue<string>());
        Assert.Equal("1", h["children"]![1]!["value"]!.GetValue<string>());
        Assert.Null(result["evaluationError"]);
    }

    [Fact]
    public void APatternThatStopsPartWayKeepsWhatItFound()
    {
        using Workspace w = new();
        string file = w.Path("data.bin");
        File.WriteAllBytes(file, [1, 2, 3]);

        var result = w.Run("evaluate-hexpat", [file], "u8 a @ 0;\nstd::error(\"no more\");\n");

        Assert.Single(result["ast"]!.AsArray());
        Assert.Contains("no more", result["evaluationError"]!.GetValue<string>());
    }

    [Fact]
    public void APatternThatDoesNotParseIsAnError()
    {
        using Workspace w = new();
        string file = w.Path("data.bin");
        File.WriteAllBytes(file, [1]);

        var result = w.Run("evaluate-hexpat", [file], "struct {");

        Assert.Contains("Hexpat error", result["error"]!.GetValue<string>());
    }
}

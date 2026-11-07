using System.Text.Json.Nodes;
using BfresSharp;
using BfresSharp.Container;
using BfresSharp.Model;
using BfresSharp.Model.Geometry;
using BfresSharp.Model.Material;
using BntxSharp;

namespace TkvscHost.Tests;

/// <summary>Recipes: texture, copy, delete and include steps, and previews.</summary>
public class RecipeStepTests
{
    private static byte[] Bntx(byte r, byte g, byte b)
    {
        BntxTexture texture = new()
        {
            Name = "Paint", Format = SurfaceFormat.R8_G8_B8_A8_UNORM, Width = 8, Height = 8, Depth = 1, ArrayLength = 1, TextureLayout = 0, Alignment = 0x200,
        };
        texture.MipOffsets.Add(0);
        texture.Data = new byte[texture.ArraySliceStride];
        byte[] pixels = new byte[8 * 8 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = 255; }
        texture.SetDeswizzledData(pixels, 0, 0);
        BntxFile file = new();
        file.Textures.Add(texture);
        return file.Save();
    }

    private static byte[] ModelBfres()
    {
        var material = new Material { ShaderParamData = new byte[16] };
        material.ShaderParams.Add("color", new ShaderParam { Name = "color", Type = ShaderParamType.Float4, DataOffset = 0 });
        ShaderParamValues.WriteFloats(material, material.ShaderParams["color"], [0.5f, 0.25f, 0.125f, 1f], false);
        var model = new ResModel { Name = "m" };
        model.Materials.Add("Mt_Body", material);
        model.VertexBuffers.Add(new VertexBuffer());
        model.Shapes.Add("Body", new Shape { Name = "Body" });
        var file = new ResFile
        {
            Name = "synthetic", Platform = BfresPlatform.Switch, Version = new BfresVersion(BfresPlatform.Switch, 0x00090000),
            DataAlignment = 0x2000, Alignment = 0x0C, TargetAddressSize = 0x40,
        };
        file.Models.Add("m", model);
        return BfresFile.Write(file);
    }

    // A game folder with two textures and a model, and a mod folder holding the given recipes.
    private static (Env Env, string Mod, string Game) Setup(Workspace w, params (string Name, string Text)[] recipes)
    {
        string game = w.Path("romfs-game");
        Directory.CreateDirectory(Path.Combine(game, "Tex"));
        Directory.CreateDirectory(Path.Combine(game, "Model"));
        File.WriteAllBytes(Path.Combine(game, "Tex", "a.bntx"), Bntx(200, 100, 50));
        File.WriteAllBytes(Path.Combine(game, "Tex", "b.bntx"), Bntx(10, 20, 30));
        File.WriteAllBytes(Path.Combine(game, "Model", "thing.bfres"), ModelBfres());
        string mod = w.Path("mod");
        Directory.CreateDirectory(mod);
        foreach (var (name, text) in recipes) File.WriteAllText(Path.Combine(mod, name), text);
        Env env = Env.From(name => name switch
        {
            "TKVSC_COMPRESSION_BACKEND" => "plain-zstd-yaz0",
            "TKVSC_HANDLER_MANIFEST" => w.Manifest(),
            "TKVSC_ROMFS" => game,
            _ => null,
        });
        return (env, mod, game);
    }

    private static JsonObject Run(Workspace w, Env env, string command, params string[] args)
        => (JsonObject)Commands.Find(command)!(w.Context(command, env, "", args));

    private static byte[] FirstPixel(string path) => BntxFile.LoadFile(path).Textures[0].ToRgba8(0)[..4];

    private static float[] Colour(string path)
    {
        Material material = BfresFile.Read(File.ReadAllBytes(path)).Models["m"].Materials["Mt_Body"];
        return ShaderParamValues.ReadFloats(material, material.ShaderParams["color"], false);
    }

    [Fact]
    public void ATintStepRecoloursATextureKeepingEachPixelsBrightness()
    {
        using Workspace w = new();
        var (env, mod, game) = Setup(w, ("t.recipe.yaml", """
            color: "#00FF00"

            Tex/a.bntx:
              '*': tint $color
            """));
        byte[] original = File.ReadAllBytes(Path.Combine(game, "Tex", "a.bntx"));

        JsonObject result = Run(w, env, "apply-recipe", Path.Combine(mod, "t.recipe.yaml"));

        Assert.Equal([0, 200, 0, 255], FirstPixel(Path.Combine(mod, "romfs", "Tex", "a.bntx")));
        Assert.Equal(1, result["files"]![0]!["changed"]!.GetValue<int>());
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(game, "Tex", "a.bntx")));
    }

    [Fact]
    public void AReplaceStepCannotChangeATexture()
    {
        using Workspace w = new();
        var (env, mod, _) = Setup(w, ("t.recipe.yaml", """
            Tex/a.bntx:
              '*': replace a -> b
            """));

        Assert.Contains("texture", Assert.Throws<InvalidDataException>(() => Run(w, env, "apply-recipe", Path.Combine(mod, "t.recipe.yaml"))).Message);
    }

    [Fact]
    public void ACopyStepMakesAFileACopyOfAnotherAndLaterStepsSeeTheChangesSoFar()
    {
        using Workspace w = new();
        var (env, mod, game) = Setup(w, ("t.recipe.yaml", """
            Tex/c.bntx: copy Tex/b.bntx
            Tex/d.bntx: copy Tex/a.bntx
            Tex/a.bntx:
              '*': tint "#0000FF"
            """));

        Run(w, env, "apply-recipe", Path.Combine(mod, "t.recipe.yaml"));

        Assert.Equal(File.ReadAllBytes(Path.Combine(game, "Tex", "b.bntx")), File.ReadAllBytes(Path.Combine(mod, "romfs", "Tex", "c.bntx")));
        // d was copied before a was tinted, so it holds the game's a.
        Assert.Equal([200, 100, 50, 255], FirstPixel(Path.Combine(mod, "romfs", "Tex", "d.bntx")));
        Assert.Equal([0, 0, 200, 255], FirstPixel(Path.Combine(mod, "romfs", "Tex", "a.bntx")));
    }

    [Fact]
    public void ACopyStepHasToComeFirstAndDeleteStandsAlone()
    {
        using Workspace w = new();
        var (env, mod, _) = Setup(w, ("bad.recipe.yaml", """
            Tex/c.bntx:
              - tint "#FF0000"
              - copy Tex/b.bntx
            """), ("bad2.recipe.yaml", """
            Tex/c.bntx:
              - delete
              - copy Tex/b.bntx
            """));

        Assert.Contains("come first", Assert.Throws<InvalidDataException>(() => Run(w, env, "apply-recipe", Path.Combine(mod, "bad.recipe.yaml"))).Message);
        Assert.Contains("cannot be combined", Assert.Throws<InvalidDataException>(() => Run(w, env, "apply-recipe", Path.Combine(mod, "bad2.recipe.yaml"))).Message);
    }

    [Fact]
    public void ADeleteStepRemovesAFileFromTheOutputAndWarnsWhenThereIsNone()
    {
        using Workspace w = new();
        var (env, mod, _) = Setup(w, ("u.recipe.yaml", """
            Tex/c.bntx: copy Tex/b.bntx
            Tex/x.bntx: delete
            """));

        JsonObject result = Run(w, env, "apply-recipe", Path.Combine(mod, "u.recipe.yaml"));

        Assert.False(File.Exists(Path.Combine(mod, "romfs", "Tex", "x.bntx")));
        Assert.Contains("no such file", result["warnings"]![0]!.GetValue<string>());
    }

    [Fact]
    public void AnIncludedRecipeRunsFirstAndTheOuterRecipesVariableWins()
    {
        using Workspace w = new();
        var (env, mod, _) = Setup(w, ("shared.recipe.yaml", """
            color: "#FF0000"
            size: "1"

            Model/thing.bfres:
              Models/m/Materials/*.fmat: tint $color
            """), ("main.recipe.yaml", """
            include: shared.recipe.yaml
            color: "#0000FF"

            Tex/a.bntx:
              '*': tint $color
            """));

        JsonObject info = Run(w, env, "recipe-info", Path.Combine(mod, "main.recipe.yaml"));
        Run(w, env, "apply-recipe", Path.Combine(mod, "main.recipe.yaml"));

        Assert.Equal("#0000FF", info["variables"]!["color"]!.GetValue<string>());
        Assert.Equal("1", info["variables"]!["size"]!.GetValue<string>());
        Assert.Equal([0f, 0f, 0.5f, 1f], Colour(Path.Combine(mod, "romfs", "Model", "thing.bfres")));
        Assert.Equal([0, 0, 200, 255], FirstPixel(Path.Combine(mod, "romfs", "Tex", "a.bntx")));
    }

    [Fact]
    public void RecipesThatIncludeEachOtherAreRefused()
    {
        using Workspace w = new();
        var (env, mod, _) = Setup(w, ("a.recipe.yaml", "include: b.recipe.yaml\nTex/a.bntx: copy Tex/b.bntx\n"), ("b.recipe.yaml", "include: a.recipe.yaml\nTex/a.bntx: copy Tex/b.bntx\n"));

        Assert.Contains("includes itself", Assert.Throws<InvalidDataException>(() => Run(w, env, "recipe-info", Path.Combine(mod, "a.recipe.yaml"))).Message);
    }

    [Fact]
    public void APreviewSaysWhatWouldChangeAndKeepsNothing()
    {
        using Workspace w = new();
        var (env, mod, _) = Setup(w, ("t.recipe.yaml", """
            color: "#FF0000"

            Model/thing.bfres:
              Models/m/Materials/*.fmat: tint $color
              Models/m/Shapes/Nope*.fshp: tint $color
            Tex/a.bntx:
              '*': tint $color
            Tex/missing.bntx:
              '*': tint $color
            """));

        JsonObject result = Run(w, env, "preview-recipe", Path.Combine(mod, "t.recipe.yaml"));

        Assert.False(Directory.Exists(Path.Combine(mod, "romfs")));
        Assert.False(Directory.Exists(result["output"]!.GetValue<string>()));
        Assert.True(result["preview"]!.GetValue<bool>());
        JsonArray line = result["details"]!["Model/thing.bfres"]![0]!["lines"]!.AsArray();
        Assert.Contains("0.5, 0.25, 0.125", line[0]!["from"]!.GetValue<string>());
        Assert.Contains("0.5, 0, 0", line[0]!["to"]!.GetValue<string>());
        Assert.Contains("average colour", result["details"]!["Tex/a.bntx"]![0]!["lines"]![0]!["from"]!.GetValue<string>());
        string warnings = string.Join("|", result["warnings"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("nothing inside it matched Models/m/Shapes/Nope*.fshp", warnings);
        Assert.Contains("Tex/missing.bntx: is not in the RomFS", warnings);
    }

    [Theory]
    [InlineData("../outside.bntx")]
    [InlineData("Tex/../../outside.bntx")]
    [InlineData("C:/outside.bntx")]
    public void APathThatLeavesTheRomFsIsRefusedAndNothingIsWrittenOutsideTheOutputFolder(string target)
    {
        using Workspace w = new();
        var (env, mod, game) = Setup(w, ("t.recipe.yaml", $"\"{target}\": copy Tex/b.bntx\n"), ("u.recipe.yaml", $"Tex/c.bntx: copy \"{target}\"\n"));

        Assert.Contains("not a path inside", Assert.Throws<InvalidDataException>(() => Run(w, env, "apply-recipe", Path.Combine(mod, "t.recipe.yaml"))).Message);
        Assert.Contains("not a path inside", Assert.Throws<InvalidDataException>(() => Run(w, env, "apply-recipe", Path.Combine(mod, "u.recipe.yaml"))).Message);
        Assert.False(File.Exists(Path.Combine(w.Path("mod"), "outside.bntx")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(game)!, "outside.bntx")));
    }

    [Fact]
    public void ARecipeNeverWritesIntoTheGameFolder()
    {
        using Workspace w = new();
        var (_, _, game) = Setup(w);
        // A recipe sitting where its romfs output would be the game's own folder.
        string recipe = Path.Combine(Path.GetDirectoryName(game)!, "x.recipe.yaml");
        string gameNamedRomfs = Path.Combine(Path.GetDirectoryName(game)!, "romfs");
        Directory.CreateDirectory(Path.Combine(gameNamedRomfs, "Tex"));
        File.WriteAllBytes(Path.Combine(gameNamedRomfs, "Tex", "a.bntx"), Bntx(200, 100, 50));
        File.WriteAllText(recipe, "Tex/a.bntx:\n  '*': tint \"#00FF00\"\n");
        Env same = Env.From(name => name switch
        {
            "TKVSC_COMPRESSION_BACKEND" => "plain-zstd-yaz0",
            "TKVSC_HANDLER_MANIFEST" => w.Manifest(),
            "TKVSC_ROMFS" => gameNamedRomfs,
            _ => null,
        });
        byte[] before = File.ReadAllBytes(Path.Combine(gameNamedRomfs, "Tex", "a.bntx"));

        Assert.Contains("never write to the game", Assert.Throws<InvalidOperationException>(() => Run(w, same, "apply-recipe", recipe)).Message);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(gameNamedRomfs, "Tex", "a.bntx")));
    }
}

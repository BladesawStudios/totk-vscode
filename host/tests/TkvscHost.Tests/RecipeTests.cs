using System.Text.Json.Nodes;
using BfresSharp;
using BfresSharp.Container;
using BfresSharp.Model;
using BfresSharp.Model.Geometry;
using BfresSharp.Model.Material;

namespace TkvscHost.Tests;

public class RecipeTests
{
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

    // A game folder with one model, and a mod folder with a recipe in it.
    private static (Env Env, string RecipePath, string Output) Setup(Workspace w, string recipe)
    {
        string romfs = w.Path("romfs-game");
        Directory.CreateDirectory(Path.Combine(romfs, "Model"));
        File.WriteAllBytes(Path.Combine(romfs, "Model", "thing.bfres"), ModelBfres());
        string mod = w.Path("mod");
        Directory.CreateDirectory(mod);
        string recipePath = Path.Combine(mod, "thing.recipe.yaml");
        File.WriteAllText(recipePath, recipe);
        Env env = Env.From(name => name switch
        {
            "TKVSC_COMPRESSION_BACKEND" => "plain-zstd-yaz0",
            "TKVSC_HANDLER_MANIFEST" => w.Manifest(),
            "TKVSC_ROMFS" => romfs,
            _ => null,
        });
        return (env, recipePath, Path.Combine(mod, "romfs", "Model", "thing.bfres"));
    }

    private const string TintRecipe = """
        color: "#FF0000"

        Model/thing.bfres:
          Models/m/Materials/*.fmat: tint $color
        """;

    private static JsonObject Run(Workspace w, Env env, string command, params string[] args)
        => (JsonObject)Commands.Find(command)!(w.Context(command, env, "", args));

    private static float[] Colour(string path)
    {
        Material material = BfresFile.Read(File.ReadAllBytes(path)).Models["m"].Materials["Mt_Body"];
        return ShaderParamValues.ReadFloats(material, material.ShaderParams["color"], false);
    }

    [Fact]
    public void ARecipeTintsTheMaterialsKeepingTheirBrightnessAndLeavesTheGameFileAlone()
    {
        using Workspace w = new();
        var (env, recipe, output) = Setup(w, TintRecipe);
        byte[] original = File.ReadAllBytes(Path.Combine(w.Path("romfs-game"), "Model", "thing.bfres"));

        JsonObject result = Run(w, env, "apply-recipe", recipe);

        Assert.Equal(1, result["files"]![0]!["changed"]!.GetValue<int>());
        Assert.Equal([0.5f, 0f, 0f, 1f], Colour(output));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(w.Path("romfs-game"), "Model", "thing.bfres")));
    }

    [Fact]
    public void TintCanMakeTheColourBrighterOrDimmerWithX()
    {
        using Workspace w = new();
        var (env, recipe, output) = Setup(w, TintRecipe.Replace("tint $color", "tint $color x 4"));

        Run(w, env, "apply-recipe", recipe);

        Assert.Equal([2f, 0f, 0f, 1f], Colour(output));
    }

    [Fact]
    public void AVariableCanBeGivenAnotherValueAndEachRunStartsFromTheGameFile()
    {
        using Workspace w = new();
        var (env, recipe, output) = Setup(w, TintRecipe);

        Run(w, env, "apply-recipe", recipe);
        Run(w, env, "apply-recipe", recipe, "color=#0000FF");

        Assert.Equal([0f, 0f, 0.5f, 1f], Colour(output));
        Assert.Equal("#FF0000", Run(w, env, "recipe-info", recipe)["variables"]!["color"]!.GetValue<string>());
    }

    [Fact]
    public void AnUnknownVariableOrOperationIsRefusedBeforeAnythingIsWritten()
    {
        using Workspace w = new();
        var (env, recipe, output) = Setup(w, TintRecipe.Replace("tint $color", "tint $colour"));
        Assert.Contains("no such variable", Assert.Throws<ArgumentException>(() => Run(w, env, "apply-recipe", recipe)).Message);
        Assert.False(File.Exists(output));

        File.WriteAllText(recipe, TintRecipe.Replace("tint $color", "paint $color"));
        Assert.Contains("Unknown step", Assert.Throws<InvalidDataException>(() => Run(w, env, "apply-recipe", recipe)).Message);

        File.WriteAllText(recipe, TintRecipe);
        Assert.Contains("no variable", Assert.Throws<ArgumentException>(() => Run(w, env, "apply-recipe", recipe, "nope=1")).Message);
    }

    [Fact]
    public void AReplaceStepChangesTheTextOfTheEntry()
    {
        using Workspace w = new();
        var (env, recipe, output) = Setup(w, """
            color: "1, 1, 0, 1"

            Model/thing.bfres:
              Models/m/Materials/*.fmat:
                - replace \[0.5, 0.25, 0.125, 1\] -> [$color]
            """);

        Run(w, env, "apply-recipe", recipe);

        Assert.Equal([1f, 1f, 0f, 1f], Colour(output));
    }
}

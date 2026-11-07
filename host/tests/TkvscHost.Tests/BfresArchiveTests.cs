using System.Text.Json.Nodes;
using BfresSharp;
using BfresSharp.Container;
using BfresSharp.Model;
using BfresSharp.Model.Animation;

namespace TkvscHost.Tests;

public class BfresArchiveTests
{
    private static MaterialAnim Anim(string name, float value)
    {
        var item = new MaterialAnimItem
        {
            Name = "Mt_Body",
            ParamAnimInfos = [new ParamAnimInfo { Name = "color", ConstantCount = 1, SubBindIndex = 4 }],
            Constants = [new AnimConstant(16, BitConverter.SingleToUInt32Bits(value))],
        };
        return new MaterialAnim { Name = name, FrameCount = 1, BindIndices = [0], Items = [item], TextureBinds = [] };
    }

    private static byte[] BuildBfres()
    {
        var file = new ResFile
        {
            Name = "synthetic",
            Platform = BfresPlatform.Switch,
            Version = new BfresVersion(BfresPlatform.Switch, 0x00090000),
            DataAlignment = 0x2000,
            Alignment = 0x0C,
            TargetAddressSize = 0x40,
        };
        file.MaterialAnims.Add("Idle", Anim("Idle", 1f));
        file.MaterialAnims.Add("Angry", Anim("Angry", 2f));
        file.ExternalFiles.Add("extra.bin", new ExternalFile { Data = new byte[] { 1, 2, 3, 4 } });
        return BfresFile.Write(file);
    }

    private static string Save(Workspace w, string name = "thing.bfres")
    {
        string path = w.Path(name);
        File.WriteAllBytes(path, BuildBfres());
        return path;
    }

    private static ResFile Reload(string path) => BfresFile.Read(File.ReadAllBytes(path));

    [Fact]
    public void ListShowsTheAnimationsAndEmbeddedFilesAsFolders()
    {
        using Workspace w = new();
        string path = Save(w);

        JsonArray names = (JsonArray)Commands.Find("list")!(w.Context("list", w.Env(), "", path));

        Assert.Equal(["ExternalFiles/extra.bin", "MaterialAnims/Angry.fmaa", "MaterialAnims/Idle.fmaa"], names.Select(n => n!.GetValue<string>()).Order());
    }

    [Fact]
    public void AModelIsAFolderWithAFilePerMaterialAndSummariesOfItsShapes()
    {
        using Workspace w = new();
        var material = new BfresSharp.Model.Material.Material { ShaderParamData = new byte[16] };
        material.ShaderParams.Add("color", new BfresSharp.Model.Material.ShaderParam { Name = "color", Type = BfresSharp.Model.Material.ShaderParamType.Float4, DataOffset = 0 });
        BfresSharp.Model.Material.ShaderParamValues.WriteFloats(material, material.ShaderParams["color"], [0.5f, 0.5f, 0.5f, 1f], false);
        var model = new ResModel { Name = "m" };
        model.Materials.Add("Mt_Body", material);
        model.VertexBuffers.Add(new BfresSharp.Model.Geometry.VertexBuffer());
        model.Shapes.Add("Body", new BfresSharp.Model.Geometry.Shape { Name = "Body" });
        var file = new ResFile
        {
            Name = "synthetic", Platform = BfresPlatform.Switch, Version = new BfresVersion(BfresPlatform.Switch, 0x00090000),
            DataAlignment = 0x2000, Alignment = 0x0C, TargetAddressSize = 0x40,
        };
        file.Models.Add("m", model);
        string path = w.Path("thing.bfres");
        File.WriteAllBytes(path, BfresFile.Write(file));

        JsonArray names = (JsonArray)Commands.Find("list")!(w.Context("list", w.Env(), "", path));
        Assert.Contains("Models/m/Materials/Mt_Body.fmat", names.Select(n => n!.GetValue<string>()));
        Assert.Contains("Models/m/Shapes/Body.fshp", names.Select(n => n!.GetValue<string>()));

        string yaml = w.Run("read", [path, "Models/m/Materials/Mt_Body.fmat"])["content"]!.GetValue<string>();
        Assert.Contains("name: color", yaml);
        w.Run("write", [path, "Models/m/Materials/Mt_Body.fmat"], yaml.Replace("[0.5, 0.5, 0.5, 1]", "[1, 0, 0, 1]"));
        BfresSharp.Model.Material.Material saved = Reload(path).Models["m"].Materials["Mt_Body"];
        Assert.Equal([1f, 0f, 0f, 1f], BfresSharp.Model.Material.ShaderParamValues.ReadFloats(saved, saved.ShaderParams["color"], false));
    }

    [Fact]
    public void AGameThatListsOtherArchivePatternsStillOpensABfres()
    {
        Env env = Env.From(name => name == "TKVSC_ARCHIVE_PATTERNS" ? "[\"\\.(pack|sarc)$\"]" : null);

        Assert.True(env.IsArchiveName("Npc_Navi.anim.bfres.zs"));
        Assert.True(env.IsArchiveName("Npc_Navi.anim.bfres"));
        Assert.True(env.IsArchiveName("a.pack"));
        Assert.False(env.IsArchiveName("a.byml"));
        Assert.True(env.IsArchiveName("static.Nin_Ounce_NVN2.vsetb.byml.zs"));
        Assert.False(env.IsArchiveName("static.Nin_Ounce_NVN2.esetb.byml.zs"));
    }

    [Fact]
    public void ReadGivesAMaterialAnimationAsYamlAndAnEmbeddedFileAsItsBytes()
    {
        using Workspace w = new();
        string path = Save(w);

        string yaml = w.Run("read", [path, "MaterialAnims/Idle.fmaa"])["content"]!.GetValue<string>();

        Assert.Contains("name: Idle\n", yaml);
        Assert.Contains("value: 1, offset: 16", yaml.Replace("{offset: 16, value: 1}", "value: 1, offset: 16"));
        Assert.Equal([1, 2, 3, 4], new Archives(new Containers(w.Env(), null)).ReadArchiveFileBytes(path, "ExternalFiles/extra.bin"));
    }

    [Fact]
    public void SavingAnEditedAnimationChangesOnlyThatAnimation()
    {
        using Workspace w = new();
        string path = Save(w);
        string yaml = w.Run("read", [path, "MaterialAnims/Idle.fmaa"])["content"]!.GetValue<string>();

        w.Run("write", [path, "MaterialAnims/Idle.fmaa"], yaml.Replace("value: 1}", "value: 0.25}"));

        ResFile file = Reload(path);
        Assert.Equal(0.25f, file.MaterialAnims["Idle"].Items[0].Constants[0].AsSingle);
        Assert.Equal(2f, file.MaterialAnims["Angry"].Items[0].Constants[0].AsSingle);
        Assert.Equal([1, 2, 3, 4], file.ExternalFiles["extra.bin"].Data.ToArray());
    }

    [Fact]
    public void SavingTheTextUnchangedLeavesTheFileTheSame()
    {
        using Workspace w = new();
        string path = Save(w);
        byte[] before = File.ReadAllBytes(path);
        string yaml = w.Run("read", [path, "MaterialAnims/Idle.fmaa"])["content"]!.GetValue<string>();

        w.Run("write", [path, "MaterialAnims/Idle.fmaa"], yaml);

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void BrokenYamlIsRefusedAndTheFileIsKept()
    {
        using Workspace w = new();
        string path = Save(w);
        byte[] before = File.ReadAllBytes(path);

        Assert.ThrowsAny<Exception>(() => w.Run("write", [path, "MaterialAnims/Idle.fmaa"], "Name: Idle\nFrameCuont: 3\n"));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void DeletingAnAnimationRemovesItFromTheFile()
    {
        using Workspace w = new();
        string path = Save(w);

        w.Run("delete-entry", [path, "MaterialAnims/Angry.fmaa"]);

        Assert.Equal(["Idle"], Reload(path).MaterialAnims.Keys);
    }

    [Fact]
    public void AnEntryOfNoKnownKindCannotBeAdded()
    {
        using Workspace w = new();
        string path = Save(w);
        byte[] before = File.ReadAllBytes(path);

        var error = Assert.ThrowsAny<Exception>(() => w.Run("write-raw", [path, "Notes/readme.txt"], Convert.ToBase64String([1, 2])));

        Assert.Contains("cannot be added", error.Message);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ABfresInsideAPackOpensAndSaves()
    {
        using Workspace w = new();
        string pack = w.Path("outer.pack");
        File.WriteAllBytes(pack, Workspace.Sarc(("Model/thing.bfres", BuildBfres())).Write());
        string yaml = w.Run("read", [pack, "Model/thing.bfres/MaterialAnims/Idle.fmaa"])["content"]!.GetValue<string>();

        w.Run("write", [pack, "Model/thing.bfres/MaterialAnims/Idle.fmaa"], yaml.Replace("value: 1}", "value: 3}"));

        byte[] bfres = SarcSharp.SarcFile.FromBinary(File.ReadAllBytes(pack))["Model/thing.bfres"]!.Data;
        Assert.Equal(3f, BfresFile.Read(bfres).MaterialAnims["Idle"].Items[0].Constants[0].AsSingle);
    }
}

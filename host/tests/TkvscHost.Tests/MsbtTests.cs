using MsbtSharp;

namespace TkvscHost.Tests;

public class MsbtTests
{
    [Fact]
    public void AnEmptyMsbtStartsANewOne()
    {
        using Workspace w = new();
        string file = w.Path("New.msbt");
        File.WriteAllBytes(file, []);

        string template = w.Run("read-disk", [file])["content"]!.GetValue<string>();
        Assert.StartsWith("# New MSBT file", template);

        w.Run("write-disk", [file], "Armor_1150_Upper_Name: Hylian Tunic\nArmor_1150_Upper_Desc: Warm.\\nVery warm.\n");

        MsbtFile back = MsbtFile.FromBinary(File.ReadAllBytes(file));
        Assert.False(back.BigEndian);
        Assert.Equal(MsbtEncoding.Utf16, back.Encoding);
        Assert.Equal(["Armor_1150_Upper_Desc", "Armor_1150_Upper_Name"], back.Messages.Select(m => m.Label).Order());
        Assert.Equal("Hylian Tunic", back.Messages.First(m => m.Label == "Armor_1150_Upper_Name").PlainText);

        string text = w.Run("read-disk", [file])["content"]!.GetValue<string>();
        Assert.Contains("Armor_1150_Upper_Desc: Warm.\\nVery warm.", text);
    }

    [Fact]
    public void ALabelThatIsNotInTheFileIsAdded()
    {
        using Workspace w = new();
        string file = w.Path("Armor.msbt");
        File.WriteAllBytes(file, [1]);
        File.Delete(file);

        string empty = w.Path("Seed.msbt");
        File.WriteAllBytes(empty, []);
        w.Run("write-disk", [empty], "Armor_1150_Lower_Name: Trousers\n");
        File.Copy(empty, file);

        string edited = w.Run("read-disk", [file])["content"]!.GetValue<string>() + "Armor_1150_Upper_Name: Tunic\n";
        w.Run("write-disk", [file], edited);

        MsbtFile back = MsbtFile.FromBinary(File.ReadAllBytes(file));
        Assert.Equal("Trousers", back.Messages.First(m => m.Label == "Armor_1150_Lower_Name").PlainText);
        Assert.Equal("Tunic", back.Messages.First(m => m.Label == "Armor_1150_Upper_Name").PlainText);
    }

    [Fact]
    public void ANewMsbtWithNothingInItStillWrites()
    {
        using Workspace w = new();
        string file = w.Path("Blank.msbt");
        File.WriteAllBytes(file, []);

        w.Run("write-disk", [file], "# nothing yet\n");

        Assert.True(MsbtFile.IsMsbt(File.ReadAllBytes(file)));
    }
}

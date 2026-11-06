namespace SamplePlugin.Helper;

public static class Greeting
{
    public const string Name = "private dependency";

    public static string Text => $"{Name} loaded";
}

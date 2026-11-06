using System.Text.Json.Nodes;
using HexpatSharp;

namespace TkvscHost;

/// <summary>The hex editor's pattern view: runs a hexpat (ImHex pattern language) over a file and gives back the tree of what it found.</summary>
public static class HexpatCommands
{
    public static void Register(Dictionary<string, CommandHandler> commands)
        => commands["evaluate-hexpat"] = EvaluateHexpat;

    // evaluate-hexpat <file>, with the pattern on standard input.
    private static JsonNode EvaluateHexpat(CommandContext c)
    {
        byte[] data = File.ReadAllBytes(c.Arg(0));
        string pattern = c.StdinText;

        EvaluationResult result = Hexpat.Evaluate(pattern, data);

        // A pattern that fails before it places anything is an error. One that fails part way shows what it placed, and says why it stopped.
        if (result.Error is not null && result.Patterns.Count == 0)
            return new JsonObject { ["error"] = "Hexpat error: " + result.Error };

        return JsonNode.Parse(result.ToJson())!;
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TkvscHost;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: tkvsc-host <command> [args...]");
            return 2;
        }

        string command = args[0];
        Env env = Env.FromEnvironment();
        byte[]? stdin = null;
        byte[] ReadStdin()
        {
            if (stdin is not null) return stdin;
            using MemoryStream copy = new();
            using Stream input = Console.OpenStandardInput();
            input.CopyTo(copy);
            return stdin = copy.ToArray();
        }

        CommandContext context = new(command, args[1..], env, ReadStdin);

        try
        {
            if (Commands.Find(command) is not { } handler) throw new ArgumentException($"Unknown command '{command}'");

            JsonNode result = handler(context);
            WriteJson(result);
            return 0;
        }
        catch (Exception e)
        {
            WriteJson(new JsonObject { ["error"] = e.Message, ["traceback"] = e.ToString() });
            return 0;
        }
    }

    private static void WriteJson(JsonNode node)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(node.ToJsonString(JsonOptions));
        using Stream output = Console.OpenStandardOutput();
        output.Write(bytes);
        output.Write("\n"u8);
    }
}

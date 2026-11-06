using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TkvscHost.Plugins;

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

        GamePlugin plugin = GamePlugin.None;

        try
        {
            plugin = PluginLoader.Load(env);

            // Standard output carries the one JSON result; anything a plugin prints goes to standard error.
            if (plugin != GamePlugin.None) Console.SetOut(Console.Error);

            CommandContext context = new(command, args[1..], env, ReadStdin, plugin);

            // A plugin's command comes first, so it can replace a built-in one for its game.
            CommandHandler handler = plugin.FindCommand(command) ?? Commands.Find(command)
                ?? throw new ArgumentException($"Unknown command '{command}'");

            JsonNode result = handler(context);
            WriteJson(result);
            return 0;
        }
        catch (Exception e) when (plugin != GamePlugin.None && e is MissingMemberException or TypeLoadException)
        {
            WriteJson(new JsonObject { ["error"] = PluginLoader.DescribeMismatch(plugin, e), ["traceback"] = e.ToString() });
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

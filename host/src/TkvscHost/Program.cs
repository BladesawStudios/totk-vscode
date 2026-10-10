using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TkvscHost;

public static class Program
{
    private static readonly HashSet<string> StdinCommands =
    [
        "write", "write-disk", "write-raw", "evaluate-hexpat", "replace-bars-audio", "replace-bntx-payload", "replace-txtg-payload",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: tkvsc-host <command> [args...]    (the same commands as python/totk_bridge.py)");
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
            if (Commands.Find(command) is not { } handler) throw new NotPortedException(command);

            JsonNode result = handler(context);
            WriteJson(result);
            return 0;
        }
        catch (NotPortedException not)
        {
            // Only the commands that take input read it: the extension leaves the pipe open for the others.
            return PythonBridge.Run(args, StdinCommands.Contains(command) ? () => context.StdinBytes : null, not.Message);
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

/// <summary>Runs the Python bridge for what the host has not taken over, with the same arguments and input.</summary>
public static class PythonBridge
{
    public static int Run(string[] args, Func<byte[]>? readStdin, string what)
    {
        string python = Environment.GetEnvironmentVariable("TKVSC_PYTHON") ?? "";
        string bridge = Environment.GetEnvironmentVariable("TKVSC_BRIDGE") ?? "";

        if (python.Length == 0 || bridge.Length == 0)
        {
            byte[] message = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["error"] = $"'{what}' needs the Python bridge, which is not set up yet (run \"TKVSC: Set Up Python Environment\").",
            }.ToJsonString() + "\n");
            using Stream output = Console.OpenStandardOutput();
            output.Write(message);
            return 0;
        }

        ProcessStartInfo start = new(python)
        {
            WorkingDirectory = Path.GetDirectoryName(bridge) ?? "",
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(bridge);
        foreach (string arg in args) start.ArgumentList.Add(arg);

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Python.");
        if (readStdin is not null) process.StandardInput.BaseStream.Write(readStdin());
        process.StandardInput.Close();
        process.WaitForExit();
        return process.ExitCode;
    }
}

using System.Text;
using System.Text.Json.Nodes;
using TkvscHost.Plugins;

namespace TkvscHost;

/// <summary>What a command runs with: its arguments, the settings, and the standard input it may read once.</summary>
public sealed class CommandContext
{
    private readonly Func<byte[]> _readStdin;
    private byte[]? _stdin;

    public CommandContext(string command, string[] args, Env env, Func<byte[]> readStdin, GamePlugin? plugin = null)
    {
        Command = command;
        Args = args;
        Env = env;
        Plugin = plugin ?? GamePlugin.None;
        Containers = new Containers(env, Plugin);
        _readStdin = readStdin;
    }

    /// <summary>What the active game's plugin registered, if it has one.</summary>
    public GamePlugin Plugin { get; }

    public string Command { get; }

    /// <summary>The arguments after the command name.</summary>
    public string[] Args { get; }

    public Env Env { get; }

    public Containers Containers { get; }

    public string Romfs => Env.Romfs;

    public byte[] StdinBytes => _stdin ??= _readStdin();

    public string StdinText => new UTF8Encoding(false).GetString(StdinBytes);

    public string Arg(int index) => index < Args.Length
        ? Args[index]
        : throw new ArgumentException($"{Command}: missing argument {index + 1}");

    public string ArgOr(int index, string fallback) => index < Args.Length ? Args[index] : fallback;
}

public delegate JsonNode CommandHandler(CommandContext context);

/// <summary>The commands the host runs itself.</summary>
public static class Commands
{
    private static readonly Dictionary<string, CommandHandler> Handlers = new(StringComparer.Ordinal);

    static Commands()
    {
        Cmd.Register(Handlers);
    }

    public static CommandHandler? Find(string name) => Handlers.GetValueOrDefault(name);

    public static IEnumerable<string> Names => Handlers.Keys;
}

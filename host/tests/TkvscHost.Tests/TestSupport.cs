using SarcSharp;
using System.Text.Json.Nodes;

namespace TkvscHost.Tests;

/// <summary>A folder for a test to work in, removed afterwards.</summary>
public sealed class Workspace : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("tkvsc-host-").FullName;

    public string Path(string name) => System.IO.Path.Combine(Root, name);

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }

    /// <summary>Settings for tests: no game dictionaries, so .zs files use plain zstd.</summary>
    public Env Env(string? manifest = null) => TkvscHost.Env.From(name => name switch
    {
        "TKVSC_COMPRESSION_BACKEND" => "plain-zstd-yaz0",
        "TKVSC_HANDLER_MANIFEST" => manifest,
        _ => null,
    });

    public string Manifest()
    {
        string path = Path("manifest.json");
        File.WriteAllText(path, """
            {
              "version": 1,
              "extensionToHandler": { "byml": "byml", "bgyml": "byml", "msbt": "msbt", "belnk": "xlnk", "bslnk": "xlnk" },
              "aampExtensions": ["bphysics"],
              "handlers": {}
            }
            """);
        return path;
    }

    public CommandContext Context(string command, Env env, string stdin = "", params string[] args)
        => new(command, args, env, () => System.Text.Encoding.UTF8.GetBytes(stdin));

    public JsonObject Run(string command, string[] args, string stdin = "", string? manifest = null)
    {
        Env env = Env(manifest ?? Manifest());
        CommandHandler handler = Commands.Find(command) ?? throw new InvalidOperationException($"{command} is not a native command");
        return (JsonObject)handler(Context(command, env, stdin, args));
    }

    public static SarcFile Sarc(params (string Name, byte[] Data)[] files)
    {
        SarcFile sarc = new();
        foreach (var (name, data) in files) sarc.Entries.Add(new SarcEntry(name, data));
        return sarc;
    }

    public static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);
}

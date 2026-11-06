using System.Text;
using System.Text.Json.Nodes;
using SamplePlugin.Helper;
using Tkvsc.Plugins;

[assembly: TkvscPlugin(typeof(SamplePlugin.SampleGamePlugin))]

namespace SamplePlugin;

/// <summary>Shows each thing a plugin can provide: a format, a compression and a command.</summary>
public sealed class SampleGamePlugin : IGamePlugin
{
    public void Configure(IPluginBuilder builder)
    {
        builder.AddFormat("sample-text", new SampleTextFormat());
        builder.UseCompression(new SampleCompression());
        builder.AddArchiveFormat(new SampleArchive());

        builder.AddCommand("sample-info", command => new JsonObject
        {
            ["gameId"] = builder.Host.GameId,
            ["pluginDirectory"] = builder.Host.PluginDirectory,
            ["helper"] = Greeting.Text,
            ["args"] = new JsonArray([.. command.Args.Select(a => (JsonNode)a)]),
            ["stdin"] = command.StdinText,
        });

        // Output a plugin prints goes to standard error, so this doesn't disturb the JSON result.
        builder.AddCommand("sample-print", _ =>
        {
            Console.WriteLine("printed by the plugin");
            return new JsonObject { ["success"] = true };
        });

        // A command can replace a built-in one for this game.
        builder.AddCommand("list-bars", _ => new JsonObject { ["handledBy"] = "sample plugin" });
    }
}

/// <summary>A made-up format: "SMPL" followed by UTF-8 text.</summary>
public sealed class SampleTextFormat : IFormatHandler
{
    private static ReadOnlySpan<byte> Magic => "SMPL"u8;

    public string Read(byte[] data, string logicalPath) => Encoding.UTF8.GetString(data.AsSpan(Magic.Length));

    public byte[] Write(byte[] original, string text, string logicalPath) => [.. Magic, .. Encoding.UTF8.GetBytes(text)];

    public bool Matches(ReadOnlySpan<byte> data) => data.StartsWith(Magic);
}

/// <summary>A made-up archive: "SPAK", the number of files, then each file's name and bytes.</summary>
public sealed class SampleArchive : IArchiveFormat
{
    private static ReadOnlySpan<byte> Magic => "SPAK"u8;

    public bool Matches(ReadOnlySpan<byte> data) => data.StartsWith(Magic);

    public IReadOnlyList<ArchiveFile> Read(byte[] data, string logicalPath)
    {
        using BinaryReader reader = new(new MemoryStream(data, Magic.Length, data.Length - Magic.Length));
        ArchiveFile[] files = new ArchiveFile[reader.ReadInt32()];
        for (int i = 0; i < files.Length; i++)
        {
            string name = reader.ReadString();
            files[i] = new ArchiveFile(name, reader.ReadBytes(reader.ReadInt32()));
        }

        return files;
    }

    public byte[] Write(IReadOnlyList<ArchiveFile> files, byte[] original, string logicalPath)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(Magic);
        writer.Write(files.Count);
        foreach (ArchiveFile file in files)
        {
            writer.Write(file.Name);
            writer.Write(file.Data.Length);
            writer.Write(file.Data);
        }

        writer.Flush();
        return stream.ToArray();
    }
}

/// <summary>A made-up compression: "SMZ1" followed by the bytes inverted. It only shows the shape; a real one would compress.</summary>
public sealed class SampleCompression : ICompressionBackend
{
    private static ReadOnlySpan<byte> Magic => "SMZ1"u8;

    public bool IsCompressed(ReadOnlySpan<byte> data) => data.StartsWith(Magic);

    public byte[] Decompress(byte[] data, string logicalPath) => [.. data.AsSpan(Magic.Length).ToArray().Select(b => (byte)~b)];

    public byte[] Compress(byte[] data, string logicalPath) => [.. Magic, .. data.Select(b => (byte)~b)];
}

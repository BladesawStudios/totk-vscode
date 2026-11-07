using System.Text;
using BymlSharp;
using Tkvsc.Plugins;

namespace TkvscHost.Formats;

/// <summary>A <c>.vsetb.byml</c> (the particle data of a game's effects) as an archive: a folder per effect set, a file per emitter.</summary>
/// <remarks>
/// A file holds hundreds of effect sets (the shared <c>static</c> one holds most of the game's), each a few emitters, and an
/// emitter's colours are what a modder edits. Each emitter is a YAML file of its colour channels, the same text
/// <see cref="Vfx3Text"/> writes; saving one writes only that emitter's values back into the particle binary. Sets and
/// emitters cannot be added, renamed or deleted. A file whose emitters were not changed is returned byte for byte.
/// </remarks>
public sealed class VsetbArchiveFormat : IArchiveFormat
{
    private const string Folder = "Esets", Extension = ".emitter";

    public bool Matches(ReadOnlySpan<byte> data)
        => (data.StartsWith("BY"u8) || data.StartsWith("YB"u8)) && data.IndexOf("BFFH"u8) >= 0 && data.IndexOf("PtclBin"u8) >= 0;

    private sealed record Entry(string Name, byte[] Data, Action<string> Replace);

    private static byte[]? ParticleBinary(BymlFile file)
        => file.Root.IsMap && file.Root["PtclBin"] is { Type: BymlType.Binary or BymlType.BinaryAligned } ptcl && Vfx3Text.IsVfx3(ptcl.Binary) ? ptcl.Binary : null;

    public IReadOnlyList<ArchiveFile> Read(byte[] data, string logicalPath)
    {
        BymlFile file = BymlFile.FromBinary(data);
        byte[] binary = ParticleBinary(file) ?? throw new InvalidDataException("The file has no Vfx3 particle data.");
        return [.. Entries(binary, edits: null).Select(entry => new ArchiveFile(entry.Name, entry.Data))];
    }

    public byte[] Write(IReadOnlyList<ArchiveFile> files, byte[] original, string logicalPath)
    {
        BymlFile file = BymlFile.FromBinary(original);
        byte[] binary = ParticleBinary(file) ?? throw new InvalidDataException("The file has no Vfx3 particle data.");

        Dictionary<string, byte[]> given = files.ToDictionary(f => f.Name, f => f.Data, StringComparer.Ordinal);
        List<(int Eset, string Emitter, string Text)> edits = [];
        Dictionary<string, Entry> before = Entries(binary, edits).ToDictionary(entry => entry.Name, StringComparer.Ordinal);

        foreach (Entry entry in before.Values)
        {
            if (!given.TryGetValue(entry.Name, out byte[]? now)) throw new InvalidOperationException($"{entry.Name} cannot be removed: effect sets and emitters in a particle file cannot be deleted here.");
            if (!now.AsSpan().SequenceEqual(entry.Data)) entry.Replace(Encoding.UTF8.GetString(now));
        }

        string? added = given.Keys.FirstOrDefault(name => !before.ContainsKey(name));
        if (added is not null) throw new InvalidOperationException($"{added} cannot be added: effect sets and emitters in a particle file cannot be created here.");
        if (edits.Count == 0) return original;

        byte[] edited = binary;
        foreach (var (eset, _, text) in edits) edited = Vfx3Text.ApplyText(edited, text, eset);

        BymlType type = file.Root["PtclBin"].Type;
        file.Root.AsMap["PtclBin"] = type == BymlType.BinaryAligned ? Byml.From(edited, file.Root["PtclBin"].BinaryAlignment) : Byml.From(edited);
        return file.Write();
    }

    private static IEnumerable<Entry> Entries(byte[] binary, List<(int Eset, string Emitter, string Text)>? edits)
    {
        var sets = Vfx3Text.Esets(binary);
        for (int index = 0; index < sets.Count; index++)
        {
            int set = index;
            foreach (string emitter in sets[set].Emitters)
            {
                string text = Vfx3Text.ToText(binary, set, emitter);
                string header = $"# Colours of the emitter \"{emitter}\" in the effect set \"{sets[set].Name}\". Edit the numbers; each renderer lists its colour channels.\n";
                yield return new Entry($"{Folder}/{sets[set].Name}/{emitter}{Extension}", Encoding.UTF8.GetBytes(header + text), changed => edits?.Add((set, emitter, changed)));
            }
        }
    }
}

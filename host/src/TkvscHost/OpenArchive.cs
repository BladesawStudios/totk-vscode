using SarcSharp;
using Tkvsc.Plugins;

namespace TkvscHost;

/// <summary>An archive's files in memory, whichever format they came from, and the way to write them back.</summary>
public sealed class OpenArchive
{
    private readonly Func<string, SarcEntry?> _find;
    private readonly Func<byte[]> _write;

    private OpenArchive(List<SarcEntry> entries, Func<string, SarcEntry?> find, Func<byte[]> write)
    {
        Entries = entries;
        _find = find;
        _write = write;
    }

    public List<SarcEntry> Entries { get; }

    public SarcEntry? this[string name] => _find(name);

    public byte[] Write() => _write();

    public static OpenArchive FromSarc(SarcFile sarc) => new(sarc.Entries, name => sarc[name], sarc.Write);

    public static OpenArchive FromPlugin(IArchiveFormat format, byte[] plain, string logicalPath)
    {
        List<SarcEntry> entries = [.. format.Read(plain, logicalPath).Select(file => new SarcEntry(file.Name, file.Data))];
        return new(
            entries,
            name => entries.Find(entry => entry.Name == name),
            () => format.Write([.. entries.Select(entry => new ArchiveFile(entry.Name, entry.Data))], plain, logicalPath));
    }
}

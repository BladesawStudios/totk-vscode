using System.Text.RegularExpressions;
using SarcSharp;
using TkvscHost.Formats;
using ZsDicSharp;

namespace TkvscHost;

/// <summary>
/// Nested .pack / .sarc / .genvb / .blarc paths resolved to an open SARC and the path inside it
/// (the equivalent of <c>archive_resolve.py</c>). A path like <c>Pack/A.pack.zs/Actor/B.pack.zs/Thing.bgyml</c>
/// opens each archive in turn.
/// </summary>
public sealed partial class Archives(Containers containers)
{
    [GeneratedRegex(@"\.(pack|sarc|genvb|blarc|bfarc|bkres|bntx)(\.zs)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchiveSegment();

    [GeneratedRegex(@"\.bntx(\.zs)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BntxSegment();

    public static bool IsArchiveName(string name) => ArchiveSegment().IsMatch(name.Replace('\\', '/'));

    public static bool IsBntxName(string name) => BntxSegment().IsMatch(name.Replace('\\', '/'));

    /// <summary>
    /// Splits a path on disk that goes into archives (<c>A.pack.zs/Actor/B.pack.zs/Thing.bgyml</c>) into the archive file and
    /// the path inside it; null if the path doesn't go inside an archive. Port of <c>get_disk_archive_path</c> and friends.
    /// </summary>
    public static (string DiskArchive, string Locator)? SplitFsPath(string fsPath)
    {
        string normalized = fsPath.Replace('\\', '/').TrimEnd('/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int last = Array.FindLastIndex(segments, IsArchiveName);
        if (last < 0 || last >= segments.Length - 1) return null;

        bool windowsDrive = normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':';
        bool unc = !windowsDrive && normalized.StartsWith("//", StringComparison.Ordinal);
        bool unixAbsolute = !windowsDrive && !unc && normalized.StartsWith('/');

        string DiskPath(int end)
        {
            string prefix = string.Join('/', segments[..(end + 1)]);
            if (windowsDrive) return prefix.Replace('/', '\\');
            if (unc) return "//" + prefix;
            return unixAbsolute ? "/" + prefix : prefix;
        }

        // The innermost archive name that exists as a file is the one on disk; the rest is inside it.
        string diskArchive = DiskPath(last);
        for (int i = last; i >= 0; i--)
        {
            if (!IsArchiveName(segments[i])) continue;
            string candidate = DiskPath(i);
            if (File.Exists(candidate) || Directory.Exists(candidate)) { diskArchive = candidate; break; }
        }

        string rest = fsPath[diskArchive.Length..];
        if (rest.StartsWith('/') || rest.StartsWith('\\')) rest = rest[1..];
        rest = rest.Replace('\\', '/');
        return rest.Length == 0 || diskArchive == fsPath ? null : (diskArchive, rest);
    }

    public static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static string[] Split(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    // ---- opening ----

    public (SarcFile Sarc, bool IsCompressed) LoadSarcFile(string archivePath)
    {
        byte[] data = File.ReadAllBytes(archivePath);
        return LoadSarc(data, archivePath);
    }

    private (SarcFile, bool) LoadSarc(byte[] data, string archivePath)
    {
        bool compressed = Containers.IsZstd(data);
        if (compressed) data = containers.Decompress(data, archivePath).Data;

        if (data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual("BNTX"u8))
            throw new InvalidDataException($"Cannot open BNTX file as SARC: {archivePath}. Use the BNTX reader instead.");

        return (SarcFile.FromBinary(data), compressed);
    }

    private static byte[] GetFileBytes(SarcFile sarc, string internalPath)
    {
        SarcEntry? entry = sarc[internalPath];
        if (entry is null)
        {
            string sample = string.Join(", ", sarc.Entries.Take(5).Select(e => $"'{e.Name}'"));
            throw new FileNotFoundException($"File not found in archive: '{internalPath}'. Known paths sample: [{sample}]...");
        }

        return entry.Data;
    }

    private static void SetFile(SarcFile sarc, string internalPath, byte[] data)
    {
        int at = sarc.Entries.FindIndex(e => e.Name == internalPath);
        if (at >= 0) sarc.Entries[at] = sarc.Entries[at] with { Data = data };
        else sarc.Entries.Add(new SarcEntry(internalPath, data));
    }

    /// <summary>A .bntx the path leads into: its bytes, the texture named after it, and the path up to and including the .bntx.</summary>
    public readonly record struct BntxView(byte[] Data, string Remainder, string Prefix);

    // A texture container is either the archive on disk itself or a .bntx entry inside one.
    public BntxView? ResolveBntx(string diskArchivePath, string locatorPath)
    {
        if (IsBntxName(diskArchivePath))
        {
            byte[] raw = File.ReadAllBytes(diskArchivePath);
            if (Containers.IsZstd(raw)) raw = containers.Decompress(raw, diskArchivePath).Data;
            if (BntxNames.IsBntx(raw)) return new(raw, Normalize(locatorPath), "");
        }

        if (locatorPath.Length == 0) return null;

        string[] segments = Normalize(locatorPath).Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (!IsBntxName(segments[i])) continue;

            SarcView parent = ResolveSarcView(diskArchivePath, i > 0 ? string.Join('/', segments[..i]) : "");
            string entry = parent.PathPrefix.Length > 0 ? $"{parent.PathPrefix}/{segments[i]}".Trim('/') : segments[i];
            byte[] bytes = GetFileBytes(parent.Sarc, entry);
            if (Containers.IsZstd(bytes)) bytes = containers.Decompress(bytes, segments[i]).Data;
            if (BntxNames.IsBntx(bytes))
                return new(bytes, string.Join('/', segments[(i + 1)..]).Trim('/'), string.Join('/', segments[..(i + 1)]));
        }

        return null;
    }

    public readonly record struct SarcView(SarcFile Sarc, string PathPrefix, bool IsCompressed, string ConsumedArchivePrefix);

    public SarcView ResolveSarcView(string diskArchivePath, string locatorPath)
    {
        locatorPath = Normalize(locatorPath);
        (SarcFile sarc, bool compressed) = LoadSarcFile(diskArchivePath);

        if (locatorPath.Length == 0) return new(sarc, "", compressed, "");

        string[] segments = locatorPath.Split('/');
        int afterArchive = 0;
        List<string> consumed = [];

        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            if (!IsArchiveName(segment) || IsBntxName(segment)) continue;

            string entryPath = string.Join('/', segments[afterArchive..(index + 1)]);
            byte[] fileData = containers.Decompress(GetFileBytes(sarc, entryPath), entryPath).Data;
            sarc = SarcFile.FromBinary(fileData);
            consumed.AddRange(segments[afterArchive..(index + 1)]);
            afterArchive = index + 1;
        }

        string pathPrefix = string.Join('/', segments[afterArchive..]).Trim('/');
        return new(sarc, pathPrefix, compressed, string.Join('/', consumed).Trim('/'));
    }

    // ---- reading ----

    public List<string> ListArchiveFiles(string diskArchivePath, string locatorPath)
    {
        if (ResolveBntx(diskArchivePath, locatorPath) is { } bntx)
        {
            List<string> textures = BntxNames.List(bntx.Data);
            return bntx.Prefix.Length > 0 ? [.. textures.Select(n => $"{bntx.Prefix}/{n}")] : textures;
        }

        SarcView view = ResolveSarcView(diskArchivePath, locatorPath);
        IEnumerable<string> names = view.Sarc.Entries.Select(e => e.Name);
        if (view.PathPrefix.Length > 0)
        {
            string prefix = view.PathPrefix + "/";
            names = names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal));
        }

        return view.ConsumedArchivePrefix.Length > 0
            ? [.. names.Select(n => $"{view.ConsumedArchivePrefix}/{n}")]
            : [.. names];
    }

    public byte[] ReadArchiveFileBytes(string diskArchivePath, string filePath)
    {
        filePath = Normalize(filePath);
        if (filePath.Length == 0) throw new IOException("Is a directory: ''");

        string leaf = filePath.Split('/')[^1];
        if (IsArchiveName(leaf)) throw new IOException($"Is a directory: '{filePath}'");

        if (ResolveBntx(diskArchivePath, filePath) is { } bntx)
        {
            if (bntx.Remainder.Length == 0) throw new IOException($"Is a directory: '{filePath}'");
            return BntxNames.ReadTextureData(bntx.Data, bntx.Remainder);
        }

        SarcView view = ResolveSarcView(diskArchivePath, filePath);
        byte[] bytes = GetFileBytes(view.Sarc, view.PathPrefix);
        return BFontSharp.BFont.IsEncryptedFontPath(filePath) ? BFontSharp.BFont.ToOpenFont(bytes) : bytes;
    }

    /// <summary>The bytes stored for an entry, as they are (no decryption; nested archives allowed).</summary>
    public byte[] ReadArchiveEntryStoredBytes(string diskArchivePath, string filePath)
    {
        filePath = Normalize(filePath);
        if (filePath.Length == 0) throw new IOException("Is a directory: ''");

        string[] segments = filePath.Split('/');
        string parentLocator = string.Join('/', segments[..^1]);

        SarcView view = ResolveSarcView(diskArchivePath, parentLocator);
        string entryPath = view.PathPrefix.Length > 0 ? $"{view.PathPrefix}/{segments[^1]}" : segments[^1];
        return GetFileBytes(view.Sarc, entryPath);
    }

    // ---- changing ----

    private static void RejectBntxMutation(string diskArchivePath, string operation, string targetPath = "")
    {
        if (IsBntxName(diskArchivePath))
            throw new UnauthorizedAccessException($"Cannot {operation} inside a BNTX texture container (read-only)");

        if (targetPath.Length > 0)
        {
            string[] segments = Split(targetPath);
            if (segments.Length > 0 && segments[..^1].Any(IsBntxName))
                throw new UnauthorizedAccessException($"Cannot {operation} textures inside a nested BNTX container (read-only)");
        }
    }

    private static int NextArchiveIndex(string[] segments)
    {
        for (int i = 0; i < segments.Length - 1; i++)
            if (IsArchiveName(segments[i])) return i;
        return -1;
    }

    // Opens the nested archive the path goes through, applies the change inside it, and writes it back up the chain.
    private void MutateNested(SarcFile sarc, string[] segments, Action<SarcFile, string[]> leaf)
    {
        int index = NextArchiveIndex(segments);
        if (index < 0)
        {
            leaf(sarc, segments);
            return;
        }

        string entryPath = string.Join('/', segments[..(index + 1)]);
        string[] remainder = segments[(index + 1)..];
        Unwrapped nested = containers.Decompress(GetFileBytes(sarc, entryPath), entryPath);
        SarcFile nestedSarc = SarcFile.FromBinary(nested.Data);

        MutateNested(nestedSarc, remainder, leaf);

        byte[] packed = containers.Compress(nestedSarc.Write(), entryPath, nested.WasZstd, nested.WasYaz0);
        SetFile(sarc, entryPath, packed);
    }

    private void Save(string archivePath, SarcFile sarc, bool compressed)
    {
        byte[] bytes = sarc.Write();
        if (compressed) bytes = containers.Compress(bytes, archivePath, true, false);
        AtomicWrite(archivePath, bytes);
    }

    public static void AtomicWrite(string path, byte[] bytes)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full) ?? ".";
        string temp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    public void WriteArchiveFileBytes(string diskArchivePath, string filePath, byte[] data)
    {
        filePath = Normalize(filePath);
        RejectBntxMutation(diskArchivePath, "write", filePath);
        if (filePath.Length == 0) throw new ArgumentException("Missing file path");

        string[] segments = Split(filePath);
        if (filePath.EndsWith(".zs", StringComparison.OrdinalIgnoreCase) && !Containers.IsZstd(data))
            data = containers.Compress(data, filePath, wasZstd: true, wasYaz0: false);

        (SarcFile sarc, bool compressed) = LoadSarcFile(diskArchivePath);
        MutateNested(sarc, segments, (s, rest) => SetFile(s, string.Join('/', rest).Trim('/'), data));
        Save(diskArchivePath, sarc, compressed);
    }

    public void DeleteArchiveEntry(string diskArchivePath, string targetPath)
    {
        targetPath = Normalize(targetPath);
        RejectBntxMutation(diskArchivePath, "delete", targetPath);
        if (targetPath.Length == 0) throw new ArgumentException("Missing target path");

        (SarcFile sarc, bool compressed) = LoadSarcFile(diskArchivePath);
        MutateNested(sarc, Split(targetPath), (s, rest) =>
        {
            string target = string.Join('/', rest).Trim('/');
            string prefix = target + "/";
            int removed = s.Entries.RemoveAll(e => e.Name == target || e.Name.StartsWith(prefix, StringComparison.Ordinal));
            if (removed == 0) throw new FileNotFoundException(target);
        });
        Save(diskArchivePath, sarc, compressed);
    }

    public void RenameArchiveEntry(string diskArchivePath, string oldPath, string newPath)
    {
        oldPath = Normalize(oldPath);
        newPath = Normalize(newPath);
        RejectBntxMutation(diskArchivePath, "rename", oldPath);
        RejectBntxMutation(diskArchivePath, "rename", newPath);
        if (oldPath.Length == 0 || newPath.Length == 0) throw new ArgumentException("Missing rename path");

        string[] oldSegments = Split(oldPath);
        string[] newSegments = Split(newPath);
        (SarcFile sarc, bool compressed) = LoadSarcFile(diskArchivePath);

        RenameIn(sarc, oldSegments, newSegments);
        Save(diskArchivePath, sarc, compressed);
    }

    private void RenameIn(SarcFile sarc, string[] oldSegments, string[] newSegments)
    {
        int oldIndex = NextArchiveIndex(oldSegments);
        int newIndex = NextArchiveIndex(newSegments);

        if (oldIndex < 0 && newIndex < 0)
        {
            RenamePath(sarc, string.Join('/', oldSegments).Trim('/'), string.Join('/', newSegments).Trim('/'));
            return;
        }

        if (oldIndex != newIndex || oldIndex < 0)
            throw new ArgumentException("Cannot rename across different nested archive levels.");

        string oldEntry = string.Join('/', oldSegments[..(oldIndex + 1)]);
        string newEntry = string.Join('/', newSegments[..(newIndex + 1)]);
        if (oldEntry != newEntry) throw new ArgumentException("Cannot rename across different nested archives.");

        Unwrapped nested = containers.Decompress(GetFileBytes(sarc, oldEntry), oldEntry);
        SarcFile nestedSarc = SarcFile.FromBinary(nested.Data);
        RenameIn(nestedSarc, oldSegments[(oldIndex + 1)..], newSegments[(newIndex + 1)..]);
        SetFile(sarc, oldEntry, containers.Compress(nestedSarc.Write(), oldEntry, nested.WasZstd, nested.WasYaz0));
    }

    private static void RenamePath(SarcFile sarc, string oldPath, string newPath)
    {
        string oldPrefix = oldPath + "/";
        List<(int Index, string To)> moves = [];
        for (int i = 0; i < sarc.Entries.Count; i++)
        {
            string name = sarc.Entries[i].Name;
            if (name == oldPath) moves.Add((i, newPath));
            else if (name.StartsWith(oldPrefix, StringComparison.Ordinal)) moves.Add((i, $"{newPath}/{name[oldPrefix.Length..]}"));
        }

        if (moves.Count == 0) throw new FileNotFoundException(oldPath);

        HashSet<int> moving = [.. moves.Select(m => m.Index)];
        HashSet<string> staying = [.. sarc.Entries.Where((_, i) => !moving.Contains(i)).Select(e => e.Name)];
        foreach ((_, string to) in moves)
            if (staying.Contains(to)) throw new IOException($"File exists: '{to}'");

        foreach ((int index, string to) in moves)
            sarc.Entries[index] = sarc.Entries[index] with { Name = to };
    }
}

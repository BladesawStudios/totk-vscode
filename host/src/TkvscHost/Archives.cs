using SarcSharp;
using Tkvsc.Plugins;
using TkvscHost.Formats;

namespace TkvscHost;

/// <summary>
/// Nested .pack / .archive / .genvb / .blarc paths resolved to an open archive and the path inside it
/// (the equivalent of <c>archive_resolve.py</c>). A path like <c>Pack/A.pack.zs/Actor/B.pack.zs/Thing.bgyml</c>
/// opens each archive in turn.
/// </summary>
public sealed class Archives(Containers containers)
{
    private bool IsArchiveName(string name) => containers.Env.IsArchiveName(name);

    private bool IsBntxName(string name) => containers.Env.StripCompressedSuffix(name).EndsWith(".bntx", StringComparison.OrdinalIgnoreCase);

    // A plugin's format is told from the contents; everything else is SARC.
    private OpenArchive Open(byte[] plain, string path)
    {
        foreach (IArchiveFormat format in containers.ArchiveFormats)
            if (format.Matches(plain)) return OpenArchive.FromPlugin(format, plain, path);

        return OpenArchive.FromSarc(SarcFile.FromBinary(plain));
    }

    /// <summary>
    /// Splits a path on disk that goes into archives (<c>A.pack.zs/Actor/B.pack.zs/Thing.bgyml</c>) into the archive file and
    /// the path inside it; null if the path doesn't go inside an archive. Port of <c>get_disk_archive_path</c> and friends.
    /// Archives are told by <paramref name="env"/>'s patterns, or the default ones.
    /// </summary>
    public static (string DiskArchive, string Locator)? SplitFsPath(string fsPath, Env? env = null)
    {
        env ??= new Env();

        string normalized = fsPath.Replace('\\', '/').TrimEnd('/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int last = Array.FindLastIndex(segments, env.IsArchiveName);
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
            if (!env.IsArchiveName(segments[i])) continue;
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

    /// <summary>The archive on disk and how it was wrapped (zstd, a plugin's compression or Yaz0), so a save puts it back the same way.</summary>
    public (OpenArchive Archive, Unwrapped Wrapping) LoadArchive(string archivePath)
    {
        Unwrapped unwrapped = containers.Decompress(File.ReadAllBytes(archivePath), archivePath);

        if (unwrapped.Data.Length >= 4 && unwrapped.Data.AsSpan(0, 4).SequenceEqual("BNTX"u8))
            throw new InvalidDataException($"Cannot open BNTX file as an archive: {archivePath}. Use the BNTX reader instead.");

        return (Open(unwrapped.Data, archivePath), unwrapped);
    }

    private static byte[] GetFileBytes(OpenArchive archive, string internalPath)
    {
        SarcEntry? entry = archive[internalPath];
        if (entry is null)
        {
            string sample = string.Join(", ", archive.Entries.Take(5).Select(e => $"'{e.Name}'"));
            throw new FileNotFoundException($"File not found in archive: '{internalPath}'. Known paths sample: [{sample}]...");
        }

        return entry.Data;
    }

    // Every entry of that name: OOT's Layout2 archives hold four copies of each file, and the game could load any of them.
    private static void SetFile(OpenArchive archive, string internalPath, byte[] data)
    {
        bool found = false;
        for (int i = 0; i < archive.Entries.Count; i++)
        {
            if (archive.Entries[i].Name != internalPath) continue;
            archive.Entries[i] = archive.Entries[i] with { Data = data };
            found = true;
        }

        if (!found) archive.Entries.Add(new SarcEntry(internalPath, data));
    }

    /// <summary>A .bntx the path leads into: its bytes, the texture named after it, and the path up to and including the .bntx.</summary>
    public readonly record struct BntxView(byte[] Data, string Remainder, string Prefix);

    // A texture container is either the archive on disk itself or a .bntx entry inside one.
    public BntxView? ResolveBntx(string diskArchivePath, string locatorPath)
    {
        if (IsBntxName(diskArchivePath))
        {
            byte[] raw = containers.Decompress(File.ReadAllBytes(diskArchivePath), diskArchivePath).Data;
            if (BntxNames.IsBntx(raw)) return new(raw, Normalize(locatorPath), "");
        }

        if (locatorPath.Length == 0) return null;

        string[] segments = Normalize(locatorPath).Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (!IsBntxName(segments[i])) continue;

            ArchiveView parent = ResolveArchiveView(diskArchivePath, i > 0 ? string.Join('/', segments[..i]) : "");
            string entry = parent.PathPrefix.Length > 0 ? $"{parent.PathPrefix}/{segments[i]}".Trim('/') : segments[i];
            byte[] bytes = containers.Decompress(GetFileBytes(parent.Archive, entry), segments[i]).Data;
            if (BntxNames.IsBntx(bytes))
                return new(bytes, string.Join('/', segments[(i + 1)..]).Trim('/'), string.Join('/', segments[..(i + 1)]));
        }

        return null;
    }

    public readonly record struct ArchiveView(OpenArchive Archive, string PathPrefix, bool IsCompressed, string ConsumedArchivePrefix);

    public ArchiveView ResolveArchiveView(string diskArchivePath, string locatorPath)
    {
        locatorPath = Normalize(locatorPath);
        (OpenArchive archive, Unwrapped wrapping) = LoadArchive(diskArchivePath);
        bool compressed = wrapping.WasZstd || wrapping.WasYaz0;

        if (locatorPath.Length == 0) return new(archive, "", compressed, "");

        string[] segments = locatorPath.Split('/');
        int afterArchive = 0;
        List<string> consumed = [];

        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            if (!IsArchiveName(segment) || IsBntxName(segment)) continue;

            string entryPath = string.Join('/', segments[afterArchive..(index + 1)]);
            byte[] fileData = containers.Decompress(GetFileBytes(archive, entryPath), entryPath).Data;
            archive = Open(fileData, entryPath);
            consumed.AddRange(segments[afterArchive..(index + 1)]);
            afterArchive = index + 1;
        }

        string pathPrefix = string.Join('/', segments[afterArchive..]).Trim('/');
        return new(archive, pathPrefix, compressed, string.Join('/', consumed).Trim('/'));
    }

    // ---- reading ----

    public List<string> ListArchiveFiles(string diskArchivePath, string locatorPath)
    {
        if (ResolveBntx(diskArchivePath, locatorPath) is { } bntx)
        {
            List<string> textures = BntxNames.List(bntx.Data);
            return bntx.Prefix.Length > 0 ? [.. textures.Select(n => $"{bntx.Prefix}/{n}")] : textures;
        }

        ArchiveView view = ResolveArchiveView(diskArchivePath, locatorPath);
        IEnumerable<string> names = view.Archive.Entries.Select(e => e.Name);
        if (view.PathPrefix.Length > 0)
        {
            string prefix = view.PathPrefix + "/";
            names = names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal));
        }

        return view.ConsumedArchivePrefix.Length > 0
            ? [.. names.Select(n => $"{view.ConsumedArchivePrefix}/{n}")]
            : [.. names];
    }

    /// <summary>Every path inside an archive on disk, descending into nested archives and .bntx texture containers.</summary>
    public List<string> ListArchiveFilesRecursive(string diskArchivePath)
    {
        List<string> names = [];
        if (IsBntxName(diskArchivePath))
        {
            byte[] raw = containers.Decompress(File.ReadAllBytes(diskArchivePath), diskArchivePath).Data;
            if (BntxNames.IsBntx(raw)) return BntxNames.List(raw);
        }

        ListInto(LoadArchive(diskArchivePath).Archive, "", names);
        return names;
    }

    private void ListInto(OpenArchive archive, string prefix, List<string> names)
    {
        foreach (SarcEntry entry in archive.Entries)
        {
            string path = prefix + entry.Name;
            names.Add(path);
            if (!IsArchiveName(entry.Name)) continue;

            // A nested archive that doesn't open still lists as the file it is.
            try
            {
                byte[] data = containers.Decompress(entry.Data, entry.Name).Data;
                if (BntxNames.IsBntx(data)) names.AddRange(BntxNames.List(data).Select(n => $"{path}/{n}"));
                else ListInto(Open(data, entry.Name), path + "/", names);
            }
            catch (Exception) { }
        }
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

        ArchiveView view = ResolveArchiveView(diskArchivePath, filePath);
        byte[] bytes = GetFileBytes(view.Archive, view.PathPrefix);
        return BFontSharp.BFont.IsEncryptedFontPath(filePath) ? BFontSharp.BFont.ToOpenFont(bytes) : bytes;
    }

    /// <summary>The bytes stored for an entry, as they are (no decryption; nested archives allowed).</summary>
    public byte[] ReadArchiveEntryStoredBytes(string diskArchivePath, string filePath)
    {
        filePath = Normalize(filePath);
        if (filePath.Length == 0) throw new IOException("Is a directory: ''");

        string[] segments = filePath.Split('/');
        string parentLocator = string.Join('/', segments[..^1]);

        ArchiveView view = ResolveArchiveView(diskArchivePath, parentLocator);
        string entryPath = view.PathPrefix.Length > 0 ? $"{view.PathPrefix}/{segments[^1]}" : segments[^1];
        return GetFileBytes(view.Archive, entryPath);
    }

    // ---- changing ----

    private void RejectBntxMutation(string diskArchivePath, string operation, string targetPath = "")
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

    private int NextArchiveIndex(string[] segments)
    {
        for (int i = 0; i < segments.Length - 1; i++)
            if (IsArchiveName(segments[i])) return i;
        return -1;
    }

    // Opens the nested archive the path goes through, applies the change inside it, and writes it back up the chain.
    private void MutateNested(OpenArchive archive, string[] segments, Action<OpenArchive, string[]> leaf)
    {
        int index = NextArchiveIndex(segments);
        if (index < 0)
        {
            leaf(archive, segments);
            return;
        }

        string entryPath = string.Join('/', segments[..(index + 1)]);
        string[] remainder = segments[(index + 1)..];
        Unwrapped nested = containers.Decompress(GetFileBytes(archive, entryPath), entryPath);
        OpenArchive nestedArchive = Open(nested.Data, entryPath);

        MutateNested(nestedArchive, remainder, leaf);

        byte[] packed = containers.Compress(nestedArchive.Write(), entryPath, nested.WasZstd, nested.WasYaz0);
        SetFile(archive, entryPath, packed);
    }

    private void Save(string archivePath, OpenArchive archive, Unwrapped wrapping)
        => AtomicWrite(archivePath, containers.Compress(archive.Write(), archivePath, wrapping.WasZstd, wrapping.WasYaz0));

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
        if (containers.Env.IsCompressedName(filePath) && !containers.IsCompressed(data))
            data = containers.Compress(data, filePath, wasZstd: true, wasYaz0: false);

        (OpenArchive archive, Unwrapped wrapping) = LoadArchive(diskArchivePath);
        MutateNested(archive, segments, (s, rest) => SetFile(s, string.Join('/', rest).Trim('/'), data));
        Save(diskArchivePath, archive, wrapping);
    }

    public void DeleteArchiveEntry(string diskArchivePath, string targetPath)
    {
        targetPath = Normalize(targetPath);
        RejectBntxMutation(diskArchivePath, "delete", targetPath);
        if (targetPath.Length == 0) throw new ArgumentException("Missing target path");

        (OpenArchive archive, Unwrapped wrapping) = LoadArchive(diskArchivePath);
        MutateNested(archive, Split(targetPath), (s, rest) =>
        {
            string target = string.Join('/', rest).Trim('/');
            string prefix = target + "/";
            int removed = s.Entries.RemoveAll(e => e.Name == target || e.Name.StartsWith(prefix, StringComparison.Ordinal));
            if (removed == 0) throw new FileNotFoundException(target);
        });
        Save(diskArchivePath, archive, wrapping);
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
        (OpenArchive archive, Unwrapped wrapping) = LoadArchive(diskArchivePath);

        RenameIn(archive, oldSegments, newSegments);
        Save(diskArchivePath, archive, wrapping);
    }

    private void RenameIn(OpenArchive archive, string[] oldSegments, string[] newSegments)
    {
        int oldIndex = NextArchiveIndex(oldSegments);
        int newIndex = NextArchiveIndex(newSegments);

        if (oldIndex < 0 && newIndex < 0)
        {
            RenamePath(archive, string.Join('/', oldSegments).Trim('/'), string.Join('/', newSegments).Trim('/'));
            return;
        }

        if (oldIndex != newIndex || oldIndex < 0)
            throw new ArgumentException("Cannot rename across different nested archive levels.");

        string oldEntry = string.Join('/', oldSegments[..(oldIndex + 1)]);
        string newEntry = string.Join('/', newSegments[..(newIndex + 1)]);
        if (oldEntry != newEntry) throw new ArgumentException("Cannot rename across different nested archives.");

        Unwrapped nested = containers.Decompress(GetFileBytes(archive, oldEntry), oldEntry);
        OpenArchive nestedArchive = Open(nested.Data, oldEntry);
        RenameIn(nestedArchive, oldSegments[(oldIndex + 1)..], newSegments[(newIndex + 1)..]);
        SetFile(archive, oldEntry, containers.Compress(nestedArchive.Write(), oldEntry, nested.WasZstd, nested.WasYaz0));
    }

    private static void RenamePath(OpenArchive archive, string oldPath, string newPath)
    {
        string oldPrefix = oldPath + "/";
        List<(int Index, string To)> moves = [];
        for (int i = 0; i < archive.Entries.Count; i++)
        {
            string name = archive.Entries[i].Name;
            if (name == oldPath) moves.Add((i, newPath));
            else if (name.StartsWith(oldPrefix, StringComparison.Ordinal)) moves.Add((i, $"{newPath}/{name[oldPrefix.Length..]}"));
        }

        if (moves.Count == 0) throw new FileNotFoundException(oldPath);

        HashSet<int> moving = [.. moves.Select(m => m.Index)];
        HashSet<string> staying = [.. archive.Entries.Where((_, i) => !moving.Contains(i)).Select(e => e.Name)];
        foreach ((_, string to) in moves)
            if (staying.Contains(to)) throw new IOException($"File exists: '{to}'");

        foreach ((int index, string to) in moves)
            archive.Entries[index] = archive.Entries[index] with { Name = to };
    }
}

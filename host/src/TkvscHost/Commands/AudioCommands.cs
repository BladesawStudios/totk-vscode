using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BfAudioSharp;
using BymlSharp;

namespace TkvscHost;

/// <summary>The sound commands: BWAV and BARS files in the viewers, and replacing a BARS entry's audio. Through BfAudioSharp.</summary>
public static class AudioCommands
{
    public static void Register(Dictionary<string, CommandHandler> commands)
    {
        commands["read-bwav"] = ReadBwav;
        commands["read-bwav-audio"] = ReadBwavAudio;
        commands["list-bars"] = ListBars;
        commands["read-bars-audio"] = ReadBarsAudio;
        commands["read-bars-audio-many"] = ReadBarsAudioMany;
        commands["replace-bars-audio"] = ReplaceBarsAudio;
        commands["decode-audio-file"] = DecodeAudioFile;
        commands["decode-bars-entry"] = DecodeBarsEntry;
        commands["set-bars-loop"] = SetBarsLoop;
        commands["bars-section-info"] = BarsSectionInfo;
        commands["replace-bars-sections"] = ReplaceBarsSections;
        commands["replace-bars-same"] = ReplaceBarsSame;
        commands["replace-bars-batch"] = ReplaceBarsBatch;
    }

    // ---- shared ----

    private static string TempFile(string prefix, string suffix, byte[] data)
    {
        Span<byte> random = stackalloc byte[5];
        RandomNumberGenerator.Fill(random);
        string path = Path.Combine(Path.GetTempPath(), $"{prefix}{Convert.ToHexString(random).ToLowerInvariant()}{suffix}");
        File.WriteAllBytes(path, data);
        return path;
    }

    // The file an archive path points at: from inside the archive, or the file itself when the path is empty.
    private static (byte[] Data, string Logical) Source(Archives archives, string archive, string internalPath)
        => internalPath.Length > 0
            ? (archives.ReadArchiveFileBytes(archive, internalPath), internalPath)
            : (Archives.ReadShared(archive), archive);

    private static void Save(CommandContext c, Archives archives, string archive, string logical, byte[] bytes, bool wasZstd)
    {
        if (wasZstd) bytes = c.Containers.Compress(bytes, logical.Length > 0 ? logical : archive, wasZstd: true, wasYaz0: false);

        if (logical != archive) archives.WriteArchiveFileBytes(archive, logical, bytes);
        else Archives.AtomicWrite(archive, bytes);
    }

    private static PcmAudio Decode(byte[] bwav) => Bwav.Decode(bwav);

    private static JsonNode? Seconds(int? samples, int rate) => samples is { } s && rate > 0 ? JsonValue.Create(s / (double)rate) : null;

    private static JsonObject DecodedToWav(byte[] bwav, string name, bool isPrefetch)
    {
        PcmAudio audio = Decode(bwav);
        string path = TempFile("totk-bwav-", ".wav", Wav.Write(audio));
        return new JsonObject
        {
            ["wavPath"] = path,
            ["name"] = name,
            ["isPrefetch"] = isPrefetch,
            ["loopStart"] = Seconds(audio.LoopStart, audio.SampleRate),
            ["loopEnd"] = Seconds(audio.LoopEnd, audio.SampleRate),
        };
    }

    private static byte[] Plain(CommandContext c, byte[] data, string logical)
    {
        try
        {
            return c.Containers.Decompress(data, logical).Data;
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            return data;
        }
    }

    // ---- BWAV ----

    private static JsonNode ReadBwav(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.ArgOr(1, "");
        var (data, logical) = Source(new Archives(c.Containers), archive, internalPath);
        byte[] bwav = c.Containers.Decompress(data, logical).Data;
        if (!Bwav.IsBwav(bwav)) throw new InvalidDataException("Not a BWAV file");

        return new JsonObject { ["wavPath"] = DecodedToWav(bwav, Path.GetFileName(logical), false)["wavPath"]!.GetValue<string>() };
    }

    private static JsonNode ReadBwavAudio(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.Arg(1);
        var (data, logical) = Source(new Archives(c.Containers), archive, internalPath);
        byte[] bwav = Plain(c, data, logical);

        if (Bwav.IsDummy(bwav)) throw new InvalidDataException("This BWAV is a dummy clip (0 samples) and contains no audio data.");
        if (!Bwav.IsBwav(bwav)) throw new InvalidDataException($"Not a BWAV file (got magic {BitConverter.ToString(bwav[..Math.Min(4, bwav.Length)])})");

        return DecodedToWav(bwav, Path.GetFileName(logical.Replace('\\', '/')), false);
    }

    // ---- BARS ----

    // The romfs root a mod's BARS sits in: the folder above its Sound/Resource or Voice/Resource segment, or "" if it isn't laid out that way.
    private static string ModRomfsRoot(string archivePath)
    {
        bool rooted = archivePath.StartsWith('/') || archivePath.StartsWith('\\');
        string[] parts = archivePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 2; i >= 0; i--)
            if ((parts[i].Equals("sound", StringComparison.OrdinalIgnoreCase) || parts[i].Equals("voice", StringComparison.OrdinalIgnoreCase))
                && parts[i + 1].Equals("resource", StringComparison.OrdinalIgnoreCase))
                return i > 0 ? (rooted ? Path.DirectorySeparatorChar.ToString() : "") + string.Join(Path.DirectorySeparatorChar, parts[..i]) : "";
        return "";
    }

    private static byte[]? OriginalHeader(CommandContext c, byte[] bars, int entryIndex, string name)
    {
        if (LocateFullBwav(name, c.Romfs, "") is { } game)
        {
            using FileStream stream = File.OpenRead(game.Path);
            byte[] head = new byte[Math.Min(stream.Length, 0x10 + 0x4C * 32)];
            stream.ReadExactly(head);
            if (Bwav.IsBwav(head) && Bwav.ChannelCount(head) > 0) return head;
        }
        int offset = TotkBars.RawBwavOffset(bars, entryIndex);
        if (offset is -1 or 0 || offset >= bars.Length || !Bwav.IsBwav(bars.AsSpan(offset)) || Bwav.ChannelCount(bars.AsSpan(offset)) == 0) return null;
        return bars[offset..];
    }

    // Where an entry's whole BWAV is: the mod's own folder is searched before the game's, so a replaced stream wins.
    private static (string Path, string RomfsRelative)? LocateFullBwav(string name, string romfs, string modRoot)
    {
        foreach (string root in new[] { modRoot, romfs })
        {
            if (root.Length == 0) continue;
            foreach (string pattern in new[] { "Sound/Resource/Stream/{0}.bwav", "Sound/Resource/{0}.bwav", "Voice/Resource/Stream/{0}.bwav" })
            {
                string relative = string.Format(pattern, name);
                string candidate = Path.Combine(root, relative);
                if (File.Exists(candidate)) return (candidate, relative);
            }
        }
        return null;
    }

    private static (byte[] Data, TotkBars Bars) LoadBars(CommandContext c, string archive, string internalPath)
    {
        var (file, logical) = Source(new Archives(c.Containers), archive, internalPath);
        byte[] data = c.Containers.Decompress(file, logical).Data;
        return (data, TotkBars.Parse(data));
    }

    private static JsonNode ListBars(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.ArgOr(1, "");
        var (_, bars) = LoadBars(c, archive, internalPath);
        string modRoot = ModRomfsRoot(archive);

        JsonArray entries = [];
        foreach (TotkBarsEntry entry in bars.Entries)
        {
            TotkAmta m = entry.Metadata;
            entries.Add(new JsonObject
            {
                ["name"] = entry.Name,
                ["name_hash"] = entry.NameHash,
                ["amta_offset"] = entry.AmtaOffset,
                ["bwav_offset"] = entry.BwavOffset,
                ["has_prefetch"] = entry.BwavOffset != -1,
                ["has_romfs_bwav"] = LocateFullBwav(entry.Name, c.Romfs, modRoot) is not null,
                ["metadata"] = new JsonObject
                {
                    ["audio_type"] = m.AudioType,
                    ["sample_rate"] = m.SampleRate,
                    ["channel_count"] = m.ChannelCount,
                    ["num_samples"] = m.SampleCount,
                    ["loop_start"] = m.LoopStart,
                    ["is_looped"] = m.IsLooped,
                    ["volume_db"] = (double)m.VolumeDb,
                    ["volume_linear"] = m.VolumeLinear,
                    ["amplitude_peak"] = m.AmplitudePeak is { } peak ? (double)peak : null,
                    ["stream_tracks"] = new JsonArray([.. m.StreamTracks.Select(t => (JsonNode)new JsonObject
                    {
                        ["channel_count"] = t.ChannelCount,
                        ["volume"] = (double)t.Volume,
                    })]),
                    ["markers"] = new JsonArray([.. m.Markers.Select(mk => (JsonNode)new JsonObject
                    {
                        ["id"] = mk.Id,
                        ["name"] = mk.Name,
                        ["start"] = mk.Start,
                        ["length"] = mk.Length,
                    })]),
                },
            });
        }
        return new JsonObject { ["entries"] = entries };
    }

    private static JsonNode ReadBarsAudio(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.ArgOr(1, "");
        int index = int.Parse(c.ArgOr(2, "0"));
        bool forcePrefetch = c.ArgOr(3, "false") == "true";
        var (data, bars) = LoadBars(c, archive, internalPath);

        var (bwav, name, isPrefetch) = EntryBwav(c, archive, data, bars, index, forcePrefetch);
        return DecodedToWav(bwav, name, isPrefetch);
    }

    // The BWAV an entry plays: its full stream from the mod or romfs, or the clip embedded in the BARS.
    private static (byte[] Bwav, string Name, bool IsPrefetch) EntryBwav(
        CommandContext c, string archive, byte[] data, TotkBars bars, int index, bool forcePrefetch)
    {
        if (index < 0 || index >= bars.Entries.Count)
            throw new IndexOutOfRangeException($"Entry index {index} out of range (BARS has {bars.Entries.Count} entries)");
        TotkBarsEntry entry = bars.Entries[index];

        if (!forcePrefetch && LocateFullBwav(entry.Name, c.Romfs, ModRomfsRoot(archive)) is { } full)
            return (File.ReadAllBytes(full.Path), entry.Name, false);

        if (entry.BwavOffset == -1)
        {
            throw new FileNotFoundException(c.Romfs.Length > 0
                ? $"Full BWAV not found in romfs for '{entry.Name}', and this entry has no embedded prefetch clip."
                : $"No audio available for '{entry.Name}': no prefetch clip and romfs is not configured.");
        }

        return (data[entry.BwavOffset..], entry.Name, true);
    }

    // Bump when the WAVs a BWAV decodes to change, so cached ones from an older decoder aren't used.
    private const string AudioCacheVersion = "bars-wav-1";

    // Several entries of one BARS, read once: "index:1" plays the embedded clip, "index:0" the full stream when there is one.
    // With a cache folder, each WAV is kept there under a hash of its BWAV, so a BARS opened again (or another
    // BARS with the same sounds) skips the decode; a changed BARS or replaced stream hashes differently.
    private static JsonNode ReadBarsAudioMany(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.Arg(1), cacheDir = c.Arg(2), specs = c.Arg(3);
        var (data, bars) = LoadBars(c, archive, internalPath);
        if (cacheDir.Length > 0) Directory.CreateDirectory(cacheDir);

        JsonArray results = [];
        foreach (string spec in specs.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = spec.Split(':');
            int index = int.Parse(parts[0]);
            bool forcePrefetch = parts.Length > 1 && parts[1] == "1";
            JsonObject result;
            try
            {
                var (bwav, name, isPrefetch) = EntryBwav(c, archive, data, bars, index, forcePrefetch);
                result = cacheDir.Length > 0 ? CachedWav(cacheDir, bwav, name, isPrefetch) : DecodedToWav(bwav, name, isPrefetch);
            }
            catch (Exception e)
            {
                result = new JsonObject { ["error"] = e.Message };
            }
            result["index"] = index;
            result["usePrefetch"] = forcePrefetch;
            results.Add(result);
        }
        return new JsonObject { ["results"] = results };
    }

    private static JsonObject CachedWav(string cacheDir, byte[] bwav, string name, bool isPrefetch)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(AudioCacheVersion));
        hash.AppendData(bwav);
        string key = Convert.ToHexString(hash.GetHashAndReset(), 0, 20).ToLowerInvariant();
        string wavPath = Path.Combine(cacheDir, key + ".wav"), infoPath = Path.Combine(cacheDir, key + ".json");

        JsonObject? info = null;
        if (File.Exists(wavPath) && File.Exists(infoPath))
        {
            try
            {
                info = JsonNode.Parse(File.ReadAllText(infoPath)) as JsonObject;
                // The newest-used files are kept longest when the extension trims the cache.
                DateTime now = DateTime.UtcNow;
                File.SetLastWriteTimeUtc(wavPath, now);
                File.SetLastWriteTimeUtc(infoPath, now);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                info = null;
            }
        }
        if (info is null)
        {
            PcmAudio audio = Decode(bwav);
            info = new JsonObject
            {
                ["loopStart"] = Seconds(audio.LoopStart, audio.SampleRate),
                ["loopEnd"] = Seconds(audio.LoopEnd, audio.SampleRate),
            };
            // Written beside the final name and moved in, so a reader never sees half a file. The WAV goes
            // first: the info file is what marks an entry complete.
            WriteAtomically(wavPath, Wav.Write(audio));
            WriteAtomically(infoPath, System.Text.Encoding.UTF8.GetBytes(info.ToJsonString()));
        }

        return new JsonObject
        {
            ["wavPath"] = wavPath,
            ["name"] = name,
            ["isPrefetch"] = isPrefetch,
            ["loopStart"] = info["loopStart"]?.DeepClone(),
            ["loopEnd"] = info["loopEnd"]?.DeepClone(),
        };
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        string temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllBytes(temp, bytes);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another batch wrote the same sound and the viewer has it open: the file there is the same.
            File.Delete(temp);
            if (!File.Exists(path)) throw;
        }
    }

    // ---- replacing ----

    // "auto" (take the loop from the file), "none", or a sample number.
    private static (bool Auto, bool None, int Value) ParseLoop(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (text is "" or "auto" or "-1") return (true, false, 0);
        if (text == "none") return (false, true, 0);
        return (false, false, int.Parse(text));
    }

    private static string? FindFfmpeg()
    {
        string? over = Environment.GetEnvironmentVariable("TKVSC_FFMPEG")?.Trim();
        if (!string.IsNullOrEmpty(over) && File.Exists(over)) return over;

        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    // Any audio ffmpeg reads (MP3, OGG, FLAC, M4A, ...) as a 16-bit WAV, keeping the source rate and channel count.
    private static byte[] DecodeWithFfmpeg(byte[] payload, string nameHint)
    {
        string tool = FindFfmpeg() ?? throw new InvalidOperationException(
            "This audio format needs ffmpeg to decode. Install ffmpeg and make sure it is on PATH (or set TKVSC_FFMPEG to the executable), or supply a WAV/BWAV file instead.");

        string folder = Directory.CreateTempSubdirectory("totk-audio-conv-").FullName;
        try
        {
            string suffix = Path.GetExtension(nameHint);
            string input = Path.Combine(folder, "input" + (suffix.Length > 0 ? suffix : ".bin")), output = Path.Combine(folder, "output.wav");
            File.WriteAllBytes(input, payload);

            ProcessStartInfo start = new(tool) { RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };
            foreach (string arg in new[] { "-y", "-i", input, "-map_metadata", "-1", "-c:a", "pcm_s16le", output }) start.ArgumentList.Add(arg);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ffmpeg.");
            process.StandardInput.Close();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEndAsync().Wait();
            process.WaitForExit();

            if (process.ExitCode != 0 || !File.Exists(output))
            {
                string[] lines = [.. stderr.Result.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0 && !l.StartsWith("  ", StringComparison.Ordinal))];
                string detail = lines.Length > 0 ? string.Join(" | ", lines[^Math.Min(3, lines.Length)..]) : $"exit {process.ExitCode}";
                throw new InvalidOperationException($"ffmpeg failed to decode the audio: {detail}");
            }
            return File.ReadAllBytes(output);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    // A source file decoded the way replacing reads it, so sample positions picked in the loop editor hold for the encode.
    private static JsonNode DecodeAudioFile(CommandContext c) => DecodedPreview(ReadSourceAudio(c.Arg(0)));

    private static PcmAudio ReadSourceAudio(string file)
    {
        byte[] payload = File.ReadAllBytes(file);
        if (Bwav.IsBwav(payload))
        {
            if (Bwav.IsDummy(payload)) throw new InvalidDataException("This BWAV is a dummy clip (0 samples) and contains no audio data.");
            return Decode(payload);
        }
        if (!Wav.IsWav(payload)) payload = DecodeWithFfmpeg(payload, Path.GetFileName(file));
        return Wav.Parse(payload);
    }

    // ---- groups of entries, as sections of one piece of music ----

    // The game's music table: the mod's copy if it has one, else the game's. Sound/Bgm/bgm.Product.<version>.byml.zs in OoT and Tears.
    private static string? FindBgmTable(string root)
    {
        string folder = Path.Combine(root, "Sound", "Bgm");
        if (root.Length == 0 || !Directory.Exists(folder)) return null;
        return Directory.GetFiles(folder, "bgm.Product.*.byml*").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
    }

    private static (string Path, BymlFile Table, Unwrapped Unwrapped)? LoadBgmTable(CommandContext c, string modRoot)
    {
        if ((FindBgmTable(modRoot) ?? FindBgmTable(c.Romfs)) is not { } path) return null;
        Unwrapped unwrapped = c.Containers.Decompress(Archives.ReadShared(path), path);
        return (path, BymlFile.FromBinary(unwrapped.Data), unwrapped);
    }

    private static IEnumerable<Byml> BgmGraphs(BymlFile table)
        => table.Root["BGM"] is { IsMap: true } graphs ? graphs.AsMap.Values : [];

    private static IEnumerable<Byml> GraphNodes(Byml graph)
        => graph["Node"] is { IsArray: true } nodes ? nodes.AsArray : [];

    private static bool IsAsset(Byml node) => node["Type"]?.AsString() == "Asset";

    // Nodes with a set play length rather than their file's. Such a node also runs a negative fade-out as long as the file
    // is past that length, which looks like a tail overlapping the next node.
    private static bool IsFixedLength(Byml node) => node["IsDurationAuto"] is { Type: BymlType.Bool, Bool: false } && node["Duration"] is not null;

    private static double SecondsOf(Byml? time) => time?["Value"]?.AsNumber() ?? 0;

    // Where a node goes when nothing interrupts it: its unconditional transitions, or all of them if every one has a condition.
    private static IEnumerable<string> PlainNextNodes(Byml node)
    {
        if (node["Transition"] is not { IsArray: true } transitions) return [];
        List<Byml> all = [.. transitions.AsArray];
        List<Byml> plain = [.. all.Where(t => t["ConditionBin"] is null)];
        return (plain.Count > 0 ? plain : all).Select(t => t["DestinationNode"]?.AsString()).OfType<string>();
    }

    // The graph's assets in playing order, walking from Start along the uninterrupted path.
    private static List<string> PlayingOrder(Byml graph)
    {
        Dictionary<string, Byml> byName = [];
        foreach (Byml node in GraphNodes(graph))
            if (node["Name"]?.AsString() is { } name) byName[name] = node;

        List<string> order = [];
        HashSet<string> seen = [];
        void Walk(Byml node)
        {
            if (node["Name"]?.AsString() is not { } name || !seen.Add(name)) return;
            if (IsAsset(node) && node["Key"]?.AsString() is { } key && !order.Contains(key)) order.Add(key);
            foreach (string next in PlainNextNodes(node))
                if (byName.TryGetValue(next, out Byml? nextNode)) Walk(nextNode);
        }
        foreach (Byml node in GraphNodes(graph))
            if (node["Type"]?.AsString() == "Start") Walk(node);
        return order;
    }

    // The asset a graph's asset normally runs into, through any switches and the like.
    private static string? NextAsset(Byml graph, Byml asset)
    {
        Dictionary<string, Byml> byName = [];
        foreach (Byml node in GraphNodes(graph))
            if (node["Name"]?.AsString() is { } name) byName[name] = node;

        HashSet<string> seen = [];
        Queue<string> queue = new(PlainNextNodes(asset));
        while (queue.TryDequeue(out string? name))
        {
            if (!seen.Add(name) || !byName.TryGetValue(name, out Byml? node)) continue;
            if (IsAsset(node)) return node["Key"]?.AsString();
            foreach (string next in PlainNextNodes(node)) queue.Enqueue(next);
        }
        return null;
    }

    // What the section editor needs for the entries it was given: the file lengths, what the table sets, and the playing order.
    private static JsonNode BarsSectionInfo(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.ArgOr(1, "");
        int[] indices = [.. c.Arg(2).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];
        var (data, bars) = LoadBars(c, archive, internalPath);
        string modRoot = ModRomfsRoot(archive);
        BymlFile? table = LoadBgmTable(c, modRoot)?.Table;
        Dictionary<int, string> names = indices.ToDictionary(i => i, i => EntryAt(bars, i).Name);

        // The graph holding the most of these entries gives the order; the rest follow by name.
        Byml? graph = table is null ? null : BgmGraphs(table)
            .Select(g => (Graph: g, Count: GraphNodes(g).Count(n => IsAsset(n) && n["Key"]?.AsString() is { } key && names.ContainsValue(key))))
            .Where(g => g.Count > 0).OrderByDescending(g => g.Count).Select(g => g.Graph).FirstOrDefault();
        List<string> order = graph is null ? [] : PlayingOrder(graph);

        JsonArray sections = [];
        foreach (int index in indices
            .OrderBy(i => order.IndexOf(names[i]) is var at && at >= 0 ? at : int.MaxValue)
            .ThenBy(i => names[i].Contains("intro", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(i => names[i], StringComparer.OrdinalIgnoreCase))
        {
            TotkBarsEntry entry = bars.Entries[index];
            byte[]? whole = LocateFullBwav(entry.Name, c.Romfs, modRoot) is { } full ? File.ReadAllBytes(full.Path)
                : entry.BwavOffset != -1 ? data[entry.BwavOffset..]
                : null;

            Byml? node = table is null ? null : (graph is null ? [] : GraphNodes(graph)).Concat(BgmGraphs(table).SelectMany(GraphNodes))
                .FirstOrDefault(n => IsAsset(n) && n["Key"]?.AsString() == entry.Name);
            bool isFixed = node is not null && IsFixedLength(node);
            string? next = node is not null && graph is not null && GraphNodes(graph).Contains(node) ? NextAsset(graph, node) : null;

            sections.Add(new JsonObject
            {
                ["index"] = index,
                ["name"] = entry.Name,
                ["seconds"] = whole is null ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(whole.AsSpan(0x18)) / (double)Math.Max(1u, BinaryPrimitives.ReadUInt32LittleEndian(whole.AsSpan(0x14))),
                ["looped"] = whole is not null && BinaryPrimitives.ReadInt32LittleEndian(whole.AsSpan(0x4C)) != -1,
                ["inTable"] = node is not null,
                ["fixedSeconds"] = isFixed ? SecondsOf(node!["Duration"]) : null,
                ["tailSeconds"] = isFixed ? Math.Max(0, -SecondsOf(node!["FadeOutTime"])) : null,
                ["next"] = next,
            });
        }
        return new JsonObject { ["sections"] = sections, ["hasTable"] = table is not null };
    }

    // Cuts one source into several entries. The spec on stdin: { source, updateTable, sections: [{ index, start, end, loop }] }.
    // The BARS goes back to how it was if any of it fails, so a group is never left half replaced.
    private static JsonNode ReplaceBarsSections(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.ArgOr(1, "");
        JsonNode spec = JsonNode.Parse(c.StdinText) ?? throw new InvalidDataException("Empty section list.");
        PcmAudio source = ReadSourceAudio(spec["source"]!.GetValue<string>());
        bool updateTable = spec["updateTable"]?.GetValue<bool>() ?? false;
        List<(int Index, int Start, int End, bool Loop)> sections = [.. spec["sections"]!.AsArray().Select(s =>
            (s!["index"]!.GetValue<int>(), s["start"]!.GetValue<int>(), s["end"]!.GetValue<int>(), s["loop"]?.GetValue<bool>() ?? false))];

        foreach (var (index, start, end, _) in sections)
            if (start < 0 || end <= start || end > source.SampleCount)
                throw new ArgumentOutOfRangeException(nameof(spec), $"Section {start}-{end} for entry {index} doesn't fit the {source.SampleCount} samples of the source.");
        if (sections.Select(s => s.Index).Distinct().Count() != sections.Count) throw new InvalidDataException("An entry appears twice in the section list.");

        Archives archives = new(c.Containers);
        byte[] original = Archives.ReadShared(archive);
        JsonArray results = [];
        try
        {
            List<string> names = [];
            foreach (var (index, start, end, loop) in sections)
            {
                var (file, logical) = Source(archives, archive, internalPath);
                Unwrapped unwrapped = c.Containers.Decompress(file, logical);
                names.Add(EntryAt(TotkBars.Parse(unwrapped.Data), index).Name);

                PcmAudio cut = new() { SampleRate = source.SampleRate, Channels = [.. source.Channels.Select(channel => channel[start..end])] };
                var loopStart = loop ? (false, false, 0) : (false, true, 0);
                var loopEnd = loop ? (false, false, end - start) : (false, true, 0);
                results.Add(ReplaceEntryAudio(c, archives, archive, logical, unwrapped, index, loopStart, loopEnd, names[^1], false, Wav.Write(cut)));
            }

            var (tablePath, changed) = updateTable ? FreeFixedLengths(c, ModRomfsRoot(archive), names) : (null, 0);
            return new JsonObject
            {
                ["success"] = true,
                ["sections"] = results,
                ["tableUpdatedAt"] = tablePath,
                ["tableNodesChanged"] = changed,
            };
        }
        catch
        {
            Archives.AtomicWrite(archive, original);
            foreach (JsonNode? result in results)
                if (result?["fullBwavTempPath"]?.GetValue<string>() is { } temp) File.Delete(temp);
            throw;
        }
    }

    // Every entry in the list gets the same audio. The spec on stdin: { source, loopStart, loopEnd, trim, updateTable, indices }, the
    // loop as replace-bars-audio takes it. All or nothing, like replace-bars-sections.
    private static JsonNode ReplaceBarsSame(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.ArgOr(1, "");
        JsonNode spec = JsonNode.Parse(c.StdinText) ?? throw new InvalidDataException("Empty entry list.");
        string sourcePath = spec["source"]!.GetValue<string>();
        byte[] payload = File.ReadAllBytes(sourcePath);
        var loopStartArg = ParseLoop(spec["loopStart"]?.ToString() ?? "auto");
        var loopEndArg = ParseLoop(spec["loopEnd"]?.ToString() ?? "auto");
        bool trim = spec["trim"]?.GetValue<bool>() ?? false;
        bool updateTable = spec["updateTable"]?.GetValue<bool>() ?? false;
        int[] indices = [.. spec["indices"]!.AsArray().Select(i => i!.GetValue<int>())];
        if (indices.Length == 0) throw new InvalidDataException("No entries to replace.");
        if (indices.Distinct().Count() != indices.Length) throw new InvalidDataException("An entry appears twice in the list.");

        Archives archives = new(c.Containers);
        byte[] original = Archives.ReadShared(archive);
        JsonArray results = [];
        try
        {
            // The first entry of each channel count is encoded from the source; the rest get that same BWAV with the loop it
            // ended up with, so they all match exactly and a long track isn't encoded again for each entry.
            Dictionary<(int Channels, bool Opus), byte[]> encoded = [];
            Dictionary<string, double> newSeconds = [];
            foreach (int index in indices)
            {
                var (file, logical) = Source(archives, archive, internalPath);
                Unwrapped unwrapped = c.Containers.Decompress(file, logical);
                TotkBarsEntry entry = EntryAt(TotkBars.Parse(unwrapped.Data), index);
                var channels = (entry.Metadata.ChannelCount,
                    OriginalHeader(c, unwrapped.Data, index, entry.Name) is { } head && Bwav.Codec(head) == BwavCodec.Opus);

                JsonNode result;
                if (encoded.TryGetValue(channels, out byte[]? bwav))
                {
                    int end = BinaryPrimitives.ReadInt32LittleEndian(bwav.AsSpan(0x10 + 0x3C)), start = BinaryPrimitives.ReadInt32LittleEndian(bwav.AsSpan(0x10 + 0x40));
                    var loopStart = end == -1 ? (false, true, 0) : (false, false, start);
                    var loopEnd = end == -1 ? (false, true, 0) : (false, false, end);
                    result = ReplaceEntryAudio(c, archives, archive, logical, unwrapped, index, loopStart, loopEnd, entry.Name, false, bwav);
                }
                else
                {
                    result = ReplaceEntryAudio(c, archives, archive, logical, unwrapped, index, loopStartArg, loopEndArg, Path.GetFileName(sourcePath), trim, payload);
                    bwav = encoded[channels] = File.ReadAllBytes(result["fullBwavTempPath"]!.GetValue<string>());
                }
                results.Add(result);
                newSeconds[entry.Name] = Bwav.SampleCount(bwav) / (double)Math.Max(1u, BinaryPrimitives.ReadUInt32LittleEndian(bwav.AsSpan(0x14)));
            }

            var (tablePath, changed) = updateTable ? FreeFixedLengths(c, ModRomfsRoot(archive), [.. newSeconds.Keys], newSeconds) : (null, 0);
            return new JsonObject
            {
                ["success"] = true,
                ["sections"] = results,
                ["tableUpdatedAt"] = tablePath,
                ["tableNodesChanged"] = changed,
            };
        }
        catch
        {
            Archives.AtomicWrite(archive, original);
            foreach (JsonNode? result in results)
                if (result?["fullBwavTempPath"]?.GetValue<string>() is { } temp) File.Delete(temp);
            throw;
        }
    }

    // Replaces many entries, each with its own audio file, loading and saving the BARS once. The spec on stdin:
    // { items: [{ index, source, loopStart, loopEnd, trim }] }, the loop as replace-bars-audio takes it. Unlike the all-or-nothing
    // commands above, an item that can't be replaced (a file that won't decode, say) is reported and skipped, and the rest go in.
    private static JsonNode ReplaceBarsBatch(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.ArgOr(1, "");
        JsonNode spec = JsonNode.Parse(c.StdinText) ?? throw new InvalidDataException("Empty item list.");
        JsonArray items = spec["items"]?.AsArray() ?? throw new InvalidDataException("No items to replace.");
        if (items.Count == 0) throw new InvalidDataException("No entries to replace.");
        int[] indices = [.. items.Select(i => i!["index"]!.GetValue<int>())];
        if (indices.Distinct().Count() != indices.Length) throw new InvalidDataException("An entry appears twice in the list.");

        Archives archives = new(c.Containers);
        var (file, logical) = Source(archives, archive, internalPath);
        Unwrapped unwrapped = c.Containers.Decompress(file, logical);
        byte[] current = unwrapped.Data;

        JsonArray results = [];
        int replaced = 0;
        foreach (JsonNode? item in items)
        {
            int index = item!["index"]!.GetValue<int>();
            string source = item["source"]!.GetValue<string>();
            try
            {
                var loopStart = ParseLoop(item["loopStart"]?.ToString() ?? "auto");
                var loopEnd = ParseLoop(item["loopEnd"]?.ToString() ?? "auto");
                bool trim = item["trim"]?.GetValue<bool>() ?? false;
                byte[] payload = File.ReadAllBytes(source);

                // Each item starts from the BARS the earlier ones left, held in memory.
                byte[] before = current;
                JsonNode result = ReplaceEntryAudio(
                    c, archives, archive, logical, new Unwrapped(before, unwrapped.WasZstd, unwrapped.WasYaz0), index,
                    loopStart, loopEnd, Path.GetFileName(source), trim, payload, bytes => current = bytes);
                result["index"] = index;
                result["source"] = source;
                results.Add(result);
                replaced++;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                results.Add(new JsonObject { ["success"] = false, ["index"] = index, ["source"] = source, ["error"] = e.Message });
            }
        }

        if (replaced > 0) Save(c, archives, archive, logical, current, unwrapped.WasZstd);
        return new JsonObject { ["success"] = true, ["replaced"] = replaced, ["results"] = results };
    }

    // Makes these entries' table nodes play their whole file, in the exact shape of the table's other nodes that do. Given the
    // new lengths, a node already set to that length is left alone.
    private static (string? Path, int Changed) FreeFixedLengths(CommandContext c, string modRoot, List<string> names, IReadOnlyDictionary<string, double>? newSeconds = null)
    {
        if (modRoot.Length == 0 || LoadBgmTable(c, modRoot) is not { } loaded) return (null, 0);

        int changed = 0;
        foreach (Byml node in BgmGraphs(loaded.Table).SelectMany(GraphNodes))
        {
            if (!IsAsset(node) || node["Key"]?.AsString() is not { } key || !names.Contains(key) || !IsFixedLength(node)) continue;
            if (newSeconds is not null && newSeconds.TryGetValue(key, out double seconds) && Math.Abs(seconds - SecondsOf(node["Duration"])) < 0.0005) continue;
            IDictionary<string, Byml> map = node.AsMap;
            map.Remove("Duration");
            map.Remove("StartOffset");
            map["IsDurationAuto"] = true;
            // A new map, because the file may share this one between nodes.
            map["FadeOutTime"] = Byml.Map(new Dictionary<string, Byml>(StringComparer.Ordinal) { ["Type"] = "Second", ["Value"] = 0f });
            changed++;
        }
        if (changed == 0) return (null, 0);

        string target = Path.Combine(modRoot, "Sound", "Bgm", Path.GetFileName(loaded.Path));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Archives.AtomicWrite(target, c.Containers.Compress(loaded.Table.Write(), target, loaded.Unwrapped.WasZstd, loaded.Unwrapped.WasYaz0));
        return (target, changed);
    }

    private static JsonObject DecodedPreview(PcmAudio audio) => new()
    {
        ["wavPath"] = TempFile("totk-audio-preview-", ".wav", Wav.Write(audio)),
        ["sampleRate"] = audio.SampleRate,
        ["channels"] = audio.Channels.Length,
        ["samples"] = audio.SampleCount,
        ["loopStart"] = audio.LoopStart,
        ["loopEnd"] = audio.LoopEnd,
    };

    private static TotkBarsEntry EntryAt(TotkBars bars, int index) => index >= 0 && index < bars.Entries.Count
        ? bars.Entries[index]
        : throw new IndexOutOfRangeException($"Entry index {index} out of range (BARS has {bars.Entries.Count} entries)");

    // A prefetch clip is only the start of the sound, so its loop can't be judged or cut without the whole BWAV.
    private static InvalidDataException OnlyAPrefetchClip(string name)
        => new($"Only the prefetch clip of '{name}' is available, so its loop can't be edited. Set the game dump path so its full BWAV can be found.");

    private static bool IsEmbeddedPrefetch(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 0xC)) != 0;

    // An entry's whole sound for the loop editor: the BWAV on disk, or the BARS's own copy when that's all there is.
    private static JsonNode DecodeBarsEntry(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.ArgOr(1, "");
        var (data, bars) = LoadBars(c, archive, internalPath);
        TotkBarsEntry entry = EntryAt(bars, int.Parse(c.ArgOr(2, "0")));

        if (LocateFullBwav(entry.Name, c.Romfs, ModRomfsRoot(archive)) is { } full) return DecodedPreview(Decode(File.ReadAllBytes(full.Path)));
        if (entry.BwavOffset == -1) throw new FileNotFoundException($"No audio available for '{entry.Name}'.");
        if (IsEmbeddedPrefetch(data, entry.BwavOffset)) throw OnlyAPrefetchClip(entry.Name);
        return DecodedPreview(Decode(data[entry.BwavOffset..]));
    }

    // Sets an entry's loop without touching its audio. Loops live only in BWAV headers (real v5 AMTAs hold none), so that's
    // the BWAV on disk plus the BARS's embedded block, which carries the whole sound's loop even when it's a prefetch clip.
    private static JsonNode SetBarsLoop(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.ArgOr(1, "");
        int entryIndex = int.Parse(c.ArgOr(2, "0"));
        var startArg = ParseLoop(c.ArgOr(3, "none"));
        var endArg = ParseLoop(c.ArgOr(4, "none"));
        bool trim = c.ArgOr(5, "false") == "true";
        if (startArg.Auto || endArg.Auto) throw new ArgumentException("set-bars-loop needs explicit loop points or 'none'.");

        Archives archives = new(c.Containers);
        var (file, logical) = Source(archives, archive, internalPath);
        Unwrapped unwrapped = c.Containers.Decompress(file, logical);
        byte[] data = unwrapped.Data;
        TotkBarsEntry entry = EntryAt(TotkBars.Parse(data), entryIndex);

        var located = LocateFullBwav(entry.Name, c.Romfs, ModRomfsRoot(archive));
        byte[]? full = located is { } onDisk ? File.ReadAllBytes(onDisk.Path) : null;
        bool embedded = entry.BwavOffset != -1;
        if (full is null && !embedded) throw new FileNotFoundException($"No audio available for '{entry.Name}'.");
        bool prefetch = embedded && IsEmbeddedPrefetch(data, entry.BwavOffset);
        if (full is null && prefetch) throw OnlyAPrefetchClip(entry.Name);

        int? loopStart = startArg.None || endArg.None ? null : startArg.Value;
        int? loopEnd = startArg.None || endArg.None ? null : endArg.Value;

        // Cutting changes the audio, so it goes the way a replacement does: re-encoded, with the clip and loudness redone.
        byte[] whole = full ?? data[entry.BwavOffset..];
        if (trim && loopEnd is { } cutAt && cutAt < Bwav.SampleCount(whole))
            return ReplaceEntryAudio(c, archives, archive, logical, unwrapped, entryIndex, startArg, endArg, entry.Name, true, whole);

        int rawOffset = TotkBars.RawBwavOffset(data, entryIndex);
        bool stub = !embedded && rawOffset is not (-1 or 0) && Bwav.IsBwav(data.AsSpan(rawOffset)) && Bwav.ChannelCount(data.AsSpan(rawOffset)) > 0;

        if (embedded && !prefetch)
        {
            Bwav.SetLoop(data.AsSpan(entry.BwavOffset), loopStart, loopEnd).CopyTo(data, entry.BwavOffset);
            Save(c, archives, archive, logical, data, unwrapped.WasZstd);
        }
        else if (prefetch || stub)
        {
            byte[] block = TotkBars.BlockBytes(data, entryIndex)!;
            byte[] looped = Bwav.SetLoop(block, loopStart, loopEnd);
            if (TotkBars.SharesBlock(data, entryIndex))
                data = TotkBars.Rebuild(data, new Dictionary<int, byte[]> { [entryIndex] = looped }, new HashSet<int> { entryIndex });
            else
                looped.CopyTo(data, rawOffset);
            Save(c, archives, archive, logical, data, unwrapped.WasZstd);
        }

        byte[] written = full is null ? data[entry.BwavOffset..] : Bwav.SetLoop(full, loopStart, loopEnd);
        int endOut = BinaryPrimitives.ReadInt32LittleEndian(written.AsSpan(0x10 + 0x3C)), startOut = BinaryPrimitives.ReadInt32LittleEndian(written.AsSpan(0x10 + 0x40));
        return new JsonObject
        {
            ["success"] = true,
            ["name"] = entry.Name,
            ["embedded"] = embedded ? (prefetch ? "prefetch" : "full") : stub ? "stub" : null,
            ["needsStreamFile"] = full is not null,
            ["streamRelativePath"] = located?.RomfsRelative,
            ["fullBwavTempPath"] = full is null ? null : TempFile("totk-bwav-full-", ".bwav", written),
            ["numSamples"] = Bwav.SampleCount(written),
            ["channels"] = Bwav.ChannelCount(written),
            ["loopStart"] = endOut != -1 ? startOut : null,
            ["loopEnd"] = endOut != -1 ? endOut : null,
            ["loudnessUpdated"] = false,
        };
    }

    private static JsonNode ReplaceBarsAudio(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.ArgOr(1, "");
        int entryIndex = int.Parse(c.ArgOr(2, "0"));
        var loopStartArg = ParseLoop(c.ArgOr(3, "auto"));
        var loopEndArg = ParseLoop(c.ArgOr(4, "auto"));
        string nameHint = c.ArgOr(5, "audio.bin");
        bool trim = c.ArgOr(6, "false") == "true";

        string encoded = c.StdinText.Trim();
        byte[] payload = encoded.Length == 0 ? [] : Convert.FromBase64String(encoded);

        Archives archives = new(c.Containers);
        var (file, logical) = Source(archives, archive, internalPath);
        Unwrapped unwrapped = c.Containers.Decompress(file, logical);
        return ReplaceEntryAudio(c, archives, archive, logical, unwrapped, entryIndex, loopStartArg, loopEndArg, nameHint, trim, payload);
    }

    private static JsonNode ReplaceEntryAudio(
        CommandContext c, Archives archives, string archive, string logical, Unwrapped unwrapped, int entryIndex,
        (bool Auto, bool None, int Value) loopStartArg, (bool Auto, bool None, int Value) loopEndArg,
        string nameHint, bool trim, byte[] payload, Action<byte[]>? deferSave = null)
    {
        // With deferSave, the rebuilt BARS goes to the caller instead of the disk, so a batch can save once at the end.
        void Commit(byte[] bytes)
        {
            if (deferSave is not null) deferSave(bytes);
            else Save(c, archives, archive, logical, bytes, unwrapped.WasZstd);
        }

        // Every looping BWAV in the game ends on its loop end, so an earlier end can cut the audio there. Needs explicit loop points.
        bool explicitLoop = loopStartArg is { Auto: false, None: false } && loopEndArg is { Auto: false, None: false };
        bool trimAfterLoop = trim && explicitLoop;

        byte[] data = unwrapped.Data;
        TotkBarsEntry entry = EntryAt(TotkBars.Parse(data), entryIndex);

        // The AMTA channel count must match the audio (a v5 AMTA stores no length or loop, but does store channels), so remix to it.
        int targetChannels = entry.Metadata.ChannelCount;
        int? sourceChannels = null;
        (int? Start, int? End)? bwavAutoLoop = null;
        PcmAudio? pcm = null;

        byte[]? original = OriginalHeader(c, data, entryIndex, entry.Name);
        BwavCodec codec = original is not null && Bwav.Codec(original) == BwavCodec.Opus ? BwavCodec.Opus : BwavCodec.DspAdpcm;
        bool payloadNeedsReencode = codec == BwavCodec.Opus && Bwav.IsBwav(payload) && Bwav.ChannelCount(payload) > 0 && Bwav.Codec(payload) != BwavCodec.Opus;

        if (Bwav.IsBwav(payload) && ((targetChannels > 0 && Bwav.ChannelCount(payload) != targetChannels)
            || (trimAfterLoop && loopEndArg.Value < Bwav.SampleCount(payload))
            || payloadNeedsReencode))
        {
            // Decode it so it can be remixed or cut, keeping its own loop for "auto".
            int srcEnd = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0x10 + 0x3C)), srcStart = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0x10 + 0x40));
            bwavAutoLoop = srcEnd != -1 ? (srcStart, srcEnd) : (null, null);
            PcmAudio decoded = Decode(payload);
            decoded.LoopStart = decoded.LoopEnd = null;
            payload = Wav.Write(decoded);
        }

        byte[] fullBwav;
        if (Bwav.IsBwav(payload))
        {
            fullBwav = payload;
            if (loopStartArg.None) fullBwav = Bwav.SetLoop(fullBwav, null, null);
            else if (!loopStartArg.Auto) fullBwav = Bwav.SetLoop(fullBwav, loopStartArg.Value, loopEndArg is { Auto: false, None: false } ? loopEndArg.Value : 0x7FFFFFFF);
        }
        else
        {
            if (!Wav.IsWav(payload)) payload = DecodeWithFfmpeg(payload, nameHint);
            PcmAudio wav = Wav.Parse(payload);

            int? loopStart, loopEnd;
            if (loopStartArg.None) (loopStart, loopEnd) = (null, null);
            else if (loopStartArg.Auto) (loopStart, loopEnd) = bwavAutoLoop ?? (wav.LoopStart, wav.LoopEnd);
            else (loopStart, loopEnd) = (loopStartArg.Value, loopEndArg is { Auto: false, None: false } ? loopEndArg.Value : 0x7FFFFFFF);

            short[][] channels = wav.Channels;
            if (targetChannels > 0 && channels.Length != targetChannels)
            {
                sourceChannels = channels.Length;
                channels = Bwav.MatchChannelCount(channels, targetChannels);
            }
            if (trimAfterLoop && loopStart < loopEnd && loopEnd < channels[0].Length)
            {
                int kept = loopEnd.Value;
                channels = [.. channels.Select(channel => channel[..kept])];
            }
            ushort[]? pans = original is not null && Bwav.ChannelCount(original) == channels.Length ? Bwav.ReadPans(original) : null;
            fullBwav = codec == BwavCodec.Opus
                ? Bwav.BuildOpus(channels, wav.SampleRate, loopStart, loopEnd, pans)
                : Bwav.Build(channels, wav.SampleRate, loopStart, loopEnd, pans);
            pcm = new PcmAudio { SampleRate = wav.SampleRate, Channels = channels };
        }

        // Refresh the AMTA loudness (peak, R128) for the new audio; if it can't be measured the old figures stay.
        if (pcm is null)
        {
            try { pcm = Bwav.Decode(fullBwav); }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException) { pcm = null; }
        }

        byte[] working = (byte[])data.Clone();
        LoudnessStats? loudness = pcm is null ? null : AmtaLoudness.Measure(pcm.Channels, pcm.SampleRate);
        bool loudnessUpdated = loudness is not null && AmtaLoudness.Write(working, entry.AmtaOffset, loudness);

        // The loop that was actually written.
        int loopEndOut = BinaryPrimitives.ReadInt32LittleEndian(fullBwav.AsSpan(0x10 + 0x3C)), loopStartOut = BinaryPrimitives.ReadInt32LittleEndian(fullBwav.AsSpan(0x10 + 0x40));

        // The raw pair-table offset (the entry list reports -1 for empty placeholder clips, but the table may still point at a real block).
        int rawOffset = TotkBars.RawBwavOffset(working, entryIndex);

        string? embedded = null;
        bool needsStreamFile = false;
        if (rawOffset is -1 or 0)
        {
            // Stream-only entry: nothing embedded to swap. The caller gets the whole BWAV to put in Sound/Resource/Stream/.
            needsStreamFile = true;
            if (loudnessUpdated) Commit(working);
        }
        else
        {
            bool wasPrefetch = BinaryPrimitives.ReadUInt16LittleEndian(working.AsSpan(rawOffset + 0xC)) != 0;
            byte[] blob;
            if (wasPrefetch)
            {
                int? clip = Bwav.Codec(fullBwav) == BwavCodec.Opus ? Bwav.SamplesInFile(working.AsSpan(rawOffset)) : null;
                blob = Bwav.MakePrefetch(fullBwav, clip);
                embedded = "prefetch";
                needsStreamFile = true;
            }
            else if (Bwav.IsDummy(working.AsSpan(rawOffset)))
            {
                blob = Bwav.MakeStub(fullBwav);
                embedded = "stub";
                needsStreamFile = true;
            }
            else
            {
                blob = fullBwav;
                embedded = "full";
            }
            HashSet<int>? own = embedded is "prefetch" or "stub" ? [entryIndex] : null;
            byte[] rebuilt = TotkBars.Rebuild(working, new Dictionary<int, byte[]> { [entryIndex] = blob }, own);
            Commit(rebuilt);
        }

        // An existing whole BWAV is what the entry plays from, so it has to be replaced too, whatever the BARS embeds.
        var existing = LocateFullBwav(entry.Name, c.Romfs, ModRomfsRoot(archive));

        string fullPath = TempFile("totk-bwav-full-", ".bwav", fullBwav);
        return new JsonObject
        {
            ["success"] = true,
            ["name"] = entry.Name,
            ["embedded"] = embedded,
            ["needsStreamFile"] = needsStreamFile || existing is not null,
            ["streamRelativePath"] = existing?.RomfsRelative,
            ["fullBwavTempPath"] = fullPath,
            ["numSamples"] = Bwav.SampleCount(fullBwav),
            ["channels"] = Bwav.ChannelCount(fullBwav),
            ["loopStart"] = loopEndOut != -1 ? loopStartOut : null,
            ["loopEnd"] = loopEndOut != -1 ? loopEndOut : null,
            ["channelsConvertedFrom"] = sourceChannels,
            ["loudnessUpdated"] = loudnessUpdated,
            ["integratedLoudness"] = loudness?.Integrated,
        };
    }
}

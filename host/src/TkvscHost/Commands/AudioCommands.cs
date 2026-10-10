using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BfAudioSharp;

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
        commands["replace-bars-audio"] = ReplaceBarsAudio;
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
            : (File.ReadAllBytes(archive), archive);

    private static void Save(CommandContext c, Archives archives, string archive, string logical, byte[] bytes, bool wasZstd)
    {
        if (wasZstd) bytes = c.Containers.Compress(bytes, logical.Length > 0 ? logical : archive, wasZstd: true, wasYaz0: false);

        if (logical != archive) archives.WriteArchiveFileBytes(archive, logical, bytes);
        else File.WriteAllBytes(archive, bytes);
    }

    // A BWAV's samples. A codec BfAudioSharp does not know goes back to the Python bridge (vgmstream).
    private static PcmAudio Decode(byte[] bwav)
    {
        try
        {
            return Bwav.Decode(bwav);
        }
        catch (NotSupportedException e)
        {
            throw new NotPortedException(e.Message);
        }
    }

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

    // The romfs root a mod's BARS sits in: the folder above its Sound/Resource segment, or "" if it isn't laid out that way.
    private static string ModRomfsRoot(string archivePath)
    {
        bool rooted = archivePath.StartsWith('/') || archivePath.StartsWith('\\');
        string[] parts = archivePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 2; i >= 0; i--)
            if (parts[i].Equals("sound", StringComparison.OrdinalIgnoreCase) && parts[i + 1].Equals("resource", StringComparison.OrdinalIgnoreCase))
                return i > 0 ? (rooted ? Path.DirectorySeparatorChar.ToString() : "") + string.Join(Path.DirectorySeparatorChar, parts[..i]) : "";
        return "";
    }

    // An entry's whole BWAV: the mod's own folder is searched before the game's, so a replaced stream wins.
    private static byte[]? FindFullBwav(string name, string romfs, string modRoot)
    {
        foreach (string root in new[] { modRoot, romfs })
        {
            if (root.Length == 0) continue;
            foreach (string pattern in new[] { "Sound/Resource/Stream/{0}.bwav", "Sound/Resource/{0}.bwav" })
            {
                string candidate = Path.Combine(root, string.Format(pattern, name));
                if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
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
                ["has_romfs_bwav"] = FindFullBwav(entry.Name, c.Romfs, modRoot) is not null,
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

        if (index < 0 || index >= bars.Entries.Count)
            throw new IndexOutOfRangeException($"Entry index {index} out of range (BARS has {bars.Entries.Count} entries)");
        TotkBarsEntry entry = bars.Entries[index];

        if (!forcePrefetch && FindFullBwav(entry.Name, c.Romfs, ModRomfsRoot(archive)) is { } full)
            return DecodedToWav(full, entry.Name, false);

        if (entry.BwavOffset == -1)
        {
            throw new FileNotFoundException(c.Romfs.Length > 0
                ? $"Full BWAV not found in romfs for '{entry.Name}', and this entry has no embedded prefetch clip."
                : $"No audio available for '{entry.Name}': no prefetch clip and romfs is not configured.");
        }

        return DecodedToWav(data[entry.BwavOffset..], entry.Name, true);
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

    private static JsonNode ReplaceBarsAudio(CommandContext c)
    {
        string archive = c.Arg(0), internalPath = c.ArgOr(1, "");
        int entryIndex = int.Parse(c.ArgOr(2, "0"));
        var loopStartArg = ParseLoop(c.ArgOr(3, "auto"));
        var loopEndArg = ParseLoop(c.ArgOr(4, "auto"));
        string nameHint = c.ArgOr(5, "audio.bin");

        string encoded = c.StdinText.Trim();
        byte[] payload = encoded.Length == 0 ? [] : Convert.FromBase64String(encoded);

        Archives archives = new(c.Containers);
        var (file, logical) = Source(archives, archive, internalPath);
        Unwrapped unwrapped = c.Containers.Decompress(file, logical);
        byte[] data = unwrapped.Data;
        TotkBars bars = TotkBars.Parse(data);
        if (entryIndex < 0 || entryIndex >= bars.Entries.Count)
            throw new IndexOutOfRangeException($"Entry index {entryIndex} out of range (BARS has {bars.Entries.Count} entries)");
        TotkBarsEntry entry = bars.Entries[entryIndex];

        // The AMTA channel count must match the audio (a v5 AMTA stores no length or loop, but does store channels), so remix to it.
        int targetChannels = entry.Metadata.ChannelCount;
        int? sourceChannels = null;
        (int? Start, int? End)? bwavAutoLoop = null;
        PcmAudio? pcm = null;

        if (Bwav.IsBwav(payload) && targetChannels > 0 && Bwav.ChannelCount(payload) != targetChannels)
        {
            // Decode it so it can be remixed, keeping its own loop for "auto".
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
            fullBwav = Bwav.Build(channels, wav.SampleRate, loopStart, loopEnd);
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
            if (loudnessUpdated) Save(c, archives, archive, logical, working, unwrapped.WasZstd);
        }
        else
        {
            bool wasPrefetch = BinaryPrimitives.ReadUInt16LittleEndian(working.AsSpan(rawOffset + 0xC)) != 0;
            byte[] blob;
            if (wasPrefetch)
            {
                blob = Bwav.MakePrefetch(fullBwav);
                embedded = "prefetch";
                needsStreamFile = true;
            }
            else
            {
                blob = fullBwav;
                embedded = "full";
            }
            byte[] rebuilt = TotkBars.Rebuild(working, new Dictionary<int, byte[]> { [entryIndex] = blob });
            Save(c, archives, archive, logical, rebuilt, unwrapped.WasZstd);
        }

        string fullPath = TempFile("totk-bwav-full-", ".bwav", fullBwav);
        return new JsonObject
        {
            ["success"] = true,
            ["name"] = entry.Name,
            ["embedded"] = embedded,
            ["needsStreamFile"] = needsStreamFile,
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

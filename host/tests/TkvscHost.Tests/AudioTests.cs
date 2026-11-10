using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using BfAudioSharp;
using BymlSharp;

namespace TkvscHost.Tests;

public class AudioTests
{
    private static short[] Tone(int samples) => [.. Enumerable.Range(0, samples).Select(i => (short)(7000 * Math.Sin(i / 11.0)))];

    // One-entry BARS in the version 5 layout, with the given BWAV embedded (or none).
    private static byte[] Bars(string name, byte[]? bwav, int channels = 1)
    {
        const int amtaAt = 0x28, bwavAt = 0x140;
        byte[] file = new byte[bwav is null ? 0x140 : bwavAt + bwav.Length];
        "BARS"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)file.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), 0xFEFF);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(10), 0x0102);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x10), 1234);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x14), amtaAt);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x18), bwav is null ? -1 : bwavAt);

        Span<byte> amta = file.AsSpan(amtaAt, 0x100);
        "AMTA"u8.CopyTo(amta);
        BinaryPrimitives.WriteUInt16LittleEndian(amta[4..], 0xFEFF);
        BinaryPrimitives.WriteUInt16LittleEndian(amta[6..], 0x0500);
        BinaryPrimitives.WriteUInt32LittleEndian(amta[0x10..], 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(amta[0x14..], 0xA0);
        BinaryPrimitives.WriteUInt32LittleEndian(amta[0x24..], 0x40);
        amta[0x31] = (byte)channels;
        Encoding.UTF8.GetBytes(name).CopyTo(amta[(0x24 + 0x40)..]);
        BinaryPrimitives.WriteSingleLittleEndian(amta[(0x80 + 0xC)..], -6f);
        // A loudness block the way the game stores it, so replacing the audio can refresh it.
        BinaryPrimitives.WriteUInt32LittleEndian(amta[0x34..], 0x4F);
        bwav?.CopyTo(file, bwavAt);
        return file;
    }

    [Fact]
    public void ABwavIsDecodedToAWav()
    {
        using Workspace w = new();
        string file = w.Path("Tone.bwav");
        File.WriteAllBytes(file, Bwav.Build([Tone(2000)], 32000, 100, 1500));

        var result = w.Run("read-bwav-audio", [file, ""]);

        string wavPath = result["wavPath"]!.GetValue<string>();
        try
        {
            PcmAudio audio = Wav.Parse(File.ReadAllBytes(wavPath));
            Assert.Equal(32000, audio.SampleRate);
            Assert.Equal(2000, audio.SampleCount);
            Assert.Equal(100 / 32000.0, result["loopStart"]!.GetValue<double>(), 9);
            Assert.Equal(1500 / 32000.0, result["loopEnd"]!.GetValue<double>(), 9);
            Assert.Equal("Tone.bwav", result["name"]!.GetValue<string>());
            Assert.False(result["isPrefetch"]!.GetValue<bool>());
        }
        finally { File.Delete(wavPath); }
    }

    [Fact]
    public void ADummyBwavIsRefusedWithAClearMessage()
    {
        using Workspace w = new();
        byte[] bwav = Bwav.Build([Tone(100)], 32000);
        BinaryPrimitives.WriteUInt32LittleEndian(bwav.AsSpan(0x1C), 0);
        string file = w.Path("Empty.bwav");
        File.WriteAllBytes(file, bwav);

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => w.Run("read-bwav-audio", [file, ""]));
        Assert.Contains("dummy clip", e.Message);
    }

    [Fact]
    public void ABwavWithAnUnknownCodecIsRefused()
    {
        using Workspace w = new();
        byte[] bwav = Bwav.Build([Tone(100)], 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(bwav.AsSpan(0x10), (ushort)7);
        string file = w.Path("Voice.bwav");
        File.WriteAllBytes(file, bwav);

        Assert.Throws<NotSupportedException>(() => w.Run("read-bwav-audio", [file, ""]));
    }

    private static int[] BarsModified(Workspace w, string romfs, string file)
    {
        Env env = TkvscHost.Env.From(name => name switch
        {
            "TKVSC_COMPRESSION_BACKEND" => "plain-zstd-yaz0",
            "TKVSC_ROMFS" => romfs,
            _ => null,
        });
        var result = Commands.Find("bars-modified")!(w.Context("bars-modified", env, "", file, ""));
        return [.. result["modified"]!.AsArray().Select(n => n!.GetValue<int>())];
    }

    [Fact]
    public void ABarsEntryThatDiffersFromTheGamesIsModified()
    {
        using Workspace w = new();
        string game = Directory.CreateDirectory(Path.Combine(w.Root, "game", "Sound", "Resource")).FullName;
        string mod = Directory.CreateDirectory(Path.Combine(w.Root, "mod", "romfs", "Sound", "Resource")).FullName;
        File.WriteAllBytes(Path.Combine(game, "Sound.bars"), Bars("Hit", Bwav.Build([Tone(500)], 32000)));
        string romfs = Path.Combine(w.Root, "game");

        File.WriteAllBytes(Path.Combine(mod, "Sound.bars"), Bars("Hit", Bwav.Build([Tone(500)], 32000)));
        Assert.Empty(BarsModified(w, romfs, Path.Combine(mod, "Sound.bars")));

        File.WriteAllBytes(Path.Combine(mod, "Sound.bars"), Bars("Hit", Bwav.Build([Tone(800)], 32000)));
        Assert.Equal([0], BarsModified(w, romfs, Path.Combine(mod, "Sound.bars")));

        // The game's own file is never modified.
        Assert.Empty(BarsModified(w, romfs, Path.Combine(game, "Sound.bars")));
    }

    [Fact]
    public void AModifiedBarsEntryIsResetToTheGames()
    {
        using Workspace w = new();
        string game = Directory.CreateDirectory(Path.Combine(w.Root, "game", "Sound", "Resource")).FullName;
        string mod = Directory.CreateDirectory(Path.Combine(w.Root, "mod", "romfs", "Sound", "Resource")).FullName;
        byte[] original = Bars("Hit", Bwav.Build([Tone(500)], 32000));
        File.WriteAllBytes(Path.Combine(game, "Sound.bars"), original);
        string romfs = Path.Combine(w.Root, "game"), file = Path.Combine(mod, "Sound.bars");
        File.WriteAllBytes(file, Bars("Hit", Bwav.Build([Tone(900)], 32000)));
        Assert.Equal([0], BarsModified(w, romfs, file));

        Env env = TkvscHost.Env.From(name => name switch
        {
            "TKVSC_COMPRESSION_BACKEND" => "plain-zstd-yaz0",
            "TKVSC_ROMFS" => romfs,
            _ => null,
        });
        var result = Commands.Find("reset-bars-entry")!(w.Context("reset-bars-entry", env, "", file, "", "0"));

        Assert.True(result["changed"]!.GetValue<bool>());
        Assert.Empty(BarsModified(w, romfs, file));
    }

    [Fact]
    public void ABarsEntryWithItsOwnStreamIsModified()
    {
        using Workspace w = new();
        string game = Directory.CreateDirectory(Path.Combine(w.Root, "game", "Sound", "Resource", "Stream")).FullName;
        string mod = Directory.CreateDirectory(Path.Combine(w.Root, "mod", "romfs", "Sound", "Resource", "Stream")).FullName;
        byte[] bars = Bars("Line", null);
        File.WriteAllBytes(Path.Combine(game, "..", "Line.bars"), bars);
        File.WriteAllBytes(Path.Combine(mod, "..", "Line.bars"), bars);
        File.WriteAllBytes(Path.Combine(game, "Line.bwav"), Bwav.Build([Tone(3000)], 32000));
        string romfs = Path.Combine(w.Root, "game"), file = Path.Combine(mod, "..", "Line.bars");

        File.WriteAllBytes(Path.Combine(mod, "Line.bwav"), Bwav.Build([Tone(3000)], 32000));
        Assert.Empty(BarsModified(w, romfs, file));

        File.WriteAllBytes(Path.Combine(mod, "Line.bwav"), Bwav.Build([Tone(2000)], 32000));
        Assert.Equal([0], BarsModified(w, romfs, file));
    }

    [Fact]
    public void ABarsIsListedWithItsMetadata()
    {
        using Workspace w = new();
        string file = w.Path("Sound.bars");
        File.WriteAllBytes(file, Bars("Hit", Bwav.Build([Tone(500)], 32000)));

        var entry = w.Run("list-bars", [file, ""])["entries"]![0]!;

        Assert.Equal("Hit", entry["name"]!.GetValue<string>());
        Assert.Equal(1234u, entry["name_hash"]!.GetValue<uint>());
        Assert.True(entry["has_prefetch"]!.GetValue<bool>());
        Assert.False(entry["has_romfs_bwav"]!.GetValue<bool>());
        Assert.Equal(1, entry["metadata"]!["channel_count"]!.GetValue<int>());
        Assert.Equal(-6.0, entry["metadata"]!["volume_db"]!.GetValue<double>());
    }

    [Fact]
    public void TheFullBwavNextToABarsIsUsedBeforeTheEmbeddedClip()
    {
        using Workspace w = new();
        string folder = Directory.CreateDirectory(Path.Combine(w.Root, "Sound", "Resource", "Stream")).FullName;
        string file = Path.Combine(w.Root, "Sound", "Resource", "Sound.bars");
        File.WriteAllBytes(file, Bars("Hit", Bwav.Build([Tone(500)], 32000)));
        File.WriteAllBytes(Path.Combine(folder, "Hit.bwav"), Bwav.Build([Tone(3000)], 32000));

        var listed = w.Run("list-bars", [file, ""])["entries"]![0]!;
        var full = w.Run("read-bars-audio", [file, "", "0", "false"]);
        var clip = w.Run("read-bars-audio", [file, "", "0", "true"]);

        Assert.True(listed["has_romfs_bwav"]!.GetValue<bool>());
        Assert.False(full["isPrefetch"]!.GetValue<bool>());
        Assert.True(clip["isPrefetch"]!.GetValue<bool>());
        foreach (var result in new[] { full, clip }) File.Delete(result["wavPath"]!.GetValue<string>());
    }

    [Fact]
    public void AVoiceBarsFindsItsStreamInTheVoiceFolder()
    {
        using Workspace w = new();
        string folder = Directory.CreateDirectory(Path.Combine(w.Root, "Voice", "Resource", "Stream")).FullName;
        string file = Path.Combine(w.Root, "Voice", "Resource", "Line.bars");
        File.WriteAllBytes(file, Bars("Line", null));
        File.WriteAllBytes(Path.Combine(folder, "Line.bwav"), Bwav.Build([Tone(3000)], 32000));

        var listed = w.Run("list-bars", [file, ""])["entries"]![0]!;
        var full = w.Run("read-bars-audio", [file, "", "0", "false"]);

        Assert.True(listed["has_romfs_bwav"]!.GetValue<bool>());
        Assert.False(full["isPrefetch"]!.GetValue<bool>());
        File.Delete(full["wavPath"]!.GetValue<string>());
    }

    [Fact]
    public void ManyEntriesAreDecodedIntoTheCacheAndFoundThereAgain()
    {
        using Workspace w = new();
        string folder = Directory.CreateDirectory(Path.Combine(w.Root, "Sound", "Resource", "Stream")).FullName;
        string file = Path.Combine(w.Root, "Sound", "Resource", "Sound.bars");
        File.WriteAllBytes(file, Bars("Hit", Bwav.Build([Tone(500)], 32000)));
        string stream = Path.Combine(folder, "Hit.bwav");
        File.WriteAllBytes(stream, Bwav.Build([Tone(3000)], 32000, 100, 2900));
        string cache = w.Path("cache");

        var first = w.Run("read-bars-audio-many", [file, "", cache, "0:0,0:1,5:0"])["results"]!;
        var full = first[0]!;
        var clip = first[1]!;

        Assert.Equal(0, full["index"]!.GetValue<int>());
        Assert.False(full["usePrefetch"]!.GetValue<bool>());
        Assert.False(full["isPrefetch"]!.GetValue<bool>());
        Assert.True(clip["isPrefetch"]!.GetValue<bool>());
        Assert.Equal(100 / 32000.0, full["loopStart"]!.GetValue<double>(), 9);
        Assert.StartsWith(cache, full["wavPath"]!.GetValue<string>());
        Assert.NotEqual(full["wavPath"]!.GetValue<string>(), clip["wavPath"]!.GetValue<string>());
        Assert.Equal(3000, Wav.Parse(File.ReadAllBytes(full["wavPath"]!.GetValue<string>())).SampleCount);
        // One bad entry doesn't fail the others.
        Assert.Contains("out of range", first[2]!["error"]!.GetValue<string>());

        // Opened again: the same file, with the loop read back from the cache.
        var again = w.Run("read-bars-audio-many", [file, "", cache, "0:0"])["results"]![0]!;
        Assert.Equal(full["wavPath"]!.GetValue<string>(), again["wavPath"]!.GetValue<string>());
        Assert.Equal(2900 / 32000.0, again["loopEnd"]!.GetValue<double>(), 9);

        // A replaced stream is new audio, not the cached WAV.
        File.WriteAllBytes(stream, Bwav.Build([Tone(1200)], 32000));
        var replaced = w.Run("read-bars-audio-many", [file, "", cache, "0:0"])["results"]![0]!;
        Assert.NotEqual(full["wavPath"]!.GetValue<string>(), replaced["wavPath"]!.GetValue<string>());
        Assert.Equal(1200, Wav.Parse(File.ReadAllBytes(replaced["wavPath"]!.GetValue<string>())).SampleCount);
        Assert.Null(replaced["loopStart"]);
    }

    [Fact]
    public void ABarsEntryWithoutAnyAudioSaysSo()
    {
        using Workspace w = new();
        string file = w.Path("Quiet.bars");
        File.WriteAllBytes(file, Bars("Silent", null));

        FileNotFoundException e = Assert.Throws<FileNotFoundException>(() => w.Run("read-bars-audio", [file, "", "0", "false"]));
        Assert.Contains("Silent", e.Message);
    }

    [Fact]
    public void ReplacingAnEntrysAudioRebuildsTheBarsAndRefreshesTheFile()
    {
        using Workspace w = new();
        string file = w.Path("Sound.bars");
        File.WriteAllBytes(file, Bars("Hit", Bwav.Build([Tone(500)], 32000)));
        byte[] replacement = Wav.Write(new PcmAudio { SampleRate = 24000, Channels = [Tone(4000), Tone(4000)] });

        var result = w.Run("replace-bars-audio", [file, "", "0", "auto", "auto", "new.wav"], Convert.ToBase64String(replacement));

        Assert.True(result["success"]!.GetValue<bool>());
        Assert.Equal("full", result["embedded"]!.GetValue<string>());
        Assert.False(result["needsStreamFile"]!.GetValue<bool>());
        Assert.Equal(4000, result["numSamples"]!.GetValue<int>());
        Assert.Equal(1, result["channels"]!.GetValue<int>());
        Assert.Equal(2, result["channelsConvertedFrom"]!.GetValue<int>());

        TotkBars bars = TotkBars.Parse(File.ReadAllBytes(file));
        PcmAudio now = Bwav.Decode(File.ReadAllBytes(file).AsSpan(bars.Entries[0].BwavOffset));
        Assert.Equal(24000, now.SampleRate);
        Assert.Equal(4000, now.SampleCount);
        File.Delete(result["fullBwavTempPath"]!.GetValue<string>());
    }

    [Fact]
    public void ReplacingAnEntryWithAWholeBwavSaysWhereThatBwavLives()
    {
        using Workspace w = new();
        string folder = Directory.CreateDirectory(Path.Combine(w.Root, "Voice", "Resource", "Stream")).FullName;
        string file = Path.Combine(w.Root, "Voice", "Resource", "Line.bars");
        File.WriteAllBytes(file, Bars("Line", Bwav.Build([Tone(500)], 32000)));
        File.WriteAllBytes(Path.Combine(folder, "Line.bwav"), Bwav.Build([Tone(3000)], 32000));
        byte[] replacement = Wav.Write(new PcmAudio { SampleRate = 24000, Channels = [Tone(1000)] });

        var result = w.Run("replace-bars-audio", [file, "", "0", "none", "none", "new.wav"], Convert.ToBase64String(replacement));

        // Embedded whole, but the Full tab plays the BWAV on disk, so that has to change as well.
        Assert.Equal("full", result["embedded"]!.GetValue<string>());
        Assert.True(result["needsStreamFile"]!.GetValue<bool>());
        Assert.Equal("Voice/Resource/Stream/Line.bwav", result["streamRelativePath"]!.GetValue<string>());
        File.Delete(result["fullBwavTempPath"]!.GetValue<string>());
    }

    [Fact]
    public void ASourceFileIsDecodedForTheLoopEditor()
    {
        using Workspace w = new();
        string file = w.Path("Source.bwav");
        File.WriteAllBytes(file, Bwav.Build([Tone(2000), Tone(2000)], 24000, 100, 1500));

        var result = w.Run("decode-audio-file", [file]);

        string wavPath = result["wavPath"]!.GetValue<string>();
        try
        {
            Assert.Equal(24000, result["sampleRate"]!.GetValue<int>());
            Assert.Equal(2, result["channels"]!.GetValue<int>());
            Assert.Equal(2000, result["samples"]!.GetValue<int>());
            Assert.Equal(100, result["loopStart"]!.GetValue<int>());
            Assert.Equal(1500, result["loopEnd"]!.GetValue<int>());
            Assert.Equal(2000, Wav.Parse(File.ReadAllBytes(wavPath)).SampleCount);
        }
        finally { File.Delete(wavPath); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheAudioAfterALoopEndIsCutWhenAsked(bool sourceIsBwav)
    {
        using Workspace w = new();
        string file = w.Path("Sound.bars");
        File.WriteAllBytes(file, Bars("Long", null));
        byte[] replacement = sourceIsBwav
            ? Bwav.Build([Tone(5000)], 24000)
            : Wav.Write(new PcmAudio { SampleRate = 24000, Channels = [Tone(5000)] });
        string encoded = Convert.ToBase64String(replacement);

        var kept = w.Run("replace-bars-audio", [file, "", "0", "1000", "3000", "new.wav"], encoded);
        var cut = w.Run("replace-bars-audio", [file, "", "0", "1000", "3000", "new.wav", "true"], encoded);

        Assert.Equal(5000, kept["numSamples"]!.GetValue<int>());
        Assert.Equal(3000, cut["numSamples"]!.GetValue<int>());
        Assert.Equal(1000, cut["loopStart"]!.GetValue<int>());
        Assert.Equal(3000, cut["loopEnd"]!.GetValue<int>());
        foreach (var result in new[] { kept, cut }) File.Delete(result["fullBwavTempPath"]!.GetValue<string>());
    }

    private static (int? Start, int? End) LoopIn(ReadOnlySpan<byte> bwav)
    {
        int end = BinaryPrimitives.ReadInt32LittleEndian(bwav[(0x10 + 0x3C)..]), start = BinaryPrimitives.ReadInt32LittleEndian(bwav[(0x10 + 0x40)..]);
        return end == -1 ? (null, null) : (start, end);
    }

    // A BARS with a prefetch clip in it and the whole BWAV in the Stream folder beside it.
    private static (string Bars, byte[] Whole) StreamedEntry(Workspace w, string name)
    {
        string folder = Directory.CreateDirectory(Path.Combine(w.Root, "Sound", "Resource", "Stream")).FullName;
        string file = Path.Combine(w.Root, "Sound", "Resource", "Sound.bars");
        byte[] whole = Bwav.Build([Tone(30000)], 32000, 100, 30000);
        File.WriteAllBytes(file, Bars(name, Bwav.MakePrefetch(whole)));
        File.WriteAllBytes(Path.Combine(folder, name + ".bwav"), whole);
        return (file, whole);
    }

    [Fact]
    public void SettingALoopChangesOnlyTheLoopFieldsOfAnEmbeddedBwav()
    {
        using Workspace w = new();
        string file = w.Path("Sound.bars");
        byte[] bwav = Bwav.Build([Tone(5000)], 32000);
        byte[] before = Bars("Hit", bwav);
        File.WriteAllBytes(file, before);

        var result = w.Run("set-bars-loop", [file, "", "0", "1000", "5000", "false"]);

        byte[] after = File.ReadAllBytes(file);
        int at = TotkBars.Parse(after).Entries[0].BwavOffset;
        Assert.Equal((1000, 5000), LoopIn(after.AsSpan(at)));
        Assert.Equal(1000, result["loopStart"]!.GetValue<int>());
        Assert.False(result["needsStreamFile"]!.GetValue<bool>());
        Assert.Null(result["fullBwavTempPath"]);

        // Put the old loop fields back: nothing else may differ.
        BinaryPrimitives.WriteInt32LittleEndian(after.AsSpan(at + 0x10 + 0x3C), -1);
        BinaryPrimitives.WriteInt32LittleEndian(after.AsSpan(at + 0x10 + 0x40), 0);
        Assert.Equal(before, after);
    }

    [Fact]
    public void SettingALoopOnAStreamedEntryKeepsItsAudioAndUpdatesBothHeaders()
    {
        using Workspace w = new();
        var (file, whole) = StreamedEntry(w, "Hit");

        var result = w.Run("set-bars-loop", [file, "", "0", "2000", "25000", "false"]);

        string temp = result["fullBwavTempPath"]!.GetValue<string>();
        byte[] written = File.ReadAllBytes(temp);
        File.Delete(temp);
        Assert.Equal("prefetch", result["embedded"]!.GetValue<string>());
        Assert.True(result["needsStreamFile"]!.GetValue<bool>());
        Assert.Equal("Sound/Resource/Stream/Hit.bwav", result["streamRelativePath"]!.GetValue<string>());
        Assert.Equal((2000, 25000), LoopIn(written));
        Assert.Equal(Bwav.Decode(whole).Channels[0], Bwav.Decode(written).Channels[0]);

        // The clip stops long before the loop does, but carries the whole sound's loop like the game's do.
        byte[] bars = File.ReadAllBytes(file);
        Assert.Equal((2000, 25000), LoopIn(bars.AsSpan(TotkBars.Parse(bars).Entries[0].BwavOffset)));
    }

    [Fact]
    public void ALoopCanBeClearedFromAnEntry()
    {
        using Workspace w = new();
        var (file, _) = StreamedEntry(w, "Hit");

        var result = w.Run("set-bars-loop", [file, "", "0", "none", "none", "false"]);

        string temp = result["fullBwavTempPath"]!.GetValue<string>();
        byte[] written = File.ReadAllBytes(temp);
        File.Delete(temp);
        Assert.Null(result["loopStart"]);
        Assert.Equal((null, null), LoopIn(written));
    }

    [Fact]
    public void SettingALoopAndCuttingTheTailShortensTheWholeSound()
    {
        using Workspace w = new();
        var (file, _) = StreamedEntry(w, "Hit");

        var result = w.Run("set-bars-loop", [file, "", "0", "2000", "25000", "true"]);

        string temp = result["fullBwavTempPath"]!.GetValue<string>();
        byte[] written = File.ReadAllBytes(temp);
        File.Delete(temp);
        Assert.Equal(25000, result["numSamples"]!.GetValue<int>());
        Assert.Equal(25000, Bwav.SampleCount(written));
        Assert.Equal((2000, 25000), LoopIn(written));
    }

    [Fact]
    public void AnEntrysWholeSoundIsDecodedForTheLoopEditor()
    {
        using Workspace w = new();
        var (file, _) = StreamedEntry(w, "Hit");

        var result = w.Run("decode-bars-entry", [file, "", "0"]);

        File.Delete(result["wavPath"]!.GetValue<string>());
        Assert.Equal(30000, result["samples"]!.GetValue<int>());
        Assert.Equal(100, result["loopStart"]!.GetValue<int>());
        Assert.Equal(30000, result["loopEnd"]!.GetValue<int>());
    }

    [Fact]
    public void AnEntryWithOnlyAPrefetchClipRefusesLoopEdits()
    {
        using Workspace w = new();
        string file = w.Path("Sound.bars");
        byte[] before = Bars("Hit", Bwav.MakePrefetch(Bwav.Build([Tone(30000)], 32000)));
        File.WriteAllBytes(file, before);

        Assert.Contains("prefetch clip", Assert.Throws<InvalidDataException>(() => w.Run("decode-bars-entry", [file, "", "0"])).Message);
        Assert.Contains("prefetch clip", Assert.Throws<InvalidDataException>(() => w.Run("set-bars-loop", [file, "", "0", "0", "100", "false"])).Message);
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    // Several entries, each with a BWAV embedded, laid out like the game's: AMTAs, then BWAVs on 0x40 boundaries.
    private static byte[] ManyBars(params (string Name, byte[] Bwav)[] entries)
    {
        int count = entries.Length, amtaBase = 0x10 + 4 * count + 8 * count;
        int bwavAt = (amtaBase + 0x100 * count + 0x3F) & ~0x3F;
        int size = bwavAt;
        foreach (var (_, bwav) in entries) size = ((size + 0x3F) & ~0x3F) + bwav.Length;

        byte[] file = new byte[size];
        "BARS"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), 0xFEFF);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(10), 0x0102);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)count);

        int position = bwavAt;
        for (int i = 0; i < count; i++)
        {
            int amtaAt = amtaBase + i * 0x100;
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x10 + i * 4), (uint)(1000 + i));
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x10 + 4 * count + i * 8), (uint)amtaAt);
            position = (position + 0x3F) & ~0x3F;
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x10 + 4 * count + i * 8 + 4), position);

            Span<byte> amta = file.AsSpan(amtaAt, 0x100);
            "AMTA"u8.CopyTo(amta);
            BinaryPrimitives.WriteUInt16LittleEndian(amta[4..], 0xFEFF);
            BinaryPrimitives.WriteUInt16LittleEndian(amta[6..], 0x0500);
            BinaryPrimitives.WriteUInt32LittleEndian(amta[0x10..], 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(amta[0x14..], 0xA0);
            BinaryPrimitives.WriteUInt32LittleEndian(amta[0x24..], 0x40);
            amta[0x31] = 1;
            Encoding.UTF8.GetBytes(entries[i].Name).CopyTo(amta[(0x24 + 0x40)..]);

            entries[i].Bwav.CopyTo(file, position);
            position += entries[i].Bwav.Length;
        }
        return file;
    }

    private static Byml AssetNode(string name, string key, string next, double? fixedSeconds = null)
    {
        Dictionary<string, Byml> node = new(StringComparer.Ordinal)
        {
            ["Name"] = name,
            ["Type"] = "Asset",
            ["Key"] = key,
            ["IsDurationAuto"] = fixedSeconds is null,
            ["FadeOutTime"] = Byml.Map(new Dictionary<string, Byml>(StringComparer.Ordinal) { ["Type"] = "Second", ["Value"] = fixedSeconds is null ? 0f : -1.5f }),
            ["Transition"] = Byml.Array(
            [
                // An interruption comes first in the game's tables, and mustn't count as the normal path.
                Byml.Map(new Dictionary<string, Byml>(StringComparer.Ordinal) { ["ConditionBin"] = Byml.From(new byte[] { 1 }), ["DestinationNode"] = "elsewhere" }),
                Byml.Map(new Dictionary<string, Byml>(StringComparer.Ordinal) { ["DestinationNode"] = next, ["Type"] = "End" }),
            ]),
        };
        if (fixedSeconds is { } seconds)
        {
            node["Duration"] = Byml.Map(new Dictionary<string, Byml>(StringComparer.Ordinal) { ["Type"] = "Second", ["Value"] = (float)seconds });
            node["StartOffset"] = Byml.Map(new Dictionary<string, Byml>(StringComparer.Ordinal) { ["Type"] = "Second", ["Value"] = 0f });
        }
        return Byml.Map(node);
    }

    // BARS entries Song_01, Song_Intro and Song_02 (not in playing order), and a table playing Intro -> 01 -> switch -> 02 -> 01, with 02 fixed.
    private static string SongGroup(Workspace w)
    {
        Directory.CreateDirectory(Path.Combine(w.Root, "Sound", "Resource"));
        Directory.CreateDirectory(Path.Combine(w.Root, "Sound", "Bgm"));
        string bars = Path.Combine(w.Root, "Sound", "Resource", "Song.bars");
        File.WriteAllBytes(bars, ManyBars(
            ("Song_01", Bwav.Build([Tone(3200)], 32000)),
            ("Song_Intro", Bwav.Build([Tone(1600)], 32000)),
            ("Song_02", Bwav.Build([Tone(4800)], 32000))));

        Byml graph = Byml.Map(new Dictionary<string, Byml>(StringComparer.Ordinal)
        {
            ["Node"] = Byml.Array(
            [
                Byml.Map(new Dictionary<string, Byml>(StringComparer.Ordinal)
                {
                    ["Name"] = "start",
                    ["Type"] = "Start",
                    ["Transition"] = Byml.Array([Byml.Map(new Dictionary<string, Byml>(StringComparer.Ordinal) { ["ConditionBin"] = Byml.From(new byte[] { 1 }), ["DestinationNode"] = "intro" })]),
                }),
                AssetNode("one", "Song_01", "switch"),
                AssetNode("intro", "Song_Intro", "one"),
                AssetNode("two", "Song_02", "one", 0.1),
                Byml.Map(new Dictionary<string, Byml>(StringComparer.Ordinal)
                {
                    ["Name"] = "switch",
                    ["Type"] = "Switch",
                    ["Transition"] = Byml.Array(
                    [
                        Byml.Map(new Dictionary<string, Byml>(StringComparer.Ordinal) { ["ConditionBin"] = Byml.From(new byte[] { 1 }), ["DestinationNode"] = "elsewhere" }),
                        Byml.Map(new Dictionary<string, Byml>(StringComparer.Ordinal) { ["DestinationNode"] = "two" }),
                    ]),
                }),
            ]),
        });
        Byml.Map(new Dictionary<string, Byml>(StringComparer.Ordinal) { ["BGM"] = Byml.Map(new Dictionary<string, Byml>(StringComparer.Ordinal) { ["Song"] = graph }), ["Version"] = 3 })
            .ToFile(Path.Combine(w.Root, "Sound", "Bgm", "bgm.Product.100.byml"));
        return bars;
    }

    [Fact]
    public void SectionInfoFollowsTheTablesPlayingOrder()
    {
        using Workspace w = new();
        string bars = SongGroup(w);

        var result = w.Run("bars-section-info", [bars, "", "0,1,2"]);

        var sections = result["sections"]!.AsArray();
        Assert.True(result["hasTable"]!.GetValue<bool>());
        Assert.Equal(["Song_Intro", "Song_01", "Song_02"], sections.Select(s => s!["name"]!.GetValue<string>()));
        Assert.Equal([1, 0, 2], sections.Select(s => s!["index"]!.GetValue<int>()));
        Assert.Equal(0.05, sections[0]!["seconds"]!.GetValue<double>(), 6);
        Assert.Null(sections[0]!["fixedSeconds"]);
        Assert.Equal(0.1, sections[2]!["fixedSeconds"]!.GetValue<double>(), 5);
        Assert.Equal(1.5, sections[2]!["tailSeconds"]!.GetValue<double>(), 5);
        Assert.Equal("Song_01", sections[0]!["next"]!.GetValue<string>());
        Assert.Equal("Song_02", sections[1]!["next"]!.GetValue<string>());
        Assert.Equal("Song_01", sections[2]!["next"]!.GetValue<string>());
    }

    [Fact]
    public void WithoutATableSectionsGoIntroFirstThenByName()
    {
        using Workspace w = new();
        string bars = w.Path("Song.bars");
        File.WriteAllBytes(bars, ManyBars(("B", Bwav.Build([Tone(100)], 32000)), ("Song_Intro", Bwav.Build([Tone(100)], 32000)), ("A", Bwav.Build([Tone(100)], 32000))));

        var result = w.Run("bars-section-info", [bars, "", "0,1,2"]);

        Assert.False(result["hasTable"]!.GetValue<bool>());
        Assert.Equal(["Song_Intro", "A", "B"], result["sections"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()));
    }

    private static string SectionSpec(string source, bool updateTable, params (int Index, int Start, int End)[] sections)
        => new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = source,
            ["updateTable"] = updateTable,
            ["sections"] = new System.Text.Json.Nodes.JsonArray([.. sections.Select(s => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["index"] = s.Index, ["start"] = s.Start, ["end"] = s.End })]),
        }.ToJsonString();

    [Fact]
    public void OneSourceIsCutAcrossAGroupAndTheFixedLengthIsFreed()
    {
        using Workspace w = new();
        string bars = SongGroup(w);
        string source = w.Path("master.wav");
        short[] tone = [.. Enumerable.Range(0, 9000).Select(i => (short)(i % 2000))];
        File.WriteAllBytes(source, Wav.Write(new PcmAudio { SampleRate = 24000, Channels = [tone] }));

        var result = w.Run("replace-bars-sections", [bars, ""], SectionSpec(source, true, (1, 0, 2000), (0, 2000, 5000), (2, 5000, 9000)));

        byte[] written = File.ReadAllBytes(bars);
        TotkBars parsed = TotkBars.Parse(written);
        Assert.Equal(2000, Bwav.Decode(written.AsSpan(parsed.Entries[1].BwavOffset)).SampleCount);
        Assert.Equal(3000, Bwav.Decode(written.AsSpan(parsed.Entries[0].BwavOffset)).SampleCount);
        Assert.Equal(4000, Bwav.Decode(written.AsSpan(parsed.Entries[2].BwavOffset)).SampleCount);
        Assert.Equal(24000, Bwav.Decode(written.AsSpan(parsed.Entries[0].BwavOffset)).SampleRate);

        // The cut really is that part of the source (allowing for DSP-ADPCM's small error).
        short[] middle = Bwav.Decode(written.AsSpan(parsed.Entries[0].BwavOffset)).Channels[0];
        Assert.InRange(Math.Abs(middle[1500] - tone[3500]), 0, 200);

        var sections = result["sections"]!.AsArray();
        Assert.Equal(3, sections.Count);
        foreach (var section in sections) File.Delete(section!["fullBwavTempPath"]!.GetValue<string>());
        Assert.Equal(1, result["tableNodesChanged"]!.GetValue<int>());

        Byml nodes = Byml.FromFile(Path.Combine(w.Root, "Sound", "Bgm", "bgm.Product.100.byml"))["BGM"]!["Song"]!["Node"]!;
        Byml two = nodes.AsArray.First(n => n["Key"]?.AsString() == "Song_02");
        Assert.True(two["IsDurationAuto"]!.Bool);
        Assert.Null(two["Duration"]);
        Assert.Null(two["StartOffset"]);
        Assert.Equal(0f, two["FadeOutTime"]!["Value"]!.Float);
    }

    // Sets the channel count an entry's AMTA asks for (ManyBars makes them all mono).
    private static void SetChannels(byte[] bars, int entry, int channels)
    {
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bars.AsSpan(12));
        int amta = (int)BinaryPrimitives.ReadUInt32LittleEndian(bars.AsSpan(0x10 + 4 * count + entry * 8));
        bars[amta + 0x31] = (byte)channels;
    }

    private static string SameSpec(string source, string loopStart, string loopEnd, bool trim, bool updateTable, params int[] indices)
        => new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = source,
            ["loopStart"] = loopStart,
            ["loopEnd"] = loopEnd,
            ["trim"] = trim,
            ["updateTable"] = updateTable,
            ["indices"] = new System.Text.Json.Nodes.JsonArray([.. indices.Select(i => (System.Text.Json.Nodes.JsonNode)i)]),
        }.ToJsonString();

    [Fact]
    public void OneTrackGoesOnEveryEntryWithTheSameLoop()
    {
        using Workspace w = new();
        string bars = SongGroup(w);
        byte[] layout = File.ReadAllBytes(bars);
        SetChannels(layout, 2, 2);
        File.WriteAllBytes(bars, layout);
        string source = w.Path("track.wav");
        File.WriteAllBytes(source, Wav.Write(new PcmAudio { SampleRate = 24000, Channels = [Tone(6000), Tone(6000)] }));

        var result = w.Run("replace-bars-same", [bars, ""], SameSpec(source, "500", "4000", true, false, 0, 1, 2));

        byte[] written = File.ReadAllBytes(bars);
        TotkBars parsed = TotkBars.Parse(written);
        byte[] Embedded(int i) => written[parsed.Entries[i].BwavOffset..(parsed.Entries[i].BwavOffset + Bwav.Build([Tone(4000)], 24000).Length)];
        foreach (var section in result["sections"]!.AsArray()) File.Delete(section!["fullBwavTempPath"]!.GetValue<string>());

        // Cut at the loop end once, and the same everywhere.
        foreach (int i in new[] { 0, 1, 2 })
        {
            Assert.Equal((500, 4000), LoopIn(written.AsSpan(parsed.Entries[i].BwavOffset)));
            Assert.Equal(4000, Bwav.SampleCount(written.AsSpan(parsed.Entries[i].BwavOffset)));
        }
        // The two mono entries hold the very same encoded bytes; the stereo one kept both channels.
        Assert.Equal(Embedded(0), Embedded(1));
        Assert.Equal(1, Bwav.ChannelCount(written.AsSpan(parsed.Entries[0].BwavOffset)));
        Assert.Equal(2, Bwav.ChannelCount(written.AsSpan(parsed.Entries[2].BwavOffset)));
        Assert.Equal(3, result["sections"]!.AsArray().Count);
    }

    [Fact]
    public void OneTrackOnEveryEntryCanFreeOnlyTheFixedLengthsItChanges()
    {
        using Workspace w = new();
        string bars = SongGroup(w);
        string table = Path.Combine(w.Root, "Sound", "Bgm", "bgm.Product.100.byml");
        string source = w.Path("track.wav");

        // Song_02 is fixed at 0.1s; a track of exactly that length changes nothing in the table.
        File.WriteAllBytes(source, Wav.Write(new PcmAudio { SampleRate = 32000, Channels = [Tone(3200)] }));
        byte[] before = File.ReadAllBytes(table);
        var same = w.Run("replace-bars-same", [bars, ""], SameSpec(source, "none", "none", false, true, 2, 0));
        foreach (var section in same["sections"]!.AsArray()) File.Delete(section!["fullBwavTempPath"]!.GetValue<string>());
        Assert.Null(same["tableUpdatedAt"]);
        Assert.Equal(before, File.ReadAllBytes(table));

        File.WriteAllBytes(source, Wav.Write(new PcmAudio { SampleRate = 32000, Channels = [Tone(5000)] }));
        var longer = w.Run("replace-bars-same", [bars, ""], SameSpec(source, "none", "none", false, true, 2, 0));
        foreach (var section in longer["sections"]!.AsArray()) File.Delete(section!["fullBwavTempPath"]!.GetValue<string>());
        Assert.Equal(1, longer["tableNodesChanged"]!.GetValue<int>());
        Assert.True(Byml.FromFile(table)["BGM"]!["Song"]!["Node"]!.AsArray.First(n => n["Key"]?.AsString() == "Song_02")["IsDurationAuto"]!.Bool);
    }

    [Fact]
    public void OneTrackOnEveryEntryIsAllOrNothing()
    {
        using Workspace w = new();
        string bars = SongGroup(w);
        byte[] before = File.ReadAllBytes(bars);
        string source = w.Path("track.wav");
        File.WriteAllBytes(source, Wav.Write(new PcmAudio { SampleRate = 24000, Channels = [Tone(3000)] }));

        Assert.Throws<IndexOutOfRangeException>(() => w.Run("replace-bars-same", [bars, ""], SameSpec(source, "auto", "auto", false, false, 0, 1, 9)));
        Assert.Equal(before, File.ReadAllBytes(bars));
        Assert.Throws<InvalidDataException>(() => w.Run("replace-bars-same", [bars, ""], SameSpec(source, "auto", "auto", false, false, 0, 0)));
        Assert.Equal(before, File.ReadAllBytes(bars));
    }

    [Fact]
    public void AGroupThatFailsPartWayLeavesTheBarsAsItWas()
    {
        using Workspace w = new();
        string bars = SongGroup(w);
        byte[] before = File.ReadAllBytes(bars);
        string source = w.Path("master.wav");
        File.WriteAllBytes(source, Wav.Write(new PcmAudio { SampleRate = 24000, Channels = [Tone(9000)] }));

        Assert.Throws<IndexOutOfRangeException>(() => w.Run("replace-bars-sections", [bars, ""], SectionSpec(source, true, (1, 0, 2000), (7, 2000, 5000))));
        Assert.Equal(before, File.ReadAllBytes(bars));

        Assert.Throws<ArgumentOutOfRangeException>(() => w.Run("replace-bars-sections", [bars, ""], SectionSpec(source, true, (1, 0, 99999))));
        Assert.Equal(before, File.ReadAllBytes(bars));

        Assert.Throws<InvalidDataException>(() => w.Run("replace-bars-sections", [bars, ""], SectionSpec(source, true, (1, 0, 100), (1, 100, 200))));
        Assert.Equal(before, File.ReadAllBytes(bars));
    }

    [Fact]
    public void LeavingTheTableAloneIsAnOption()
    {
        using Workspace w = new();
        string bars = SongGroup(w);
        string table = Path.Combine(w.Root, "Sound", "Bgm", "bgm.Product.100.byml");
        byte[] before = File.ReadAllBytes(table);
        string source = w.Path("master.wav");
        File.WriteAllBytes(source, Wav.Write(new PcmAudio { SampleRate = 24000, Channels = [Tone(9000)] }));

        var result = w.Run("replace-bars-sections", [bars, ""], SectionSpec(source, false, (2, 0, 4000)));

        foreach (var section in result["sections"]!.AsArray()) File.Delete(section!["fullBwavTempPath"]!.GetValue<string>());
        Assert.Null(result["tableUpdatedAt"]);
        Assert.Equal(before, File.ReadAllBytes(table));
    }

    [Fact]
    public void AStreamOnlyEntryReturnsTheWholeBwavForTheStreamFolder()
    {
        using Workspace w = new();
        string file = w.Path("Sound.bars");
        File.WriteAllBytes(file, Bars("Long", null));
        byte[] replacement = Wav.Write(new PcmAudio { SampleRate = 24000, Channels = [Tone(1000)] });

        var result = w.Run("replace-bars-audio", [file, "", "0", "none", "none", "new.wav"], Convert.ToBase64String(replacement));

        Assert.True(result["needsStreamFile"]!.GetValue<bool>());
        Assert.Null(result["embedded"]);
        string path = result["fullBwavTempPath"]!.GetValue<string>();
        Assert.True(Bwav.IsBwav(File.ReadAllBytes(path)));
        File.Delete(path);
    }

    private static string BatchSpec(params (int Index, string Source, string Loop)[] items)
        => new JsonObject
        {
            ["items"] = new JsonArray([.. items.Select(i => (JsonNode)new JsonObject
            {
                ["index"] = i.Index, ["source"] = i.Source, ["loopStart"] = i.Loop, ["loopEnd"] = i.Loop,
            })]),
        }.ToJsonString();

    private static int SamplesOf(string barsFile, int index)
    {
        byte[] data = File.ReadAllBytes(barsFile);
        return Bwav.SampleCount(data.AsSpan(TotkBars.Parse(data).Entries[index].BwavOffset).ToArray());
    }

    [Fact]
    public void ABatchReplacesEveryEntryAndSavesOnce()
    {
        using Workspace w = new();
        string bars = SongGroup(w);
        string a = w.Path("a.wav"), b = w.Path("b.wav"), c = w.Path("c.wav");
        File.WriteAllBytes(a, Wav.Write(new PcmAudio { SampleRate = 32000, Channels = [Tone(1000)] }));
        File.WriteAllBytes(b, Wav.Write(new PcmAudio { SampleRate = 32000, Channels = [Tone(2000)] }));
        File.WriteAllBytes(c, Wav.Write(new PcmAudio { SampleRate = 32000, Channels = [Tone(3000)] }));

        var result = w.Run("replace-bars-batch", [bars, ""], BatchSpec((0, a, "none"), (1, b, "none"), (2, c, "none")));

        Assert.Equal(3, result["replaced"]!.GetValue<int>());
        Assert.Equal(1000, SamplesOf(bars, 0));
        Assert.Equal(2000, SamplesOf(bars, 1));
        Assert.Equal(3000, SamplesOf(bars, 2));
        foreach (var item in result["results"]!.AsArray()) File.Delete(item!["fullBwavTempPath"]!.GetValue<string>());
    }

    [Fact]
    public void ABatchSkipsAnItemThatFailsAndKeepsTheRest()
    {
        using Workspace w = new();
        string bars = SongGroup(w);
        string good = w.Path("good.wav"), missing = w.Path("missing.wav");
        File.WriteAllBytes(good, Wav.Write(new PcmAudio { SampleRate = 32000, Channels = [Tone(1500)] }));
        int untouched = SamplesOf(bars, 0);

        var result = w.Run("replace-bars-batch", [bars, ""], BatchSpec((0, missing, "none"), (1, good, "none"), (9, good, "none")));

        Assert.Equal(1, result["replaced"]!.GetValue<int>());
        var results = result["results"]!.AsArray();
        Assert.False(results[0]!["success"]!.GetValue<bool>());
        Assert.True(results[1]!["success"]!.GetValue<bool>());
        Assert.False(results[2]!["success"]!.GetValue<bool>());
        Assert.Equal(untouched, SamplesOf(bars, 0));
        Assert.Equal(1500, SamplesOf(bars, 1));
        File.Delete(results[1]!["fullBwavTempPath"]!.GetValue<string>());
    }

    [Fact]
    public void ABatchWithNothingToReplaceLeavesTheFileAlone()
    {
        using Workspace w = new();
        string bars = SongGroup(w);
        byte[] before = File.ReadAllBytes(bars);

        var result = w.Run("replace-bars-batch", [bars, ""], BatchSpec((0, w.Path("nope.wav"), "auto")));

        Assert.Equal(0, result["replaced"]!.GetValue<int>());
        Assert.Equal(before, File.ReadAllBytes(bars));
    }
}

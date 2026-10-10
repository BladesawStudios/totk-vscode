using System.Buffers.Binary;
using System.Text;
using BfAudioSharp;

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
}

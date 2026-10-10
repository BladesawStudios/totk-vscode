using Yaz0Sharp;
using ZsDicSharp;
using ZstdSharp;

namespace TkvscHost;

/// <summary>A file's bytes with the wrapping it came in, so it can be put back the same way.</summary>
public readonly record struct Unwrapped(byte[] Data, bool WasZstd, bool WasYaz0);

/// <summary>Zstd (with the game's dictionaries) and Yaz0 wrapping, the equivalent of <c>totk_compression</c>.</summary>
public sealed class Containers(Env env)
{
    private const string RomfsHelp = "Set TKVSC.romfsPath to your extracted game RomFS folder (must contain Pack/ZsDic.pack.zs).";

    private ZsDic? _dictionaries;
    private bool _triedDictionaries;
    private readonly object _lock = new();

    private bool Plain => env.CompressionBackend == "plain-zstd-yaz0";

    private ZsDic? Dictionaries()
    {
        lock (_lock)
        {
            if (_triedDictionaries) return _dictionaries;
            _triedDictionaries = true;

            if (env.Romfs.Length > 0 && File.Exists(Path.Combine(env.Romfs, "Pack", "ZsDic.pack.zs")))
                _dictionaries = ZsDic.FromRomfs(env.Romfs);
            return _dictionaries;
        }
    }

    public Unwrapped Decompress(byte[] data, string logicalPath = "")
    {
        if (ZsDic.IsCompressed(data))
        {
            if (!Plain && Dictionaries() is { } zs)
                return new(zs.Decompress(data, logicalPath.Length > 0 ? logicalPath : "file.zs"), true, false);

            try
            {
                using Decompressor plain = new();
                return new(plain.Unwrap(data).ToArray(), true, false);
            }
            catch (Exception e) when (e is ZstdException or InvalidDataException)
            {
                throw new InvalidDataException($"Cannot decompress .zs data (dictionary mismatch). {RomfsHelp}", e);
            }
        }

        if (Yaz0.IsCompressed(data)) return new(Yaz0.Decompress(data), false, true);

        return new(data, false, false);
    }

    public byte[] Compress(byte[] data, string logicalPath, bool wasZstd, bool wasYaz0)
    {
        if (wasYaz0) return Yaz0.Compress(data);
        if (!wasZstd) return data;

        if (Plain)
        {
            using Compressor plain = new();
            return plain.Wrap(data).ToArray();
        }

        ZsDic zs = Dictionaries() ?? throw new InvalidOperationException($"Cannot recompress .zs data. {RomfsHelp}");
        return zs.Compress(data, logicalPath.Length > 0 ? logicalPath : "file.zs");
    }

    public static bool IsZstd(byte[] data) => ZsDic.IsCompressed(data);
}

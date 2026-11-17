using Tkvsc.Plugins;
using TkvscHost.Plugins;
using Yaz0Sharp;
using ZsDicSharp;
using ZstdSharp;

namespace TkvscHost;

/// <summary>A file's bytes with the wrapping it came in, so it can be put back the same way.</summary>
public readonly record struct Unwrapped(byte[] Data, bool WasZstd, bool WasYaz0);

/// <summary>Zstd (with the game's dictionaries) and Yaz0 wrapping, the equivalent of <c>totk_compression</c>.</summary>
public sealed class Containers(Env env, GamePlugin? plugin = null)
{
    private string RomfsHelp => $"Set your extracted game RomFS folder in the TKVSC settings (it must contain {env.ZsDicPath}).";

    private ZsDic? _dictionaries;
    private bool _triedDictionaries;
    private readonly object _lock = new();

    public Env Env => env;

    /// <summary>The archive formats the game's plugin adds.</summary>
    public IReadOnlyList<IArchiveFormat> ArchiveFormats => plugin?.ArchiveFormats ?? [];

    // Looked up on use, as a plugin can register after it receives the host's services.
    private ICompressionBackend? Backend => plugin?.Compression;

    private bool Plain => env.CompressionBackend == "plain-zstd-yaz0";

    private ZsDic? Dictionaries()
    {
        lock (_lock)
        {
            if (_triedDictionaries) return _dictionaries;
            _triedDictionaries = true;

            if (env.Romfs.Length == 0) return null;

            string pack = Path.Combine(env.Romfs, env.ZsDicPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(pack)) _dictionaries = ZsDic.FromPack(File.ReadAllBytes(pack));
            return _dictionaries;
        }
    }

    public Unwrapped Decompress(byte[] data, string logicalPath = "")
    {
        // A plugin's backend stands in for zstd entirely, so whatever it reads it can write back. Yaz0 stays available.
        if (Backend is { } backend)
        {
            if (backend.IsCompressed(data)) return new(backend.Decompress(data, logicalPath), true, false);
        }
        else if (ZsDic.IsCompressed(data))
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

        if (Backend is { } backend) return backend.Compress(data, logicalPath);

        if (Plain)
        {
            using Compressor plain = new();
            return plain.Wrap(data).ToArray();
        }

        ZsDic zs = Dictionaries() ?? throw new InvalidOperationException($"Cannot recompress .zs data. {RomfsHelp}");
        return zs.Compress(data, logicalPath.Length > 0 ? logicalPath : "file.zs");
    }

    /// <summary>Whether the data is in the game's main compression: zstd, or the plugin's backend.</summary>
    public bool IsCompressed(byte[] data) => Backend is { } backend ? backend.IsCompressed(data) : ZsDic.IsCompressed(data);
}

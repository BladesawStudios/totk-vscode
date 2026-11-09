using System.Security.Cryptography;

namespace TkvscHost;

/// <summary>The content hashes the "modified" marker compares: a project's files against the game's.</summary>
public static class FileHashes
{
    /// <summary>The first 16 bytes of the SHA-256 of the content, in hex. Enough to tell files apart, and half the size.</summary>
    public static string Hex(ReadOnlySpan<byte> data)
        => Convert.ToHexStringLower(SHA256.HashData(data)[..16]);
}

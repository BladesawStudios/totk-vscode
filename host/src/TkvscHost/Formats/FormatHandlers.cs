using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AampSharp;
using BymlSharp;
using MsbtSharp;
using XLinkSharp;

namespace TkvscHost.Formats;

/// <summary>Turns a file's bytes into the text the editor shows, and edited text back into bytes.</summary>
public sealed partial class FormatHandlers(Env env, Containers containers, FileKinds kinds)
{
    private const string BinaryMessageFormat =
        "<Binary Data: {0} bytes. Editable types: .byml, .byaml, .bgyml, .msbt, .belnk, .bslnk, " +
        "AAMP (many extensions - see aamp-extensions.json)>";

    // ---- reading ----

    public string ReadContent(byte[] fileData, string logicalPath)
    {
        string? kind = kinds.KindOf(logicalPath, fileData);
        if (kinds.IsAddonKind(kind)) throw new NotPortedException($"add-on handler '{kind}'");

        return kind switch
        {
            "byml" => ReadByml(fileData, logicalPath),
            "msbt" => ReadMsbt(fileData, logicalPath),
            "aamp" => ReadAamp(fileData, logicalPath),
            "xlnk" => ReadXlnk(fileData, logicalPath),
            _ => string.Format(BinaryMessageFormat, fileData.Length),
        };
    }

    private static bool IsTagProduct(string logicalPath)
    {
        string name = Path.GetFileName(logicalPath.Replace('\\', '/')).ToLowerInvariant();
        return name.StartsWith("tag.product.", StringComparison.Ordinal) && name.Contains("rstbl", StringComparison.Ordinal);
    }

    private string ReadByml(byte[] fileData, string logicalPath)
    {
        byte[] data = containers.Decompress(fileData, logicalPath).Data;
        if (data.Length == 0) return "{}\n";

        if (!(data.AsSpan().StartsWith("YB"u8) || data.AsSpan().StartsWith("BY"u8)))
            throw new NotPortedException("unusual BYML magic");

        Byml root;
        try
        {
            root = BymlFile.FromBinary(data).Root;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        {
            throw new NotPortedException("BYML the C# reader does not accept");
        }

        // Particle data shows as YAML of its emitters' colours, in place of the binary; if it can't be read the binary stays.
        if (root.IsMap && root["PtclBin"] is { Type: BymlType.Binary or BymlType.BinaryAligned } ptcl)
        {
            try
            {
                string text = PtclText.ToText(ptcl.Binary, env.PtclCompactVectors);
                root.AsMap.Remove("PtclBin");
                root.AsMap["PTCL_JSON"] = text;
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
            }
        }

        if (IsTagProduct(logicalPath))
        {
            try
            {
                return TagProduct.ToEditorText(root, env.TagProductYaml);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
            }
        }

        try
        {
            return BymlEditorFormat.ToEditorText(root, env.BymlInlineContainerMaxCount);
        }
        catch (NotPortedException)
        {
            throw;
        }
        catch (Exception)
        {
            return root.ToYaml().TrimEnd() + "\n";
        }
    }

    [GeneratedRegex(@"(?<prefix>:\s*|,\s*|\{\s*)(?<num>[0-9]{10,})(?=\s*(?:$|[,}\]]))", RegexOptions.Multiline)]
    private static partial Regex U64Literal();

    /// <summary>Bare decimals too big for a signed 64-bit int become <c>!ul 0x...</c>, so they stay unsigned.</summary>
    public static string NormalizeU64Literals(string yaml)
        => U64Literal().Replace(yaml, m =>
        {
            BigInteger value = BigInteger.Parse(m.Groups["num"].Value);
            if (value <= long.MaxValue) return m.Value;
            return $"{m.Groups["prefix"].Value}!ul 0x{value.ToString("x").TrimStart('0')}";
        });

    private MsbtTags MsbtTags()
        => env.MsbtConfigPath.Length > 0 && File.Exists(env.MsbtConfigPath)
            ? MsbtSharp.MsbtTags.FromGcfFile(env.MsbtConfigPath)
            : MsbtSharp.MsbtTags.TotK;

    private string ReadMsbt(byte[] fileData, string logicalPath)
    {
        byte[] data = containers.Decompress(fileData, logicalPath).Data;
        if (data.Length == 0)
            return "# New MSBT file detected.\n# Creating MSBT from empty data is not supported yet.\n# Copy an existing MSBT as a template, then edit labels.\n";

        return MsbtFile.FromBinary(data).ToEditorText(MsbtTags());
    }

    private NameTable AampNames()
    {
        NameTable names = (env.GameId == "botw" ? NameTable.BotW : NameTable.TotK).Copy();

        if (env.AampHashNamesPath.Length > 0 && File.Exists(env.AampHashNamesPath))
        {
            try
            {
                var custom = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(env.AampHashNamesPath)) ?? [];
                foreach (var (hashKey, name) in custom)
                {
                    if (name.Length == 0) continue;
                    names.Add(name);

                    uint actual = AampSharp.Crc32.Hash(name);
                    if (TryParseHash(hashKey, out uint declared) && declared != actual)
                        Console.Error.WriteLine($"AAMP hash name mismatch: \"{name}\" hashes to 0x{actual:x8}, not {hashKey} as declared in TKVSC.aampHashNames");
                }
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
                Console.Error.WriteLine($"Failed to read AAMP hash names from {env.AampHashNamesPath}: {e.Message}");
            }
        }

        return names;
    }

    private static bool TryParseHash(string text, out uint value)
    {
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value)
            : uint.TryParse(text, out value);
    }

    private string ReadAamp(byte[] fileData, string logicalPath)
    {
        byte[] data = containers.Decompress(fileData, logicalPath).Data;
        if (data.Length == 0) return new ParameterIO().ToYaml(AampNames());
        if (!data.AsSpan().StartsWith("AAMP"u8)) throw new NotPortedException("not AAMP");

        return ParameterIO.FromBinary(data).ToYaml(AampNames());
    }

    private XLinkGame XlinkGame() => env.GameId == "botw" ? XLinkGame.BotW : XLinkGame.Totk;

    private string ReadXlnk(byte[] fileData, string logicalPath)
        => XLinkFile.FromBinary(containers.Decompress(fileData, logicalPath).Data).ToText();

    // ---- writing ----

    public byte[] WriteContent(string kind, byte[] original, string editorText, string logicalPath)
    {
        if (kinds.IsAddonKind(kind)) throw new NotPortedException($"add-on handler '{kind}'");

        return kind switch
        {
            "byml" => WriteByml(original, editorText, logicalPath),
            "msbt" => WriteMsbt(original, editorText, logicalPath),
            "aamp" => WriteAamp(original, editorText, logicalPath),
            "xlnk" => WriteXlnk(original, editorText, logicalPath),
            _ => throw new InvalidOperationException($"Cannot write file type: {logicalPath}"),
        };
    }

    private byte[] Wrap(byte[] bytes, string logicalPath, Unwrapped original)
        => containers.Compress(bytes, logicalPath,
            original.WasZstd || logicalPath.EndsWith(".zs", StringComparison.OrdinalIgnoreCase), original.WasYaz0);

    private byte[] WriteByml(byte[] original, string editorText, string logicalPath)
    {
        Unwrapped unwrapped = containers.Decompress(original, logicalPath);
        byte[] plain = unwrapped.Data;

        // The tag table's own form is told from ordinary BYML YAML by its PathList being a map.
        bool bigEndian = false;
        int version = BymlVersion.Default;
        BymlFile? originalFile = null;
        if (plain.AsSpan().StartsWith("BY"u8) || plain.AsSpan().StartsWith("YB"u8))
        {
            bigEndian = plain[0] == (byte)'B';
            version = bigEndian ? plain[2] << 8 | plain[3] : plain[3] << 8 | plain[2];
            try
            {
                originalFile = BymlFile.FromBinary(plain);
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
            {
                throw new NotPortedException("BYML the C# reader does not accept");
            }
        }

        if (IsTagProduct(logicalPath) && TagProduct.IsEditorText(editorText, out Byml tagDocument))
        {
            BymlFile table = new(TagProduct.FromEditorText(tagDocument), version, bigEndian);
            return Wrap(table.Write(), logicalPath, unwrapped);
        }

        BymlFile file = BymlFile.FromYaml(NormalizeU64Literals(editorText), originalFile);
        if (file.Root.IsMap && file.Root["PTCL_JSON"] is { Type: BymlType.String } ptclText
            && originalFile?.Root is { IsMap: true } originalRoot && originalRoot["PtclBin"] is { Type: BymlType.Binary or BymlType.BinaryAligned } originalPtcl)
        {
            byte[] edited = PtclText.ApplyText(originalPtcl.Binary, ptclText.String);
            file.Root.AsMap.Remove("PTCL_JSON");
            file.Root.AsMap["PtclBin"] = originalPtcl.BinaryAlignment != 0 ? Byml.From(edited, originalPtcl.BinaryAlignment) : Byml.From(edited);
        }
        file.BigEndian = bigEndian;
        file.Version = version;
        return Wrap(file.Write(), logicalPath, unwrapped);
    }

    private byte[] WriteMsbt(byte[] original, string editorText, string logicalPath)
    {
        Unwrapped unwrapped = containers.Decompress(original, logicalPath);
        if (unwrapped.Data.Length == 0)
            throw new InvalidOperationException("Cannot create MSBT from empty file yet. Copy an existing .msbt as a template first.");

        MsbtFile file = MsbtFile.FromBinary(unwrapped.Data);
        file.ApplyEditorText(editorText, MsbtTags());
        return Wrap(file.ToBinary(), logicalPath, unwrapped);
    }

    private byte[] WriteAamp(byte[] original, string editorText, string logicalPath)
    {
        Unwrapped unwrapped = containers.Decompress(original, logicalPath);
        return Wrap(ParameterIO.FromYaml(editorText).ToBinary(), logicalPath, unwrapped);
    }

    private byte[] WriteXlnk(byte[] original, string editorText, string logicalPath)
    {
        // The wrapping is told from the magic alone: decompressing just to find out would need the dictionaries.
        bool yaz0 = original.AsSpan().StartsWith("Yaz0"u8);
        bool zstd = !yaz0 && (Containers.IsZstd(original) || logicalPath.EndsWith(".zs", StringComparison.OrdinalIgnoreCase));

        byte[] bytes = XLinkFile.FromText(editorText, XlinkGame()).ToBinary();
        return containers.Compress(bytes, logicalPath, zstd, yaz0);
    }
}

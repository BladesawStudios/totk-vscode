using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AampSharp;
using BymlSharp;
using MsbtSharp;
using Tkvsc.Plugins;
using TkvscHost.Plugins;
using XLinkSharp;

namespace TkvscHost.Formats;

/// <summary>Turns a file's bytes into the text the editor shows, and edited text back into bytes.</summary>
public sealed partial class FormatHandlers(Env env, Containers containers, FileKinds kinds, GamePlugin? plugin = null)
{
    private const string BinaryMessageFormat =
        "<Binary Data: {0} bytes. Editable types: .byml, .byaml, .bgyml, .msbt, .belnk, .bslnk, " +
        "AAMP (many extensions - see aamp-extensions.json)>";

    private IFormatHandler? PluginFormat(string? kind) => kind is null ? null : plugin?.Format(kind);

    // ---- reading ----

    public string ReadContent(byte[] fileData, string logicalPath)
    {
        string? kind = kinds.KindOf(logicalPath, fileData);

        // The game's plugin comes first, so it can replace a built-in format.
        if (PluginFormat(kind) is { } handler) return handler.Read(containers.Decompress(fileData, logicalPath).Data, logicalPath);

        return kind switch
        {
            "byml" => ReadByml(fileData, logicalPath),
            "msbt" => ReadMsbt(fileData, logicalPath),
            "aamp" => ReadAamp(fileData, logicalPath),
            "xlnk" => ReadXlnk(fileData, logicalPath),
            "fmaa" or "fmat" or "emitter" or "bfres-info" => Encoding.UTF8.GetString(containers.Decompress(fileData, logicalPath).Data),
            _ => string.Format(BinaryMessageFormat, fileData.Length),
        };
    }

    private static bool IsTagProduct(string logicalPath)
    {
        string name = Path.GetFileName(logicalPath.Replace('\\', '/')).ToLowerInvariant();
        return name.StartsWith("tag.product.", StringComparison.Ordinal) && name.Contains("rstbl", StringComparison.Ordinal);
    }

    private string ReadByml(byte[] fileData, string logicalPath)
        => HashNames.Load(env).Annotate(ReadBymlText(fileData, logicalPath));

    private string ReadBymlText(byte[] fileData, string logicalPath)
    {
        byte[] data = containers.Decompress(fileData, logicalPath).Data;
        if (data.Length == 0) return "{}\n";

        if (!(data.AsSpan().StartsWith("YB"u8) || data.AsSpan().StartsWith("BY"u8)))
            throw new InvalidDataException("The file is not a BYML (it does not start with BY or YB).");

        Byml root;
        try
        {
            root = BymlFile.FromBinary(data).Root;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        {
            throw new InvalidDataException($"The BYML could not be read: {e.Message}", e);
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
            return "# New MSBT file.\n# Add one message per line as 'Label: text', then save. Use \\n inside the text for a line break.\n";

        return MsbtFile.FromBinary(data).ToEditorText(MsbtTags());
    }

    private NameTable AampNames()
    {
        NameTable names = env.AampNameTable switch
        {
            "botw" => NameTable.BotW.Copy(),
            "none" => new NameTable(),
            _ => NameTable.TotK.Copy(),
        };

        AddAampNames(names, env.ProfileAampNamesPath);
        AddAampNames(names, env.AampHashNamesPath);
        return names;
    }

    private static void AddAampNames(NameTable names, string path)
    {
        if (path.Length == 0 || !File.Exists(path)) return;

        try
        {
            var custom = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
            foreach (var (hashKey, name) in custom)
            {
                if (name.Length == 0) continue;
                names.Add(name);

                uint actual = AampSharp.Crc32.Hash(name);
                if (TryParseHash(hashKey, out uint declared) && declared != actual)
                    Console.Error.WriteLine($"AAMP hash name mismatch: \"{name}\" hashes to 0x{actual:x8}, not {hashKey} as declared in {path}");
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Console.Error.WriteLine($"Failed to read AAMP hash names from {path}: {e.Message}");
        }
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
        if (!data.AsSpan().StartsWith("AAMP"u8)) throw new InvalidDataException("The file is not an AAMP (it does not start with AAMP).");

        return ParameterIO.FromBinary(data).ToYaml(AampNames());
    }

    // The text only names the module; the game's profile can say which version its files use, or the library's default stands.
    private uint? XlinkVersion(ModuleType module) => module == ModuleType.ELink ? env.XlinkELinkVersion : env.XlinkSLinkVersion;

    private string ReadXlnk(byte[] fileData, string logicalPath)
        => XLinkFile.FromBinary(containers.Decompress(fileData, logicalPath).Data).ToText();

    // ---- writing ----

    public byte[] WriteContent(string kind, byte[] original, string editorText, string logicalPath)
    {
        if (PluginFormat(kind) is { } handler)
        {
            Unwrapped unwrapped = containers.Decompress(original, logicalPath);
            return Wrap(handler.Write(unwrapped.Data, editorText, logicalPath), logicalPath, unwrapped);
        }

        return kind switch
        {
            "byml" => WriteByml(original, editorText, logicalPath),
            "msbt" => WriteMsbt(original, editorText, logicalPath),
            "aamp" => WriteAamp(original, editorText, logicalPath),
            "xlnk" => WriteXlnk(original, editorText, logicalPath),
            "fmaa" => WriteMaterialAnim(original, editorText),
            "fmat" => WriteModelParams(editorText),
            "emitter" => WriteEmitter(editorText),
            "bfres-info" => throw new InvalidOperationException($"{Path.GetFileName(logicalPath)} is a read-only summary: skeletal animations, textures, shapes and bones in a BFRES cannot be edited here."),
            _ => throw new InvalidOperationException($"Cannot write file type: {logicalPath}"),
        };
    }

    private byte[] Wrap(byte[] bytes, string logicalPath, Unwrapped original)
        => containers.Compress(bytes, logicalPath,
            original.WasZstd || env.IsCompressedName(logicalPath), original.WasYaz0);

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
                throw new InvalidDataException($"The BYML could not be read: {e.Message}", e);
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

        // An empty file starts a new one in the shape the game's own use: little endian, UTF-16, version 3, labels and text.
        MsbtFile file = unwrapped.Data.Length == 0 ? NewMsbt() : MsbtFile.FromBinary(unwrapped.Data);
        file.ApplyEditorText(editorText, MsbtTags(), addNewLabels: true);
        return Wrap(file.ToBinary(), logicalPath, unwrapped);
    }

    private static MsbtFile NewMsbt()
    {
        MsbtFile file = new() { BigEndian = false, Encoding = MsbtEncoding.Utf16, Version = 3 };
        file.SectionOrder.AddRange(["LBL1", "TXT2"]);
        return file;
    }

    // A material animation inside a BFRES is stored as its YAML. The text is checked and written back in its standard
    // form, with a changed constant keeping its float and raw word in agreement.
    private byte[] WriteMaterialAnim(byte[] original, string editorText)
    {
        BfresSharp.Model.Animation.MaterialAnim? previous = original.Length == 0 ? null : BfresSharp.Text.MaterialAnimYaml.FromYaml(Encoding.UTF8.GetString(original), null, containers.ShaderNames);
        BfresSharp.Model.Animation.MaterialAnim edited = BfresSharp.Text.MaterialAnimYaml.FromYaml(editorText, previous, containers.ShaderNames);
        return Encoding.UTF8.GetBytes(BfresSharp.Text.MaterialAnimYaml.ToYaml(edited, containers.ShaderNames));
    }

    // A model inside a BFRES is stored as the YAML of its materials' parameters; the BFRES applies it to the model when it is saved.
    // Here the text is only checked, as the model it belongs to is not at hand.
    private static byte[] WriteEmitter(string editorText)
    {
        if (!BymlSharp.Byml.FromYaml(editorText).IsMap) throw new InvalidDataException("An emitter is a YAML map of renderers and their colours.");
        return Encoding.UTF8.GetBytes(editorText);
    }

    private static byte[] WriteModelParams(string editorText)
    {
        BfresSharp.Text.ModelParamsYaml.Check(editorText);
        return Encoding.UTF8.GetBytes(editorText);
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
        bool zstd = !yaz0 && (containers.IsCompressed(original) || env.IsCompressedName(logicalPath));

        XLinkFile file = XLinkFile.FromText(editorText);
        if (OriginalXlinkVersion(original, logicalPath, zstd, file.Module) is { } kept) file.Version = kept;
        else if (XlinkVersion(file.Module) is { } fallback) file.Version = fallback;
        byte[] bytes = file.ToBinary();
        return containers.Compress(bytes, logicalPath, zstd, yaz0);
    }

    // Keeps the version of the file being replaced (e.g. 0x22) while its module still matches the edited text.
    private uint? OriginalXlinkVersion(byte[] original, string logicalPath, bool zstd, ModuleType module)
    {
        if (zstd || original.Length == 0) return null;
        try
        {
            XLinkFile previous = XLinkFile.FromBinary(containers.Decompress(original, logicalPath).Data);
            return previous.Module == module ? previous.Version : null;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}

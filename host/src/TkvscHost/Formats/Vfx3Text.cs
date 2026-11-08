using System.Buffers.Binary;
using System.Text;
using BymlSharp;

namespace TkvscHost.Formats;

/// <summary>
/// The colours and alphas of the emitters in a Vfx3 particle binary (the <c>PtclBin</c> of a <c>.vsetb.byml</c>), as YAML
/// to edit and back. Only values already in the file are overwritten: nothing moves, and a channel keeps its kind and its
/// number of keys.
/// </summary>
/// <remarks>
/// A <c>BFFH</c> file holds a <c>Default</c> section whose first word points at a <c>VFX3</c> block; pointers count from
/// the start of the file. The block lists emitters, each with four tables of channels and one of renderers. A channel is
/// found by the hash of <c>emitter/module/parameter</c>, and is a hash, a u32 and a u32 whose low byte is its kind.
/// </remarks>
public static class Vfx3Text
{
    private static readonly string[] ChannelNames =
    [
        "ParticleColor0/ParticleColor", "ParticleColor0/ParticleAlpha", "ParticleColor1/ParticleColor", "ParticleColor1/ParticleAlpha",
        "GlobalColor0/Color", "GlobalColor0/Alpha", "GlobalColor1/Color", "GlobalColor1/Alpha", "ColorScale/ColorScaleValue",
    ];

    // Channel kinds, as the game's nn::vfx3 ChannelManager::SetupEvaluators reads them. A kind says where the value is: in
    // the record at +0x10, in the record's bits as a whole number, or in the bits as an index into the game's table of
    // common numbers. A three-number kind with one value also says which of the three it fills; the others are 0.
    private const int FloatInFile = 0x00, FloatWhole = 0x01, FloatFromTable = 0x02;
    private const int Float3InFile = 0x0D, Float3Whole = 0x0E, Float3FromTable = 0x15, Float3OneInFile = 0x1C, Float3Patterns = 7;
    private const int KeyedFloat = 0x45, ListFloat3 = 0x50, KeyedFloat3 = 0x51;

    // The channels each form of a three-number kind fills, in kind order.
    private static readonly bool[][] Patterns =
    [
        [true, false, false], [false, true, false], [false, false, true],
        [true, true, false], [false, true, true], [true, false, true], [true, true, true],
    ];

    // The table the game keeps at 0x7103dfb6d8 in Ocarina of Time 1.0.0.
    private static readonly uint[] CommonNumbers =
    [
        0xBF666666, 0xBF4CCCCD, 0xBF400000, 0xBF333333, 0xBF19999A, 0xBF000000, 0xBECCCCCD, 0xBE99999A, 0xBE800000, 0xBE4CCCCD, 0xBDCCCCCD, 0xBDB851EC,
        0xBDA3D70A, 0xBD99999A, 0xBD8F5C29, 0xBD75C28F, 0xBD4CCCCD, 0xBD23D70A, 0xBCF5C28F, 0xBCCCCCCD, 0xBCA3D70A, 0xBC23D70A, 0xBBF5C28F, 0xBBA3D70A,
        0xBB23D70A, 0xBA83126F, 0xBA03126F, 0xB8D1B717, 0x38D1B717, 0x3951B717, 0x399D4952, 0x39D1B717, 0x3A03126F, 0x3A1D4952, 0x3A378034, 0x3A51B717,
        0x3A6BEDFA, 0x3A83126F, 0x3B03126F, 0x3B23D70A, 0x3B449BA6, 0x3B83126F, 0x3BA3D70A, 0x3BC49BA6, 0x3BE56042, 0x3BF5C28F, 0x3C03126F, 0x3C1374BC,
        0x3C23D70A, 0x3CA3D70A, 0x3CCCCCCD, 0x3CF5C28F, 0x3D23D70A, 0x3D4CCCCD, 0x3D75C28F, 0x3D8F5C29, 0x3D99999A, 0x3DA3D70A, 0x3DB851EC, 0x3DCCCCCD,
        0x3DE147AE, 0x3DF5C28F, 0x3E051EB8, 0x3E0F5C29, 0x3E19999A, 0x3E23D70A, 0x3E2E147B, 0x3E3851EC, 0x3E428F5C, 0x3E4CCCCD, 0x3E570A3D, 0x3E6147AE,
        0x3E6B851F, 0x3E75C28F, 0x3E800000, 0x3E851EB8, 0x3E8A3D71, 0x3E8F5C29, 0x3E947AE1, 0x3E99999A, 0x3E9EB852, 0x3EA3D70A, 0x3EA8F5C3, 0x3EAE147B,
        0x3EB33333, 0x3EB851EC, 0x3EBD70A4, 0x3EC28F5C, 0x3EC7AE14, 0x3ECCCCCD, 0x3ED1EB85, 0x3ED70A3D, 0x3EDC28F6, 0x3EE147AE, 0x3EE66666, 0x3EEB851F,
        0x3EF0A3D7, 0x3EF5C28F, 0x3EFAE148, 0x3F000000, 0x3F028F5C, 0x3F051EB8, 0x3F07AE14, 0x3F0A3D71, 0x3F0CCCCD, 0x3F0F5C29, 0x3F11EB85, 0x3F147AE1,
        0x3F170A3D, 0x3F19999A, 0x3F1C28F6, 0x3F1EB852, 0x3F2147AE, 0x3F23D70A, 0x3F266666, 0x3F28F5C3, 0x3F2B851F, 0x3F2E147B, 0x3F30A3D7, 0x3F333333,
        0x3F35C28F, 0x3F3851EC, 0x3F3AE148, 0x3F3D70A4, 0x3F400000, 0x3F428F5C, 0x3F451EB8, 0x3F47AE14, 0x3F4A3D71, 0x3F4CCCCD, 0x3F4F5C29, 0x3F51EB85,
        0x3F547AE1, 0x3F570A3D, 0x3F59999A, 0x3F5C28F6, 0x3F5EB852, 0x3F6147AE, 0x3F63D70A, 0x3F666666, 0x3F68F5C3, 0x3F6B851F, 0x3F6E147B, 0x3F70A3D7,
        0x3F733333, 0x3F75C28F, 0x3F7851EC, 0x3F7AE148, 0x3F7D70A4, 0x3F7FBE77, 0x3F7FF972, 0x3F8147AE, 0x3F828F5C, 0x3F83D70A, 0x3F851EB8, 0x3F866666,
        0x3F87AE14, 0x3F88F5C3, 0x3F8A3D71, 0x3F8B851F, 0x3F8CCCCD, 0x3F99999A, 0x3FA66666, 0x3FB33333, 0x3FC00000, 0x3FCCCCCD, 0x3FD9999A, 0x3FE66666,
        0x3FF33333, 0x40200000, 0x40666666,
    ];

    private const int HeaderKeysAt = 0x18, CountAt = 0x20, StrideAt = 0x24, RangeAt = 0x28;
    private const int InfoAt = 12, ValueAt = 16, KindMask = 0x3FF, BitsShift = 13;

    public static bool IsVfx3(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual("BFFH"u8);

    private readonly record struct Channel(string Emitter, string Renderer, string Name, long Record, int Kind);

    private static bool IsThreeOfOne(int kind) => kind is >= Float3Whole and < Float3OneInFile + Float3Patterns;

    // The kind a three-number kind's value is stored by, without its pattern.
    private static int FormOf(int kind) => Float3Whole + (kind - Float3Whole) / Float3Patterns * Float3Patterns;

    // The one number a kind with one value holds, or null if it holds something else.
    private static float? OneValue(byte[] d, long record, int kind)
    {
        int form = IsThreeOfOne(kind) ? FormOf(kind) : kind;
        uint bits = (U32(d, record + InfoAt) >> BitsShift) & 0xFFFF;
        switch (form)
        {
            case FloatInFile or Float3OneInFile:
                return F32(d, record + ValueAt);
            case FloatWhole or Float3Whole:
                return (short)bits;
            case FloatFromTable or Float3FromTable:
                return bits < CommonNumbers.Length
                    ? BitConverter.UInt32BitsToSingle(CommonNumbers[bits])
                    : throw new InvalidDataException($"A particle channel asks for common number {bits}, past the end of the table.");
            default:
                return null;
        }
    }

    private static double[] ThreeOfOne(int kind, float value)
    {
        bool[] pattern = Patterns[kind - FormOf(kind)];
        return [pattern[0] ? value : 0, pattern[1] ? value : 0, pattern[2] ? value : 0];
    }

    private static void SetKindAndBits(byte[] d, long record, int kind, uint? bits)
    {
        uint info = U32(d, record + InfoAt);
        info = (info & ~(uint)KindMask) | (uint)kind;
        if (bits is { } value) info = (info & ~(0xFFFFu << BitsShift)) | (value << BitsShift);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan((int)record + InfoAt), info);
    }

    // Stores one number by the kind's own form when it can, or by the other form that lives in the record's bits. A form
    // that has only the bits has no room for a number of any other shape.
    private static void PutOne(byte[] d, Channel c, float value, int pattern, string what)
    {
        bool threeNumbers = IsThreeOfOne(c.Kind);
        int form = threeNumbers ? FormOf(c.Kind) : c.Kind;
        int whole = threeNumbers ? Float3Whole : FloatWhole, table = threeNumbers ? Float3FromTable : FloatFromTable;

        if (form is FloatInFile or Float3OneInFile)
        {
            PutF32(d, c.Record + ValueAt, value);
            SetKindAndBits(d, c.Record, form + pattern, null);
            return;
        }

        int common = Array.IndexOf(CommonNumbers, BitConverter.SingleToUInt32Bits(value));
        bool isWhole = value == MathF.Truncate(value) && value is >= short.MinValue and <= short.MaxValue;
        if (form == whole && isWhole || form == table && common < 0 && isWhole)
            SetKindAndBits(d, c.Record, whole + pattern, (ushort)(short)value);
        else if (common >= 0)
            SetKindAndBits(d, c.Record, table + pattern, (uint)common);
        else
            throw new InvalidDataException($"{what} has no room for {PyYamlScalar.Float(value)}: this channel only holds a whole number or one of the game's common numbers, such as 0.5 or 0.75.");
    }

    private static ulong U64(byte[] d, long pos)
    {
        if (pos < 0 || pos + 8 > d.Length) throw new InvalidDataException($"The particle data ends before offset 0x{pos:x}.");
        return BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan((int)pos));
    }

    private static uint U32(byte[] d, long pos)
    {
        if (pos < 0 || pos + 4 > d.Length) throw new InvalidDataException($"The particle data ends before offset 0x{pos:x}.");
        return BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos));
    }

    private static float F32(byte[] d, long pos) => BitConverter.UInt32BitsToSingle(U32(d, pos));

    private static void PutF32(byte[] d, long pos, double value)
    {
        if (pos < 0 || pos + 4 > d.Length) throw new InvalidDataException($"The particle data ends before offset 0x{pos:x}.");
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan((int)pos), (float)value);
    }

    private static string CString(byte[] d, long pos)
    {
        if (pos <= 0 || pos >= d.Length) return "";
        int end = Array.IndexOf(d, (byte)0, (int)pos);
        return end < 0 ? "" : Encoding.UTF8.GetString(d, (int)pos, end - (int)pos);
    }

    // A file holds many emitter sets, one record each: a name, then four tables of emitters, ... The first word of the
    // section points at the VFX3 block, whose first record starts 8 bytes in; its count and record size come after.
    private const int EsetTablesAt = 8, EsetEmitterCountAt = 0x10;

    /// <summary>The emitter sets in a file, each with its emitters' names (two emitters of one name get a #2, #3 and so on).</summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Emitters)> Esets(byte[] d)
    {
        (long records, uint count, uint size) = EsetRecords(d);
        List<(string, IReadOnlyList<string>)> sets = [];
        Dictionary<string, int> setNames = [];
        for (uint i = 0; i < count; i++)
        {
            long record = records + i * (long)size;
            string name = CString(d, (long)U64(d, record));
            int seenSet = setNames.GetValueOrDefault(name);
            setNames[name] = seenSet + 1;
            sets.Add((seenSet == 0 ? name : $"{name}#{seenSet + 1}", EmitterNames(d, record)));
        }

        return sets;
    }

    private static (long Records, uint Count, uint Size) EsetRecords(byte[] d)
    {
        if (!IsVfx3(d)) throw new InvalidDataException("Not a Vfx3 particle file.");
        long section = U32(d, 0xAC);
        long block = (long)U64(d, section);
        if (block + 0x20 > d.Length || !d.AsSpan((int)block, 4).SequenceEqual("VFX3"u8))
            throw new InvalidDataException("The particle file has no VFX3 block.");

        long records = (long)U64(d, section + 0x30);
        uint count = U32(d, section + 0x38), size = U32(d, section + 0x3C);
        // A file written before this table was known has one set, at the block itself.
        if (records != block + 8 || count == 0 || size < 0x48) return (block + 8, 1, 0x68);
        return (records, count, size);
    }

    private static List<string> EmitterNames(byte[] d, long record)
    {
        List<string> names = [];
        Dictionary<string, int> seen = [];
        long emitters = (long)U64(d, record + EsetTablesAt);
        uint count = U32(d, record + EsetEmitterCountAt);
        for (uint e = 0; e < count; e++)
        {
            string name = CString(d, (long)U64(d, (long)U64(d, emitters + e * 8L)));
            int times = seen.GetValueOrDefault(name);
            seen[name] = times + 1;
            names.Add(times == 0 ? name : $"{name}#{times + 1}");
        }

        return names;
    }

    // Each colour channel of each emitter of one emitter set, in file order.
    private static List<Channel> Channels(byte[] d, int esetIndex = 0)
    {
        (long setRecords, uint setCount, uint recordSize) = EsetRecords(d);
        if (esetIndex < 0 || esetIndex >= setCount) throw new InvalidDataException($"The particle file has no emitter set number {esetIndex}.");
        long block = setRecords + esetIndex * (long)recordSize - 8;

        List<Channel> channels = [];
        Dictionary<string, int> names = [];
        long emitters = (long)U64(d, block + 0x10);
        uint emitterCount = U32(d, block + 0x18);

        for (uint e = 0; e < emitterCount; e++)
        {
            long emitter = (long)U64(d, emitters + e * 8L);
            string name = CString(d, (long)U64(d, emitter));

            Dictionary<ulong, long> records = [];
            for (int table = 0; table < 4; table++)
            {
                long at = (long)U64(d, emitter + 8 + table * 16);
                uint count = U32(d, emitter + 0x10 + table * 16);
                for (uint i = 0; i < count; i++)
                {
                    long record = (long)U64(d, at + i * 8L);
                    records[U64(d, record)] = record;
                }
            }

            // Two emitters of one name would otherwise collide in the text.
            int seen = names.GetValueOrDefault(name);
            names[name] = seen + 1;
            string key = seen == 0 ? name : $"{name}#{seen + 1}";

            long renderers = (long)U64(d, emitter + 8 + 4 * 16);
            uint rendererCount = U32(d, emitter + 0x10 + 4 * 16);
            for (uint r = 0; r < rendererCount; r++)
            {
                string renderer = CString(d, (long)U64(d, (long)U64(d, renderers + r * 8L) + 0x20));
                foreach (string channel in ChannelNames)
                {
                    if (Vfx3Hash.Of($"{name}/{renderer}/{channel}") is { } hash && records.TryGetValue(hash, out long record))
                        channels.Add(new Channel(key, renderer, channel, record, (int)U32(d, record + InfoAt) & KindMask));
                }
            }
        }

        return channels;
    }

    // ---- binary to text ----

    /// <param name="data">The particle file.</param>
    /// <param name="esetIndex">Which emitter set; the first by default.</param>
    /// <param name="onlyEmitter">When given, only that emitter (as named by <see cref="Esets"/>).</param>
    public static string ToText(byte[] data, int esetIndex = 0, string? onlyEmitter = null, TextureNames? textures = null)
    {
        StringBuilder sb = new();
        List<Channel> channels = [.. Channels(data, esetIndex).Where(c => onlyEmitter is null || c.Emitter == onlyEmitter)];
        List<Sampler> samplers = textures is null ? [] : [.. Samplers(data, esetIndex).Where(s => onlyEmitter is null || s.Emitter == onlyEmitter)];

        foreach (var emitter in channels.Select(c => (c.Emitter, c.Renderer)).Concat(samplers.Select(s => (s.Emitter, s.Renderer))).Distinct().GroupBy(k => k.Emitter))
        {
            sb.Append(PyYamlScalar.Write(emitter.Key)).Append(":\n");
            foreach (string renderer in emitter.Select(k => k.Renderer))
            {
                sb.Append("    ").Append(renderer).Append(":\n");
                foreach (Channel c in channels.Where(c => c.Emitter == emitter.Key && c.Renderer == renderer))
                {
                    sb.Append("        ").Append(c.Name).Append(':');
                    AppendValue(sb, data, c);
                }

                List<Sampler> here = [.. samplers.Where(s => s.Emitter == emitter.Key && s.Renderer == renderer)];
                if (here.Count == 0) continue;
                sb.Append("        Textures:\n");
                foreach (Sampler s in here)
                    sb.Append("            ").Append(s.Name).Append(": ").Append(PyYamlScalar.Write(textures!.NameOf(s.Guid) ?? s.Guid.ToString())).Append('\n');
            }
        }
        return sb.Length == 0 ? "{}\n" : sb.ToString();
    }

    private static string Flow(IEnumerable<double> values) => "[" + string.Join(", ", values.Select(PyYamlScalar.Float)) + "]";

    private static double[] Floats(byte[] d, long pos, int count)
    {
        double[] values = new double[count];
        for (int i = 0; i < count; i++) values[i] = F32(d, pos + i * 4L);
        return values;
    }

    private static void AppendValue(StringBuilder sb, byte[] d, Channel c)
    {
        switch (c.Kind)
        {
            case Float3InFile:
                sb.Append(' ').Append(Flow(Floats(d, c.Record + ValueAt, 3))).Append('\n');
                return;

            case FloatInFile or FloatWhole or FloatFromTable:
                sb.Append(' ').Append(PyYamlScalar.Float(OneValue(d, c.Record, c.Kind)!.Value)).Append('\n');
                return;

            case var kind when IsThreeOfOne(kind):
                sb.Append(' ').Append(Flow(ThreeOfOne(kind, OneValue(d, c.Record, kind)!.Value))).Append('\n');
                return;

            case KeyedFloat or KeyedFloat3 or ListFloat3:
            {
                long keys = (long)U64(d, c.Record + HeaderKeysAt);
                uint count = U32(d, c.Record + CountAt);
                int stride = (int)U32(d, c.Record + StrideAt);
                sb.Append('\n');
                for (uint i = 0; i < count; i++)
                {
                    long key = keys + i * (long)stride;
                    if (c.Kind == ListFloat3)
                    {
                        sb.Append("        -   color: ").Append(Flow(Floats(d, key, 3))).Append('\n');
                    }
                    else if (c.Kind == KeyedFloat3)
                    {
                        sb.Append("        -   value: ").Append(Flow(Floats(d, key, 3))).Append('\n');
                        sb.Append("            keyframe: ").Append(PyYamlScalar.Float(F32(d, key + 12))).Append('\n');
                    }
                    else
                    {
                        sb.Append("        -   value: ").Append(PyYamlScalar.Float(F32(d, key))).Append('\n');
                        sb.Append("            keyframe: ").Append(PyYamlScalar.Float(F32(d, key + 4))).Append('\n');
                    }
                }
                return;
            }

            default:
                // Set by something this doesn't write (an expression, or a kind of key the colours don't use here).
                sb.Append(" not_editable\n");
                return;
        }
    }

    // ---- text back into the binary ----

    private static double Number(Byml? node, string what)
        => node?.AsNumber() ?? throw new InvalidDataException($"{what} needs a number.");

    private static double[] Numbers(Byml? node, int count, string what)
    {
        if (node is not { IsArray: true } || node.Count != count) throw new InvalidDataException($"{what} needs {count} numbers.");
        double[] values = new double[count];
        for (int i = 0; i < count; i++) values[i] = Number(node[i], what);
        return values;
    }

    /// <summary>Applies edited <paramref name="yaml"/> (as made by <see cref="ToText"/>) to the binary and returns the new bytes.</summary>
    public static byte[] ApplyText(byte[] original, string yaml, int esetIndex = 0, TextureNames? textures = null)
    {
        Byml changes = Byml.FromYaml(yaml);
        if (!changes.IsMap) throw new InvalidDataException("Invalid particle YAML: " + yaml);

        byte[] data = (byte[])original.Clone();
        foreach (Channel c in Channels(data, esetIndex))
        {
            if (changes[c.Emitter] is not { IsMap: true } emitter || emitter[c.Renderer] is not { IsMap: true } renderer) continue;
            if (renderer[c.Name] is not { } edit) continue;

            string what = $"{c.Emitter}/{c.Renderer}/{c.Name}";
            switch (c.Kind)
            {
                case Float3InFile:
                {
                    double[] values = Numbers(edit, 3, what);
                    for (int i = 0; i < 3; i++) PutF32(data, c.Record + ValueAt + i * 4, values[i]);
                    break;
                }
                case FloatInFile or FloatWhole or FloatFromTable:
                {
                    float value = (float)Number(edit, what);
                    if (!SameBits(value, OneValue(data, c.Record, c.Kind)!.Value)) PutOne(data, c, value, 0, what);
                    break;
                }
                case var kind when IsThreeOfOne(kind):
                    PutThreeOfOne(data, c, Numbers(edit, 3, what), what);
                    break;
                case KeyedFloat or KeyedFloat3 or ListFloat3:
                    PutKeys(data, c, edit, what);
                    break;
            }
        }

        if (textures is not null) ApplyTextures(data, changes, esetIndex, textures);
        return data;
    }

    // ---- textures ----

    /// <summary>One texture slot of an emitter's renderer: the sampler's name, the texture's guid, and where the guid's text pointer is stored.</summary>
    private readonly record struct Sampler(string Emitter, string Renderer, string Name, ulong Guid, long Slot);

    private static ulong? ParseGuid(string text) => ulong.TryParse(text, System.Globalization.NumberStyles.None, null, out ulong guid) ? guid : null;

    // The texture slots of each renderer of each emitter of one emitter set. A renderer keeps its samplers' names, guids (as
    // decimal text) and name hashes in three parallel arrays.
    private static List<Sampler> Samplers(byte[] d, int esetIndex)
    {
        (long setRecords, uint setCount, uint recordSize) = EsetRecords(d);
        if (esetIndex < 0 || esetIndex >= setCount) return [];
        long block = setRecords + esetIndex * (long)recordSize - 8;

        List<Sampler> samplers = [];
        Dictionary<string, int> names = [];
        long emitters = (long)U64(d, block + 0x10);
        uint emitterCount = U32(d, block + 0x18);
        for (uint e = 0; e < emitterCount; e++)
        {
            long emitter = (long)U64(d, emitters + e * 8L);
            string name = CString(d, (long)U64(d, emitter));
            int seen = names.GetValueOrDefault(name);
            names[name] = seen + 1;
            string key = seen == 0 ? name : $"{name}#{seen + 1}";

            long renderers = (long)U64(d, emitter + 8 + 4 * 16);
            uint rendererCount = U32(d, emitter + 0x10 + 4 * 16);
            for (uint r = 0; r < rendererCount; r++)
            {
                long renderer = (long)U64(d, renderers + r * 8L);
                string rendererName = CString(d, (long)U64(d, renderer + 0x20));
                long nameArray = (long)U64(d, renderer + 0x50), guidArray = (long)U64(d, renderer + 0x58);
                uint count = U32(d, renderer + 0x68);
                if (count > 32 || nameArray <= 0 || guidArray <= 0) continue;
                for (uint i = 0; i < count; i++)
                {
                    long slot = guidArray + i * 8L;
                    if (ParseGuid(CString(d, (long)U64(d, slot))) is not { } guid) continue;
                    samplers.Add(new Sampler(key, rendererName, CString(d, (long)U64(d, nameArray + i * 8L)), guid, slot));
                }
            }
        }
        return samplers;
    }

    private static void ApplyTextures(byte[] data, Byml changes, int esetIndex, TextureNames textures)
    {
        foreach (Sampler s in Samplers(data, esetIndex))
        {
            if (changes[s.Emitter] is not { IsMap: true } emitter || emitter[s.Renderer] is not { IsMap: true } renderer) continue;
            if (renderer["Textures"] is not { IsMap: true } list || list[s.Name]?.AsString() is not { } wanted) continue;
            if (wanted == (textures.NameOf(s.Guid) ?? s.Guid.ToString())) continue;

            string what = $"{s.Emitter}/{s.Renderer}/Textures/{s.Name}";
            ulong guid = textures.Guid(wanted, s.Guid, data) ?? throw new InvalidDataException($"{what} names the texture '{wanted}', which this file does not list and cannot be added from the texture it replaces.");
            long text = FindText(data, guid.ToString()) ?? throw new InvalidDataException($"{what}: the particle data has no text for the new texture's id.");
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan((int)s.Slot), (ulong)text);
        }
    }

    // Where a number's decimal text is in the file. The text may be the end of a longer number's text, since a string ends at its 0.
    internal static long? FindText(byte[] d, string digits)
    {
        byte[] needle = [.. Encoding.ASCII.GetBytes(digits), (byte)0];
        int from = 0;
        while (from < d.Length)
        {
            int at = d.AsSpan(from).IndexOf(needle);
            if (at < 0) return null;
            at += from;
            if (at > 0 && (d[at - 1] == 0 || d[at - 1] is >= (byte)'0' and <= (byte)'9')) return at;
            from = at + 1;
        }
        return null;
    }

    /// <summary>
    /// The textures a particle file lists, by id: the <c>Textures</c> of its <c>.vsetb.byml</c>. A texture added here (named
    /// for a clone of one the file already uses) gets an id that is the end of an existing texture's id text, so the id
    /// has text in the file without anything in it moving.
    /// </summary>
    public sealed class TextureNames
    {
        private readonly Dictionary<ulong, string> _names = [];
        private readonly Dictionary<string, ulong> _guids = new(StringComparer.Ordinal);

        public List<(ulong Guid, string Name)> Added { get; } = [];

        public static TextureNames From(Byml? textures)
        {
            TextureNames names = new();
            if (textures is { IsArray: true })
                foreach (Byml texture in textures.AsArray)
                    if (texture["guid"] is { Type: BymlType.UInt64 } guid && texture["name"]?.AsString() is { } name)
                    {
                        names._names[guid.UInt64] = name;
                        names._guids[name] = guid.UInt64;
                    }
            return names;
        }

        public string? NameOf(ulong guid) => _names.GetValueOrDefault(guid);

        /// <summary>The id of a texture by name. A name the file does not list is added as a new texture, with an id taken from the end of <paramref name="from"/>'s text.</summary>
        internal ulong? Guid(string name, ulong from, byte[] data)
        {
            if (_guids.TryGetValue(name, out ulong known)) return known;

            string digits = from.ToString();
            for (int cut = 1; cut < digits.Length - 8; cut++)
            {
                if (digits[cut] == '0') continue;
                ulong candidate = ulong.Parse(digits[cut..], System.Globalization.CultureInfo.InvariantCulture);
                if (_names.ContainsKey(candidate)) continue;
                _names[candidate] = name;
                _guids[name] = candidate;
                Added.Add((candidate, name));
                return candidate;
            }
            return null;
        }
    }

    [ThreadStatic]
    private static bool _fitting;

    private static bool Fitting => _fitting;

    /// <summary>
    /// While the returned scope is open, a colour a channel cannot hold exactly (one number shared by all three) is
    /// stored as the nearest colour it can, instead of being refused. Used by recipes, which colour whole files at once.
    /// </summary>
    public static IDisposable FitToStorage()
    {
        bool before = _fitting;
        _fitting = true;
        return new Scope(() => _fitting = before);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private static bool SameBits(float a, float b) => BitConverter.SingleToUInt32Bits(a) == BitConverter.SingleToUInt32Bits(b);

    private static void PutThreeOfOne(byte[] d, Channel c, double[] values, string what)
    {
        double[] current = ThreeOfOne(c.Kind, OneValue(d, c.Record, c.Kind)!.Value);
        if (Enumerable.Range(0, 3).All(i => SameBits((float)values[i], (float)current[i]))) return;

        // The record holds one number, so every channel that isn't 0 has to share it.
        float[] used = [.. values.Select(v => (float)v).Where(v => v != 0).Distinct()];
        if (used.Length > 1 && Fitting)
        {
            // Nearest colour the record can hold: the brightest channel's number on every channel that is at least half as bright, 0 on the others.
            double top = values.Max();
            values = [.. values.Select(v => v >= top / 2 ? top : 0)];
            used = [(float)top];
        }

        if (used.Length > 1)
            throw new InvalidDataException($"{what} holds one number across its channels, so the ones that aren't 0 must match, for example [1, 0, 0] for red. The file has no room for three different numbers here.");

        // All 0 fits any pattern, so it keeps the one it had.
        int pattern = used.Length == 0
            ? c.Kind - FormOf(c.Kind)
            : Array.FindIndex(Patterns, p => p[0] == (values[0] != 0) && p[1] == (values[1] != 0) && p[2] == (values[2] != 0));
        PutOne(d, c, used.Length == 0 ? 0f : used[0], pattern, what);
    }

    private static void PutKeys(byte[] d, Channel c, Byml edit, string what)
    {
        long keys = (long)U64(d, c.Record + HeaderKeysAt);
        int count = (int)U32(d, c.Record + CountAt);
        int stride = (int)U32(d, c.Record + StrideAt);
        if (!edit.IsArray || edit.Count != count)
            throw new InvalidDataException($"{what} has {count} keys, and adding or removing keys isn't supported.");

        int components = c.Kind == KeyedFloat ? 1 : 3;
        double[] highest = new double[components], lowest = new double[components];
        Array.Fill(highest, double.NegativeInfinity);
        Array.Fill(lowest, double.PositiveInfinity);

        bool changed = false;
        for (int i = 0; i < count; i++)
        {
            Byml frame = edit[i]!;
            long key = keys + i * (long)stride;
            double[] value = c.Kind switch
            {
                KeyedFloat => [Number(frame["value"], what + " value")],
                ListFloat3 => Numbers(frame["color"], 3, what + " color"),
                _ => Numbers(frame["value"], 3, what + " value"),
            };

            for (int v = 0; v < components; v++)
            {
                changed |= BitConverter.SingleToUInt32Bits((float)value[v]) != U32(d, key + v * 4L);
                PutF32(d, key + v * 4L, value[v]);
                highest[v] = Math.Max(highest[v], (float)value[v]);
                lowest[v] = Math.Min(lowest[v], (float)value[v]);
            }

            if (c.Kind != ListFloat3)
            {
                double keyframe = Number(frame["keyframe"], what + " keyframe");
                long at = key + components * 4L;
                changed |= BitConverter.SingleToUInt32Bits((float)keyframe) != U32(d, at);
                PutF32(d, at, keyframe);
            }
        }

        // Before the keys the file keeps each component's highest value and then its lowest.
        if (!changed) return;
        for (int v = 0; v < components; v++)
        {
            PutF32(d, c.Record + RangeAt + v * 4L, highest[v]);
            PutF32(d, c.Record + RangeAt + (components + v) * 4L, lowest[v]);
        }
    }
}

/// <summary>The 64-bit name hash Vfx3 uses, a FarmHash-style hash, for names up to 256 bytes.</summary>
public static class Vfx3Hash
{
    private const ulong K0 = 0xc3a5c85c97cb3127, K1 = 0xb492b66fbe98f273, K2 = 0x9ae16a3b2f90404f;
    private const ulong K33 = K2 - 0x1E, K65 = K2 - 0x72;

    private static ulong R(ulong x, int r) => ulong.RotateRight(x, r);

    private static ulong Mix(ReadOnlySpan<byte> b, int at, ulong last, ulong third, ulong mul)
    {
        ulong first = U(b, at) * K1, second = U(b, at + 8);
        ulong a = last + first + R(second + K2, 18);
        ulong c = ((R(last, 30) + third * K2 + R(second + first, 43)) ^ a) * mul;
        return (a ^ (c >> 47) ^ c) * mul;
    }

    private static ulong U(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b[at..]);

    private static ulong V(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);

    /// <summary>The hash of a name, or null if it is too long for this port.</summary>
    public static ulong? Of(string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        return Of(bytes);
    }

    public static ulong? Of(ReadOnlySpan<byte> b)
    {
        unchecked
        {
            int n = b.Length;
            ulong length = (ulong)n;

            if (n <= 16)
            {
                if (n >= 8)
                {
                    ulong mul = K2 + length * 2, tail = U(b, n - 8), head = U(b, 0) + K2;
                    ulong x = (tail + R(head, 25)) * mul;
                    ulong y = ((head + R(tail, 37) * mul) ^ x) * mul;
                    y = (x ^ (y >> 47) ^ y) * mul;
                    return (y ^ (y >> 47)) * mul;
                }
                if (n >= 4)
                {
                    ulong mul = K2 + length * 2, tail = V(b, n - 4);
                    ulong x = (tail ^ (length | (V(b, 0) << 3))) * mul;
                    ulong y = (tail ^ (x >> 47) ^ x) * mul;
                    return (y ^ (y >> 47)) * mul;
                }
                if (n > 0)
                {
                    ulong v = ((length | ((ulong)b[n - 1] << 2)) * K0) ^ ((((ulong)b[n >> 1] << 8) | b[0]) * K2);
                    return (v ^ (v >> 47)) * K2;
                }
                return K2;
            }

            if (n <= 32)
            {
                ulong mul = K2 + length * 2;
                ulong v = Mix(b, 0, U(b, n - 8) * mul, U(b, n - 16), mul);
                return (v ^ (v >> 47)) * mul;
            }

            if (n <= 64)
            {
                ulong mul = K33 + length * 2;
                ulong first = Mix(b, 0, U(b, 24) * K33, U(b, 16), K33);
                ulong second = Mix(b, n - 32, U(b, n - 8) * mul, U(b, n - 16), mul);
                return ((first ^ (first >> 47)) + (second ^ (second >> 47)) * mul) * mul;
            }

            if (n <= 96)
            {
                ulong mul = K65 + length * 2;
                ulong first = Mix(b, 0, U(b, 24) * K65, U(b, 16), K65);
                ulong second = Mix(b, 32, U(b, 56) * mul, U(b, 48), mul);
                ulong last = U(b, n - 8) * mul, low = U(b, n - 32) * K1, mid = U(b, n - 24);
                ulong a = last + low + R(mid + K2, 18) + (second ^ (second >> 47));
                ulong c = (a ^ (R(last, 30) + (first ^ (first >> 47)) + U(b, n - 16) * K2 + R(mid + low, 43))) * mul;
                c = (a ^ (c >> 47) ^ c) * mul;
                return ((second >> 21) + (first >> 17) + (c ^ (c >> 47)) * 9) * mul;
            }

            if (n <= 256) return Long(b);
            return null;
        }
    }

    private static ulong Long(ReadOnlySpan<byte> b)
    {
        unchecked
        {
            int n = b.Length, p = 0, end = (n - 1) & ~0x3F;
            ulong s24 = 0, s18 = 0, s20 = 0, s23 = 0, s29 = 0x226bb95b4e64b6d4, s11 = 0x134a747f856d0526;
            ulong s16 = U(b, 0) + 0x1529cba0ca458ff;

            do
            {
                ulong w0 = U(b, p), w8 = U(b, p + 8), w16 = U(b, p + 16), w24 = U(b, p + 24);
                ulong w32 = U(b, p + 32), w40 = U(b, p + 40), w48 = U(b, p + 48), w56 = U(b, p + 56);

                ulong a = s18 + s16;
                s16 = R(s23 + s11, 33) * K1;
                ulong c = s29 + s24 + w48;
                ulong d = a + s29 + w8;
                s24 = w0 + s24 * K1;
                ulong e = s16 + s20 + w32;
                s29 = w40 + s18 + R(c, 42) * K1;
                s11 = R(d, 37) * K1 ^ s20;
                s20 = w8 + w16 + s24;
                s18 = w24 + s20;
                ulong f = s24 + s23 + w24 + s11;
                ulong g = w40 + w48 + e;
                s23 = w56 + g;
                s24 = R(s20, 44) + s24 + R(f, 21);
                s20 = e + w16 + w56 + s29;
                s20 = R(g, 44) + e + R(s20, 21);
                p += 64;
            } while (p != end);

            int t = (n - 1) & 0x3F;
            ulong m = (s11 & 0xFF) * 2 + K1, ts = (ulong)t;
            ulong a10 = U(b, end + t - 7), a12 = U(b, end + t - 0xF), a13 = U(b, end + t - 0x17);
            ulong a14 = U(b, end + t - 0x27), a15 = U(b, end + t - 0x2F), a27 = U(b, end + t - 0x37);

            s18 = s23 + ts + s18;
            s23 = s18 + s23 + ts;
            ulong u17 = s29 + s24 + a12;
            s24 = U(b, end + t - 0x3F) + s24 * m;
            ulong u21 = s29 + s16 + a27 + s18;
            ulong u22 = a27 + a15 + s24;
            ulong u19 = a13 + s18 * 9 + R(u17, 42) * m;
            s11 = s23 + s11;
            ulong u26 = R(s11, 33);
            ulong u28 = R(u21, 37) * m ^ s20 * 9;
            s16 = s20 + u26 * m + U(b, end + t - 0x1F);
            s20 = s24 + s23 + u28 + a14;
            s11 = a13 + a12 + s16;
            u17 = a10 + s11;
            u21 = s16 + a15 + u19 + a10;
            s11 = R(s11, 44) + s16 + R(u21, 21);
            u21 = (u17 ^ (a14 + u22)) * m;
            u17 = (u17 ^ (u21 >> 47) ^ u21) * m;
            s20 = (s11 ^ (R(u22, 44) + s24 + R(s20, 21))) * m;
            s20 = (s11 ^ (s20 >> 47) ^ s20) * m;
            s20 = ((s20 ^ (s20 >> 47)) + u26) * m;
            s11 = (s20 ^ (u28 + (u19 ^ (u19 >> 47)) * K0 + (u17 ^ (u17 >> 47)) * m)) * m;
            s20 = (s20 ^ (s11 >> 47) ^ s11) * m;
            return (s20 ^ (s20 >> 47)) * m;
        }
    }
}

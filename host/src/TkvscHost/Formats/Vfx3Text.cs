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

    // Channel kinds, the low byte of the record's second u32.
    private const byte ConstantScalar = 0x00, ConstantGray = 0x22, ConstantFloat4 = 0x0D;
    private const byte KeyedFloat = 0x45, ListFloat3 = 0x50, KeyedFloat3 = 0x51;

    private const int HeaderKeysAt = 0x18, CountAt = 0x20, StrideAt = 0x24, RangeAt = 0x28;

    public static bool IsVfx3(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual("BFFH"u8);

    private readonly record struct Channel(string Emitter, string Renderer, string Name, long Record, byte Kind);

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

    // Each colour channel of each emitter, in file order.
    private static List<Channel> Channels(byte[] d)
    {
        if (!IsVfx3(d)) throw new InvalidDataException("Not a Vfx3 particle file.");

        long section = U32(d, 0xAC);
        long block = (long)U64(d, section);
        if (block + 0x20 > d.Length || !d.AsSpan((int)block, 4).SequenceEqual("VFX3"u8))
            throw new InvalidDataException("The particle file has no VFX3 block.");

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
                        channels.Add(new Channel(key, renderer, channel, record, (byte)U32(d, record + 12)));
                }
            }
        }

        return channels;
    }

    // ---- binary to text ----

    public static string ToText(byte[] data)
    {
        StringBuilder sb = new();
        string emitter = "", renderer = "";
        foreach (Channel c in Channels(data))
        {
            if (c.Emitter != emitter)
            {
                emitter = c.Emitter;
                renderer = "";
                sb.Append(PyYamlScalar.Write(emitter)).Append(":\n");
            }
            if (c.Renderer != renderer)
            {
                renderer = c.Renderer;
                sb.Append("    ").Append(renderer).Append(":\n");
            }

            sb.Append("        ").Append(c.Name).Append(':');
            AppendValue(sb, data, c);
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
            case ConstantFloat4:
                sb.Append(' ').Append(Flow(Floats(d, c.Record + 16, 4))).Append('\n');
                return;

            case ConstantScalar or ConstantGray:
                sb.Append(' ').Append(PyYamlScalar.Float(F32(d, c.Record + 16))).Append('\n');
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
                // Set by something this doesn't read (a default, a random range, an expression), so it can't be edited here.
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
    public static byte[] ApplyText(byte[] original, string yaml)
    {
        Byml changes = Byml.FromYaml(yaml);
        if (!changes.IsMap) throw new InvalidDataException("Invalid particle YAML: " + yaml);

        byte[] data = (byte[])original.Clone();
        foreach (Channel c in Channels(data))
        {
            if (changes[c.Emitter] is not { IsMap: true } emitter || emitter[c.Renderer] is not { IsMap: true } renderer) continue;
            if (renderer[c.Name] is not { } edit) continue;

            string what = $"{c.Emitter}/{c.Renderer}/{c.Name}";
            switch (c.Kind)
            {
                case ConstantFloat4:
                {
                    double[] values = Numbers(edit, 4, what);
                    for (int i = 0; i < 4; i++) PutF32(data, c.Record + 16 + i * 4, values[i]);
                    break;
                }
                case ConstantScalar or ConstantGray:
                    PutF32(data, c.Record + 16, Number(edit, what));
                    break;
                case KeyedFloat or KeyedFloat3 or ListFloat3:
                    PutKeys(data, c, edit, what);
                    break;
            }
        }
        return data;
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

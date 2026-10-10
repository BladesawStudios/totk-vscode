using System.Buffers.Binary;
using System.Text;
using BymlSharp;

namespace TkvscHost.Formats;

/// <summary>
/// The colours and colour animations of the emitters in a particle binary (the <c>PtclBin</c> of a BYML), as YAML to edit
/// and back. Port of vendor/ptcl/ptcl.py and python/ptcl_io.py: only these fields are read or written; the rest of the
/// binary is kept as it was.
/// </summary>
public static class PtclText
{
    private const uint None = 0xFFFFFFFF;

    private readonly record struct Header(string Signature, uint SubsectionOffset, uint NextSectionOffset, uint SectionOffset);

    private sealed class Emitter
    {
        public double[] Const0 = [], Const1 = [];
        public List<double[]> ColorAnim0 = [], AlphaAnim0 = [], ColorAnim1 = [], AlphaAnim1 = [];
    }

    private static uint U32(byte[] d, long pos)
    {
        if (pos < 0 || pos + 4 > d.Length) throw new InvalidDataException($"The particle data ends before offset 0x{pos:x}.");
        return BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos));
    }

    private static float F32(byte[] d, long pos) => BitConverter.UInt32BitsToSingle(U32(d, pos));

    private static Header ReadHeader(byte[] d, long pos)
    {
        if (pos < 0 || pos + 0x20 > d.Length) throw new InvalidDataException($"The particle data ends before offset 0x{pos:x}.");
        return new Header(Encoding.UTF8.GetString(d, (int)pos, 4), U32(d, pos + 8), U32(d, pos + 12), U32(d, pos + 20));
    }

    private static string ReadString(byte[] d, long pos, int size)
    {
        if (pos < 0 || pos + size > d.Length) throw new InvalidDataException($"The particle data ends before offset 0x{pos:x}.");
        int end = Array.IndexOf(d, (byte)0, (int)pos, size);
        int length = end < 0 ? size : end - (int)pos;
        return Encoding.UTF8.GetString(d, (int)pos, length);
    }

    // The first emitter set, found by walking the section chain from the file header.
    private static long FirstEmitterSet(byte[] d)
    {
        int start = d.AsSpan().IndexOf("VFXB    "u8);
        if (start < 0) throw new InvalidDataException("Failed to find file magic");
        if (start + 0x18 > d.Length || d[start + 9] != 4 || BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(start + 10)) != 0x33
            || BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(start + 12)) != 0xFEFF)
            throw new InvalidDataException("File is not valid");

        long pos = start + BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(start + 0x16));
        Header header = ReadHeader(d, pos);
        while (header.Signature != "ESTA")
        {
            if (header.NextSectionOffset == None) throw new InvalidDataException("No emitter sets found");
            pos += header.NextSectionOffset;
            header = ReadHeader(d, pos);
        }
        return pos + header.SectionOffset;
    }

    // Calls visit for every emitter: its set's name, its own name and the offset of its body.
    private static void Walk(byte[] d, Action<string, string, long> visit)
    {
        long setPos = FirstEmitterSet(d);
        while (true)
        {
            Header set = ReadHeader(d, setPos);
            if (set.Signature != "ESET") throw new InvalidDataException("Invalid emitter set signature");
            string setName = ReadString(d, setPos + set.SectionOffset + 0x10, 0x60);

            if (set.SubsectionOffset != None)
            {
                long emitterPos = setPos + set.SubsectionOffset;
                while (true)
                {
                    Header emitter = ReadHeader(d, emitterPos);
                    if (emitter.Signature != "EMTR") throw new InvalidDataException("Invalid emitter signature");
                    long body = emitterPos + emitter.SectionOffset;
                    visit(setName, ReadString(d, body + 0x10, 0x60), body);
                    if (emitter.NextSectionOffset == None) break;
                    emitterPos += emitter.NextSectionOffset;
                }
            }

            if (set.NextSectionOffset == None) break;
            setPos += set.NextSectionOffset;
        }
    }

    private static double[] ReadFloats(byte[] d, long pos, int count)
    {
        double[] values = new double[count];
        for (int i = 0; i < count; i++) values[i] = F32(d, pos + i * 4);
        return values;
    }

    private static List<double[]> ReadAnim(byte[] d, long pos, uint count)
    {
        if (count > 8) count = 7;
        List<double[]> frames = [];
        for (int i = 0; i <= count; i++) frames.Add(ReadFloats(d, pos + i * 16, 4));
        return frames;
    }

    // ---- binary to text ----

    public static string ToText(byte[] data, bool compact = true)
    {
        // Insertion-ordered, a repeated name replacing the earlier value in place, as a Python dict does.
        List<string> setOrder = [];
        Dictionary<string, (List<string> Order, Dictionary<string, Emitter> Emitters)> sets = [];

        Walk(data, (setName, name, body) =>
        {
            if (!sets.TryGetValue(setName, out var set))
            {
                set = ([], []);
                sets[setName] = set;
                setOrder.Add(setName);
            }

            Emitter emitter = new()
            {
                Const0 = ReadFloats(data, body + 0xF48, 4),
                Const1 = ReadFloats(data, body + 0xF48 + 16, 4),
            };
            uint color0 = U32(data, body + 0x80), alpha0 = U32(data, body + 0x84), color1 = U32(data, body + 0x88), alpha1 = U32(data, body + 0x8C);
            emitter.ColorAnim0 = ReadAnim(data, body + 0x680, color0);
            emitter.AlphaAnim0 = ReadAnim(data, body + 0x680 + 0x80, alpha0);
            emitter.ColorAnim1 = ReadAnim(data, body + 0x680 + 0x100, color1);
            emitter.AlphaAnim1 = ReadAnim(data, body + 0x680 + 0x180, alpha1);

            if (!set.Emitters.ContainsKey(name)) set.Order.Add(name);
            set.Emitters[name] = emitter;
        });

        StringBuilder sb = new();
        foreach (string setName in setOrder)
        {
            var set = sets[setName];
            sb.Append(PyYamlScalar.Write(setName)).Append(':');
            if (set.Order.Count == 0) { sb.Append(" {}\n"); continue; }
            sb.Append('\n');
            foreach (string name in set.Order)
            {
                Emitter e = set.Emitters[name];
                sb.Append("    ").Append(PyYamlScalar.Write(name)).Append(":\n");
                AppendVector(sb, "const_color0", e.Const0, compact);
                AppendVector(sb, "const_color1", e.Const1, compact);
                AppendAnim(sb, "color_anim0", e.ColorAnim0, compact);
                AppendAnim(sb, "alpha_anim0", e.AlphaAnim0, compact);
                AppendAnim(sb, "color_anim1", e.ColorAnim1, compact);
                AppendAnim(sb, "alpha_anim1", e.AlphaAnim1, compact);
            }
        }
        return sb.Length == 0 ? "{}\n" : sb.ToString();
    }

    private static string Flow(IEnumerable<double> values) => "[" + string.Join(", ", values.Select(PyYamlScalar.Float)) + "]";

    // Key at indent 8. The expanded form is a block sequence of numbers, as the plain PyYAML dumper writes it.
    private static void AppendVector(StringBuilder sb, string key, double[] values, bool compact)
    {
        if (compact)
        {
            sb.Append("        ").Append(key).Append(": ").Append(Flow(values)).Append('\n');
            return;
        }
        sb.Append("        ").Append(key).Append(":\n");
        foreach (double v in values) sb.Append("        - ").Append(PyYamlScalar.Float(v)).Append('\n');
    }

    private static void AppendAnim(StringBuilder sb, string key, List<double[]> frames, bool compact)
    {
        if (frames.Count == 0)
        {
            sb.Append("        ").Append(key).Append(": []\n");
            return;
        }
        sb.Append("        ").Append(key).Append(":\n");
        foreach (double[] frame in frames)
        {
            if (compact)
            {
                sb.Append("        -   value: ").Append(Flow(frame.Take(3))).Append('\n');
            }
            else
            {
                sb.Append("        -   value:\n");
                foreach (double v in frame.Take(3)) sb.Append("            - ").Append(PyYamlScalar.Float(v)).Append('\n');
            }
            sb.Append("            keyframe: ").Append(PyYamlScalar.Float(frame[3])).Append('\n');
        }
    }

    // ---- text back into the binary ----

    private static double[] Numbers(Byml node, int count, string what)
    {
        if (!node.IsArray || node.Count < count) throw new InvalidDataException($"{what} needs {count} numbers.");
        double[] values = new double[count];
        for (int i = 0; i < count; i++)
            values[i] = node[i]?.AsNumber() ?? throw new InvalidDataException($"{what} has something that isn't a number.");
        return values;
    }

    private static void PutFloat(byte[] d, long pos, double value)
    {
        if (pos < 0 || pos + 4 > d.Length) throw new InvalidDataException($"The particle data ends before offset 0x{pos:x}.");
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan((int)pos), (float)value);
    }

    private static void PutAnim(byte[] d, long pos, Byml frames, string key)
    {
        int count = Math.Min(frames.Count, 8);
        for (int i = 0; i < count; i++)
        {
            Byml frame = frames[i]!;
            double[] value = Numbers(frame["value"] ?? throw new InvalidDataException($"{key} needs a value for every key."), 3, key + " value");
            double keyframe = frame["keyframe"]?.AsNumber() ?? throw new InvalidDataException($"{key} needs a keyframe for every key.");
            for (int c = 0; c < 3; c++) PutFloat(d, pos + i * 16 + c * 4, value[c]);
            PutFloat(d, pos + i * 16 + 12, keyframe);
        }
    }

    private static uint AnimCount(Byml frames, string key)
    {
        if (!frames.IsArray || frames.Count == 0) throw new InvalidDataException($"{key} needs at least one key.");
        return (uint)Math.Min(frames.Count - 1, 8);
    }

    /// <summary>Applies edited <paramref name="yaml"/> (as made by <see cref="ToText"/>) to the binary and returns the new bytes.</summary>
    public static byte[] ApplyText(byte[] original, string yaml)
    {
        Byml changes = Byml.FromYaml(yaml);
        if (!changes.IsMap) throw new InvalidDataException("Invalid particle YAML: " + yaml);

        byte[] data = (byte[])original.Clone();
        Walk(data, (setName, name, body) =>
        {
            if (changes[setName] is not { IsMap: true } set || set[name] is not { IsMap: true } edit) return;

            Byml Field(string key) => edit[key] ?? throw new InvalidDataException($"Emitter '{name}' has no {key}.");
            double[] c0 = Numbers(Field("const_color0"), 4, "const_color0"), c1 = Numbers(Field("const_color1"), 4, "const_color1");
            for (int i = 0; i < 4; i++)
            {
                PutFloat(data, body + 0xF48 + i * 4, c0[i]);
                PutFloat(data, body + 0xF48 + 16 + i * 4, c1[i]);
            }

            string[] keys = ["color_anim0", "alpha_anim0", "color_anim1", "alpha_anim1"];
            for (int k = 0; k < 4; k++)
            {
                Byml frames = Field(keys[k]);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)(body + 0x80 + k * 4)), AnimCount(frames, keys[k]));
                PutAnim(data, body + 0x680 + k * 0x80, frames, keys[k]);
            }
        });
        return data;
    }
}

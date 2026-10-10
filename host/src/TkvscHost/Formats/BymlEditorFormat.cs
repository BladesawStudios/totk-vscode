using System.Globalization;
using System.Text;
using BymlSharp;

namespace TkvscHost.Formats;

/// <summary>Python's <c>repr(float)</c>: the shortest digits that read back the same value.</summary>
public static class PyFloat
{
    public static string Repr(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";
        if (value == 0) return double.IsNegative(value) ? "-0.0" : "0.0";

        string text = value.ToString("R", CultureInfo.InvariantCulture);
        bool negative = text[0] == '-';
        if (negative) text = text[1..];

        int exponent = 0;
        int e = text.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
            text = text[..e];
        }

        int point = text.IndexOf('.');
        string digits = point >= 0 ? text.Remove(point, 1) : text;
        int decimalPoint = (point >= 0 ? point : text.Length) + exponent;

        // Leading zeros move the point; trailing zeros are not digits.
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..];
        decimalPoint -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";

        string body;
        if (decimalPoint <= -4 || decimalPoint > 16)
        {
            int exp10 = decimalPoint - 1;
            body = digits.Length > 1 ? $"{digits[0]}.{digits[1..]}" : digits;
            body += "e" + (exp10 < 0 ? "-" : "+") + Math.Abs(exp10).ToString("00", CultureInfo.InvariantCulture);
        }
        else if (decimalPoint <= 0)
        {
            body = "0." + new string('0', -decimalPoint) + digits;
        }
        else if (decimalPoint >= digits.Length)
        {
            body = digits + new string('0', decimalPoint - digits.Length) + ".0";
        }
        else
        {
            body = digits[..decimalPoint] + "." + digits[decimalPoint..];
        }

        return negative ? "-" + body : body;
    }
}

/// <summary>
/// The text a BYML shows in the editor: block YAML with small containers on one line.
/// Port of <c>byml_editor_format.py</c>; it reads back with BymlSharp (and oead).
/// </summary>
public sealed class BymlEditorFormat(int inlineMaxCount)
{
    public static string ToEditorText(Byml document, int inlineMaxCount = 1)
    {
        BymlEditorFormat format = new(inlineMaxCount);
        return string.Join("\n", format.Serialize(document, 0)).TrimEnd() + "\n";
    }

    private static bool IsScalar(Byml node) => !node.IsContainer;

    // oead keeps a map's keys in byte order, whatever order the file stores them in; the editor text follows it.
    private static List<string> Keys(Byml map)
    {
        List<string> keys = [.. map.AsMap.Keys];
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    private static string Spaces(int indent) => new(' ', indent * 2);

    private static string Base64(byte[] data) => Convert.ToBase64String(data);

    private static string FormatScalar(Byml value, int indent)
    {
        switch (value.Type)
        {
            case BymlType.Bool: return value.Bool ? "true" : "false";
            case BymlType.UInt64: return $"!ul 0x{value.UInt64:x}";
            case BymlType.Int64:
            {
                long signed = value.Int64;
                return signed < 0 ? $"!sl 0x{(ulong)signed:x}" : $"!sl {signed}";
            }
            case BymlType.UInt: return $"!u 0x{value.UInt:x8}";
            case BymlType.Int: return value.Int.ToString(CultureInfo.InvariantCulture);
            case BymlType.Double: return "!f64 " + PyFloat.Repr(value.Double);
            case BymlType.Float:
            {
                string text = PyFloat.Repr(value.Float);
                return text.Contains('.') || text.Contains('e') || text.Contains("nan") || text.Contains("inf") ? text : text + ".0";
            }
            case BymlType.BinaryAligned:
            {
                string sp = Spaces(indent + 1);
                return $"!binary_aligned\n{sp}Alignment: !u 0x{(uint)value.BinaryAlignment:x8}\n{sp}Data: !!binary {Base64(value.Binary)}";
            }
            case BymlType.Binary: return "!!binary " + Base64(value.Binary);
            case BymlType.Null: return "null";
            case BymlType.String: return FormatString(value.String, indent);
            default: throw new NotSupportedException($"BYML node type {value.Type}");
        }
    }

    private static string FormatString(string value, int indent)
    {
        if (value.Contains('\n'))
        {
            string sp = Spaces(indent + 1);
            // The marker says how many newlines end the block, so the text reads back as the same string.
            // (A bare "|" keeps one, which would add a newline to every multi-line string that has none.)
            int trailing = value.Length - value.TrimEnd('\n').Length;
            string marker = trailing == 0 ? "|-" : trailing == 1 ? "|" : "|+";
            return marker + "\n" + string.Join("\n", value.Split('\n').Select(line => line.Length > 0 ? sp + line : sp));
        }

        bool needsQuotes = value.Length == 0 || value.IndexOfAny([':', '#', '[', ']', '{', '}', ',', '"', '\'', '\n', '\t']) >= 0 || value[0] is '-' or '?';
        return needsQuotes
            ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\""
            : value;
    }

    private bool CanInline(Byml node)
    {
        if (node.Type == BymlType.String && node.String.Contains('\n')) return false;
        if (node.Type == BymlType.BinaryAligned) return false;
        if (node.IsContainer)
        {
            if (node.Count > inlineMaxCount) return false;
            if (node.IsMap) return node.AsMap.Values.All(CanInline);
            if (node.IsArray) return node.AsArray.All(CanInline);
            throw new NotSupportedException("BYML hash maps");
        }

        return true;
    }

    private string SerializeInline(Byml node)
    {
        if (node.IsMap) return "{" + string.Join(", ", Keys(node).Select(k => $"{k}: {SerializeInline(node.AsMap[k])}")) + "}";
        if (node.IsArray) return node.Count == 0 ? "[]" : "[" + string.Join(", ", node.AsArray.Select(SerializeInline)) + "]";
        if (node.IsHashMap) throw new NotSupportedException("BYML hash maps");
        return FormatScalar(node, 0);
    }

    private List<string> SerializeHashEntries(Byml node, int indent)
    {
        string sp = Spaces(indent);
        List<string> lines = [];
        foreach (string key in Keys(node))
        {
            Byml value = node.AsMap[key];
            if (value.IsContainer)
            {
                if (CanInline(value)) lines.Add($"{sp}{key}: {SerializeInline(value)}");
                else
                {
                    lines.Add($"{sp}{key}:");
                    lines.AddRange(Serialize(value, indent + 1));
                }
            }
            else
            {
                lines.Add($"{sp}{key}: {FormatScalar(value, indent)}");
            }
        }

        return lines;
    }

    private List<string> SerializeArrayItemHash(Byml item, int indent)
    {
        string sp = Spaces(indent);
        List<string> keys = Keys(item);

        if (keys.Count == 1) return SerializeSingleKeyHashItem(item, keys[0], indent);

        List<string> lines = [];
        string firstKey = keys[0];
        Byml first = item.AsMap[firstKey];

        if (first.IsContainer)
        {
            if (CanInline(first)) lines.Add($"{sp}- {firstKey}: {SerializeInline(first)}");
            else
            {
                lines.Add($"{sp}- {firstKey}:");
                lines.AddRange(Serialize(first, indent + 2));
            }
        }
        else
        {
            lines.Add($"{sp}- {firstKey}: {FormatScalar(first, indent + 1)}");
        }

        foreach (string key in keys.Skip(1))
        {
            Byml value = item.AsMap[key];
            if (value.IsContainer)
            {
                if (CanInline(value)) lines.Add($"{sp}  {key}: {SerializeInline(value)}");
                else
                {
                    lines.Add($"{sp}  {key}:");
                    lines.AddRange(Serialize(value, indent + 2));
                }
            }
            else
            {
                lines.Add($"{sp}  {key}: {FormatScalar(value, indent + 1)}");
            }
        }

        return lines;
    }

    private List<string> SerializeSingleKeyHashItem(Byml item, string key, int indent)
    {
        string sp = Spaces(indent);
        Byml value = item.AsMap[key];
        List<string> lines = [];

        if (value.IsContainer)
        {
            if (CanInline(value)) lines.Add($"{sp}- {key}: {SerializeInline(value)}");
            else
            {
                lines.Add($"{sp}- {key}:");
                lines.AddRange(Serialize(value, indent + 2));
            }
        }
        else if (IsScalar(value))
        {
            lines.Add($"{sp}- {key}: {FormatScalar(value, indent + 1)}");
        }

        return lines;
    }

    private static List<string> SerializeFlatHashItem(Byml item, int indent)
    {
        string sp = Spaces(indent);
        List<string> keys = Keys(item);
        List<string> lines = [$"{sp}- {keys[0]}: {FormatScalar(item.AsMap[keys[0]], indent + 1)}"];
        foreach (string key in keys.Skip(1)) lines.Add($"{sp}  {key}: {FormatScalar(item.AsMap[key], indent + 1)}");
        return lines;
    }

    private List<string> Serialize(Byml node, int indent)
    {
        if (node.IsContainer && CanInline(node)) return [Spaces(indent) + SerializeInline(node)];

        if (node.IsMap) return SerializeHashEntries(node, indent);

        if (node.IsArray)
        {
            string sp = Spaces(indent);
            if (node.Count == 0) return [$"{sp}[]"];

            List<string> lines = [];
            foreach (Byml item in node.AsArray)
            {
                if (item.IsMap)
                {
                    if (CanInline(item))
                    {
                        lines.Add($"{sp}- {SerializeInline(item)}");
                    }
                    else
                    {
                        List<string> keys = Keys(item);
                        if (keys.Count == 1) lines.AddRange(SerializeSingleKeyHashItem(item, keys[0], indent));
                        else if (keys.Count > 0 && keys.All(k => IsScalar(item.AsMap[k]))) lines.AddRange(SerializeFlatHashItem(item, indent));
                        else lines.AddRange(SerializeArrayItemHash(item, indent));
                    }
                }
                else if (item.IsArray)
                {
                    if (CanInline(item)) lines.Add($"{sp}- {SerializeInline(item)}");
                    else
                    {
                        lines.Add($"{sp}-");
                        lines.AddRange(Serialize(item, indent + 1));
                    }
                }
                else
                {
                    lines.Add($"{sp}- {FormatScalar(item, indent)}");
                }
            }

            return lines;
        }

        if (node.IsHashMap) throw new NotSupportedException("BYML hash maps");
        return [Spaces(indent) + FormatScalar(node, indent)];
    }
}

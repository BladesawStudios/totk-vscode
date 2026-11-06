using System.Buffers.Binary;
using System.Text;
using BymlSharp;

namespace TkvscHost.Formats;

/// <summary>
/// The text form of a <c>tag.product.*rstbl*</c> BYML: each actor's tags as a list, in place of the table of bits the file
/// stores. Port of python/tag_product_format.py.
/// </summary>
public static class TagProduct
{
    public static string ToEditorText(Byml document, bool yaml)
    {
        List<string> pathList = document["PathList"] is { IsArray: true } p ? [.. p.AsArray.Select(Text)] : [];
        List<string> tags = document["TagList"] is { IsArray: true } t ? [.. t.AsArray.Select(Text)] : [];

        byte[] bits = document["BitTable"] switch
        {
            { Type: BymlType.String } s => Encoding.UTF8.GetBytes(s.String),
            { Type: BymlType.Binary or BymlType.BinaryAligned } b => b.Binary,
            _ => [],
        };

        // A dict keeps the first position of a repeated key and the last value.
        List<string> order = [];
        Dictionary<string, List<string>> actors = [];
        for (int i = 0; i < pathList.Count / 3; i++)
        {
            string actor = $"{pathList[i * 3]}|{pathList[i * 3 + 1]}|{pathList[i * 3 + 2]}";
            List<string> actorTags = [];
            for (int k = 0; k < tags.Count; k++)
            {
                long bit = (long)i * tags.Count + k;
                long index = bit / 8;
                if (index < bits.Length && ((bits[index] >> (int)(bit % 8)) & 1) == 1) actorTags.Add(tags[k]);
            }
            if (!actors.ContainsKey(actor)) order.Add(actor);
            actors[actor] = actorTags;
        }

        return yaml ? WriteYaml(order, actors, tags) : WriteJson(order, actors, tags);
    }

    private static string Text(Byml node) => node.Type == BymlType.String ? node.String : node.ToString();

    // json.dumps(indent=4): ASCII only, "key": value, empty containers as [] and {}.
    private static string WriteJson(List<string> order, Dictionary<string, List<string>> actors, List<string> tags)
    {
        StringBuilder sb = new("{\n    \"PathList\": ");
        if (order.Count == 0) sb.Append("{}");
        else
        {
            sb.Append("{\n");
            for (int i = 0; i < order.Count; i++)
            {
                sb.Append("        ").Append(JsonString(order[i])).Append(": ");
                AppendJsonList(sb, actors[order[i]], 8);
                sb.Append(i < order.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("    }");
        }
        sb.Append(",\n    \"TagList\": ");
        AppendJsonList(sb, tags, 4);
        return sb.Append("\n}").ToString();
    }

    private static void AppendJsonList(StringBuilder sb, List<string> items, int indent)
    {
        if (items.Count == 0) { sb.Append("[]"); return; }
        sb.Append("[\n");
        string pad = new(' ', indent + 4);
        for (int i = 0; i < items.Count; i++)
            sb.Append(pad).Append(JsonString(items[i])).Append(i < items.Count - 1 ? ",\n" : "\n");
        sb.Append(new string(' ', indent)).Append(']');
    }

    private static string JsonString(string value)
    {
        StringBuilder sb = new("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < ' ' || c > '~') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // yaml.safe_dump(sort_keys=False, allow_unicode=True): two-space indent, lists not indented under their key.
    private static string WriteYaml(List<string> order, Dictionary<string, List<string>> actors, List<string> tags)
    {
        StringBuilder sb = new("PathList:");
        if (order.Count == 0) sb.Append(" {}\n");
        else
        {
            sb.Append('\n');
            foreach (string actor in order)
            {
                sb.Append("  ").Append(PyYamlScalar.Write(actor)).Append(':');
                AppendYamlList(sb, actors[actor], "  ");
            }
        }
        sb.Append("TagList:");
        AppendYamlList(sb, tags, "");
        return sb.ToString();
    }

    private static void AppendYamlList(StringBuilder sb, List<string> items, string indent)
    {
        if (items.Count == 0) { sb.Append(" []\n"); return; }
        sb.Append('\n');
        foreach (string item in items) sb.Append(indent).Append("- ").Append(PyYamlScalar.Write(item)).Append('\n');
    }

    /// <summary>True if edited text is in this form (PathList is a map) rather than ordinary BYML YAML.</summary>
    public static bool IsEditorText(string text, out Byml document)
    {
        document = Byml.Null;
        try
        {
            document = Byml.FromYaml(text);
            return document.IsMap && document["PathList"] is { IsMap: true };
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Actors compared part by part, the order the game's files keep them in.
    private static int CompareActors(string[] a, string[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (string.CompareOrdinal(a[i], b[i]) is var order and not 0) return order;
        return a.Length - b.Length;
    }

    /// <summary>
    /// Builds the BYML back as the game's own files have it: actors and tags in byte order, the actors' paths in a row,
    /// one bit per actor and tag, the tag list, and the rank table.
    /// </summary>
    /// <remarks>
    /// For each tag, for each run of 32 actors, the rank table holds a u32: how many actors up to the end of that run have
    /// the tag. The game's reader works out the number of runs the same way; this rebuilds both games' tables byte for byte.
    /// </remarks>
    public static Byml FromEditorText(Byml document)
    {
        List<string> tags = document["TagList"] is { IsArray: true } t ? [.. t.AsArray.Select(Text)] : [];
        tags = [.. tags.Distinct().Order(StringComparer.Ordinal)];

        // A key that isn't a path has nothing to write, so it can't take up a row of bits either.
        List<(string[] Path, HashSet<string> Tags)> actors = [];
        foreach (var (path, entries) in document["PathList"]!.AsMap)
            if (path.Contains('|')) actors.Add((path.Split('|'), entries.IsArray ? [.. entries.AsArray.Select(Text)] : []));
        actors.Sort((a, b) => CompareActors(a.Path, b.Path));

        byte[] table = new byte[(actors.Count * tags.Count + 7) / 8];
        int runs = (actors.Count + 31) / 32;
        byte[] ranks = new byte[tags.Count * runs * 4];
        for (int k = 0; k < tags.Count; k++)
        {
            uint count = 0;
            for (int i = 0; i < actors.Count; i++)
            {
                if (actors[i].Tags.Contains(tags[k]))
                {
                    int bit = i * tags.Count + k;
                    table[bit / 8] |= (byte)(1 << (bit % 8));
                    count++;
                }
                if (i % 32 == 31 || i == actors.Count - 1)
                    BinaryPrimitives.WriteUInt32LittleEndian(ranks.AsSpan((k * runs + i / 32) * 4), count);
            }
        }

        return Byml.Map(new Dictionary<string, Byml>
        {
            ["PathList"] = Byml.Array(actors.SelectMany(a => a.Path).Select(s => (Byml)s)),
            ["BitTable"] = Byml.From(table),
            ["RankTable"] = Byml.From(ranks),
            ["TagList"] = Byml.Array(tags.Select(s => (Byml)s)),
        });
    }
}

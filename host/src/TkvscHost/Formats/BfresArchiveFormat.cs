using System.Text;
using BfresSharp;
using BfresSharp.Model;
using BfresSharp.Model.Animation;
using BfresSharp.Text;
using Tkvsc.Plugins;

namespace TkvscHost.Formats;

/// <summary>A BFRES file as an archive: its animations, models, textures and embedded files as entries in folders.</summary>
/// <remarks>
/// Material animations are YAML text and can be edited, added and deleted; a model is a folder holding one YAML file per material with its shader parameters (colours and numbers), which can be edited but not added or deleted, and read-only summaries of its shapes and bones; embedded files are their raw bytes and can
/// be replaced, added and deleted. Skeletal, visibility and shape animations and textures list as a short
/// read-only text summary; a save that changes or removes one is refused rather than dropping the change. A file
/// whose entries were not changed is returned byte for byte as it was read.
/// </remarks>
public sealed class BfresArchiveFormat(Func<IReadOnlyDictionary<ulong, string>?>? shaderNames = null) : IArchiveFormat
{
    // The game's names for the parameter hashes its files hold, when its romfs has them.
    private IReadOnlyDictionary<ulong, string>? Names => shaderNames?.Invoke();

    private const string ExternalFolder = "ExternalFiles";
    private const string ModelsFolder = "Models";

    private sealed record MaterialAnimFolder(string Folder, string Extension, Func<ResFile, ResDict<MaterialAnim>> Dict);

    private static readonly MaterialAnimFolder[] MaterialFolders =
    [
        new("MaterialAnims", "fmaa", file => file.MaterialAnims),
        new("ShaderParamAnims", "fshu", file => file.ShaderParamAnims),
        new("ColorAnims", "fcla", file => file.ColorAnims),
        new("TexSrtAnims", "ftsh", file => file.TexSrtAnims),
        new("TexPatternAnims", "ftxp", file => file.TexPatternAnims),
    ];

    public bool Matches(ReadOnlySpan<byte> data) => data.StartsWith("FRES"u8);

    public IReadOnlyList<ArchiveFile> Read(byte[] data, string logicalPath)
        => [.. Entries(BfresFile.Read(data), Names).Select(entry => new ArchiveFile(entry.Name, entry.Data))];

    public byte[] Write(IReadOnlyList<ArchiveFile> files, byte[] original, string logicalPath)
    {
        ResFile file = BfresFile.Read(original);
        Dictionary<string, byte[]> given = files.ToDictionary(f => f.Name, f => f.Data, StringComparer.Ordinal);
        Dictionary<string, Entry> before = Entries(file, Names).ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        bool changed = false;

        foreach (Entry entry in before.Values)
        {
            if (!given.TryGetValue(entry.Name, out byte[]? now))
            {
                (entry.Remove ?? throw ReadOnly(entry.Name, "removed"))();
                changed = true;
            }
            else if (!now.AsSpan().SequenceEqual(entry.Data))
            {
                (entry.Replace ?? throw ReadOnly(entry.Name, "changed"))(now);
                changed = true;
            }
        }

        foreach (var (name, data) in given.Where(pair => !before.ContainsKey(pair.Key)))
        {
            Add(file, name, data, Names);
            changed = true;
        }

        return changed ? BfresFile.Write(file) : original;
    }

    private sealed record Entry(string Name, byte[] Data, Action<byte[]>? Replace = null, Action? Remove = null);

    private static InvalidOperationException ReadOnly(string name, string verb)
        => new($"{name} cannot be {verb}: only material animations and embedded files in a BFRES can be edited.");

    private static IEnumerable<Entry> Entries(ResFile file, IReadOnlyDictionary<ulong, string>? names)
    {
        foreach (MaterialAnimFolder folder in MaterialFolders)
        {
            ResDict<MaterialAnim> dict = folder.Dict(file);
            foreach (string key in dict.Keys.ToList())
            {
                string name = $"{folder.Folder}/{key}.{folder.Extension}";
                yield return new Entry(name, Encoding.UTF8.GetBytes(MaterialAnimYaml.ToYaml(dict[key], names)),
                    text => dict[key] = MaterialAnimYaml.FromYaml(Encoding.UTF8.GetString(text), dict[key], names),
                    () => dict.Remove(key));
            }
        }

        foreach (string key in file.ExternalFiles.Keys.ToList())
        {
            ExternalFile external = file.ExternalFiles[key];
            yield return new Entry($"{ExternalFolder}/{key}", external.Data.ToArray(),
                bytes => file.ExternalFiles[key].Data = bytes,
                () => file.ExternalFiles.Remove(key));
        }

        // A model is a folder: one editable file per material, and a read-only summary per shape and for the skeleton.
        bool bigEndian = file.Platform == BfresSharp.Container.BfresPlatform.WiiU;
        foreach (string modelKey in file.Models.Keys.ToList())
        {
            ResModel model = file.Models[modelKey];
            string root = $"{ModelsFolder}/{modelKey}";
            foreach (string materialKey in model.Materials.Keys.ToList())
            {
                yield return new Entry($"{root}/Materials/{materialKey}.fmat", Encoding.UTF8.GetBytes(ModelParamsYaml.ToYaml(model, names, bigEndian, materialKey)),
                    text => ModelParamsYaml.Apply(model, Encoding.UTF8.GetString(text), names, bigEndian));
            }

            foreach (var (shapeKey, shape) in model.Shapes)
            {
                string material = shape.MaterialIndex < model.Materials.Count ? model.Materials.Keys.ElementAt(shape.MaterialIndex) : "?";
                yield return Summary($"{root}/Shapes", shapeKey, "fshp", "Shape", $"Material: {material}\nMeshes: {shape.Meshes.Count}\nSkinBoneCount: {shape.VertexSkinCount}");
            }

            yield return Summary($"{root}/Skeleton", "Bones", "fskl", "Skeleton", $"Bones: {model.Skeleton.Bones.Count}\n" + string.Join("", model.Skeleton.Bones.Keys.Select(bone => $"- {bone}\n")));
        }

        foreach (Entry entry in Summaries(file)) yield return entry;
    }

    private static void Add(ResFile file, string name, byte[] data, IReadOnlyDictionary<ulong, string>? names)
    {
        string[] parts = name.Split('/', 2);
        if (parts.Length == 2 && parts[0] == ExternalFolder)
        {
            file.ExternalFiles.Add(parts[1], new ExternalFile { Data = data });
            return;
        }

        MaterialAnimFolder? folder = parts.Length == 2 ? MaterialFolders.FirstOrDefault(f => f.Folder == parts[0]) : null;
        string suffix = folder is null ? "" : "." + folder.Extension;
        if (folder is null || !parts[1].EndsWith(suffix, StringComparison.Ordinal)) throw ReadOnly(name, "added");

        string key = parts[1][..^suffix.Length];
        MaterialAnim anim = MaterialAnimYaml.FromYaml(Encoding.UTF8.GetString(data), null, names);
        anim.Name = key;
        folder.Dict(file).Add(key, anim);
    }

    // The parts of the file that cannot be edited here still show up, so a BFRES lists everything it holds.
    private static IEnumerable<Entry> Summaries(ResFile file)
    {
        foreach (var (key, anim) in file.SkeletalAnims)
            yield return Summary("SkeletalAnims", key, "fska", "Skeletal animation", $"FrameCount: {anim.FrameCount}\nBoneAnims: {anim.BoneAnims.Count}\nLoop: {anim.Loop.ToString().ToLowerInvariant()}");
        foreach (var (key, anim) in file.VisibilityAnims)
            yield return Summary("VisibilityAnims", key, "fvis", "Visibility animation", $"FrameCount: {anim.FrameCount}\nCurves: {anim.Curves.Count}");
        foreach (var (key, anim) in file.MatVisibilityAnims)
            yield return Summary("MatVisibilityAnims", key, "fvis", "Material visibility animation", $"FrameCount: {anim.FrameCount}\nCurves: {anim.Curves.Count}");
        foreach (var (key, _) in file.ShapeAnims)
            yield return Summary("ShapeAnims", key, "fsha", "Shape animation", "");
        foreach (var (key, _) in file.Textures)
            yield return Summary("Textures", key, "ftex", "Texture", "");
    }

    private static Entry Summary(string folder, string key, string extension, string what, string details)
    {
        string text = $"# {what} \"{key}\" (read-only summary).\nName: {key}\n{details}".TrimEnd() + "\n";
        return new Entry($"{folder}/{key}.{extension}", Encoding.UTF8.GetBytes(text));
    }
}

using Paradise.Export.Data;
using Paradise.Export.Serialization;
using Zio;

namespace Paradise.Sample.Runtime;

/// <summary>
/// Reads a built document, whichever form the build wrote it in.
/// </summary>
/// <remarks>
/// <para>
/// The build's <c>document_format</c> chooses TOML or JSON per profile — TOML for the editor's
/// play tree, where a document you can read and diff against <c>assets/</c> is worth having;
/// JSON for a shipped build. Same contract, same converters, so the runtime does not care which it
/// gets and this is the only place that looks. The file says what it is: a scene path handed in on
/// the command line works without also being told which profile produced it.
/// </para>
/// <para>
/// Every read is out of an <see cref="IFileSystem"/> the host mounts, never a host path: the
/// content is a TREE — the scene, and the meshes, materials and textures its fields name relative
/// to the data root — and a mount is what makes that tree the whole of what the runtime can reach.
/// A test mounts memory and needs no files on disk.
/// </para>
/// </remarks>
public static class BuiltDocument
{
    private const string TomlExtension = ".toml";

    /// <summary>The name the play tree keeps for a scene: TOML under the authoring extension.</summary>
    private const string PrefabExtension = ".prefab";

    /// <summary>The document at <paramref name="stem"/> in either form, or null when there is none.</summary>
    public static UPath? Find(IFileSystem content, UPath directory, string stem)
    {
        foreach (var extension in new[] { ".json", TomlExtension, PrefabExtension })
        {
            var candidate = directory / (stem + extension);
            if (content.FileExists(candidate)) return candidate;
        }

        return null;
    }

    private static bool IsToml(UPath path)
    {
        var extension = path.GetExtensionWithDot();
        return TomlExtension.Equals(extension, StringComparison.OrdinalIgnoreCase)
            || PrefabExtension.Equals(extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A built <c>.material</c> keeps its name under every profile, so the engine's
    /// reader tells TOML from JSON by the text, never by the extension.</summary>
    public static LevelMaterialData ReadMaterial(IFileSystem content, UPath path)
        => ExportDocumentReader.ReadMaterial(content.ReadAllText(path));

    public static ProjectSettingsData ReadProjectSettings(IFileSystem content, UPath path)
    {
        var text = content.ReadAllText(path);
        return IsToml(path)
            ? ExportTomlReader.ReadProjectSettings(text)
            : ExportJsonReader.ReadProjectSettings(text);
    }

    /// <exception cref="FileNotFoundException">There is no document at that path.</exception>
    public static PrefabData ReadPrefab(IFileSystem content, UPath path)
    {
        if (!content.FileExists(path))
        {
            throw new FileNotFoundException($"Scene document not found: {path}", path.FullName);
        }

        var text = content.ReadAllText(path);
        return IsToml(path)
            ? ExportTomlReader.ReadPrefab(text)
            : ExportJsonReader.ReadPrefab(text);
    }
}

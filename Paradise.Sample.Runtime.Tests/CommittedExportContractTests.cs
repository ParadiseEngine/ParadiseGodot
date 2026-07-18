using Paradise.Export.Serialization;

namespace Paradise.Sample.Runtime.Tests;

/// <summary>Cross-check of the committed editor exports against the Paradise.Export package's
/// reader (moved from the export test suite when Paradise.Export left this repo — the engine's
/// Paradise.Export.Test suite is hermetic; the committed <c>data/</c> fixtures live here).</summary>
public class CommittedExportContractTests
{
    [Test]
    public async Task committed_sample_scene_parses()
    {
        var root = FindRepoRoot();
        var level = ExportJsonReader.ReadLevel(File.ReadAllText(Path.Combine(root, "data", "scenes", "sample.json")));
        await Assert.That(level.SchemaVersion).IsEqualTo(2);
        await Assert.That(level.Entities.Count).IsEqualTo(28);

        var settings = ExportJsonReader.ReadProjectSettings(File.ReadAllText(Path.Combine(root, "data", "ProjectSettings.json")));
        await Assert.That(settings.Rendering).IsNotNull();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "scenes", "sample.json")))
        {
            dir = dir.Parent!;
        }
        return dir!.FullName;
    }
}

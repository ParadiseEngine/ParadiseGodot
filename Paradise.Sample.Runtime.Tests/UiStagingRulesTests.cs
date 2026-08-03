using ParadiseGodot.Export;

namespace Paradise.Sample.Runtime.Tests;

/// <summary>The pure staging rules behind UI asset export: which files ship, which
/// directories are authoring-only, and what the best-effort XAML reference scan reports.</summary>
public class UiStagingRulesTests
{
    [Test]
    [Arguments("overlay.xaml", true)]
    [Arguments("NotoSansSC.ttf", true)]
    [Arguments("theme.OTF", true)]
    [Arguments("icon.png", true)]
    [Arguments("photo.JPEG", true)]
    [Arguments("vector.svg", true)]
    [Arguments("project.noesis", false)]
    [Arguments("notes.md", false)]
    [Arguments("OFL.txt", false)]
    [Arguments(".DS_Store", false)]
    [Arguments("", false)]
    public async Task staging_ships_runtime_ui_assets_only(string fileName, bool staged)
        => await Assert.That(UiStagingRules.ShouldStageFile(fileName)).IsEqualTo(staged);

    [Test]
    [Arguments(".noesis", true)]
    [Arguments(".git", true)]
    [Arguments(".import", true)]
    [Arguments("Fonts", false)]
    [Arguments("panels", false)]
    public async Task hidden_and_studio_directories_are_authoring_only(string directory, bool skipped)
        => await Assert.That(UiStagingRules.ShouldSkipDirectory(directory)).IsEqualTo(skipped);

    [Test]
    public async Task scan_finds_source_files_and_font_folders_once()
    {
        const string xaml = """
            <Grid TextElement.FontFamily="Fonts/#Noto Sans SC, Arial">
              <Image Source="art/emblem.png"/>
              <Image Source="art/emblem.png"/>
              <ImageBrush ImageSource="art\backdrop.jpg"/>
              <TextBlock FontFamily="./Fonts/#Noto Sans SC"/>
            </Grid>
            """;

        var references = UiStagingRules.ScanReferences(xaml);

        await Assert.That(references).Contains(new UiReference("art/emblem.png", UiReferenceKind.File));
        await Assert.That(references).Contains(new UiReference("art/backdrop.jpg", UiReferenceKind.File));
        await Assert.That(references).Contains(new UiReference("Fonts", UiReferenceKind.FontFolder));
        await Assert.That(references).Count().IsEqualTo(3); // de-duplicated across spellings
    }

    [Test]
    public async Task scan_ignores_bindings_uris_and_system_fonts()
    {
        const string xaml = """
            <Grid FontFamily="Arial, #PT Root UI">
              <Image Source="{Binding Portrait}"/>
              <Image Source="pack://application:,,,/art/x.png"/>
              <Image Source="https://example.com/x.png"/>
            </Grid>
            """;

        await Assert.That(UiStagingRules.ScanReferences(xaml)).IsEmpty();
    }
}

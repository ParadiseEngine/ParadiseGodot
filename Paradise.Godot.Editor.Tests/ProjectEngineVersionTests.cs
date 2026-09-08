using ParadiseGodot.Play;
using Zio;
using Zio.FileSystems;

namespace ParadiseGodot.Tests;

public class ProjectEngineVersionTests
{
    private static MemoryFileSystem Project(params (string Path, string Text)[] files)
    {
        var fs = new MemoryFileSystem();
        foreach (var (path, text) in files)
        {
            var full = (UPath)path;
            fs.CreateDirectory(full.GetDirectory());
            fs.WriteAllText(full, text);
        }
        return fs;
    }

    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <PackageReference Include="Paradise.ECS" Version="{0}" />
            <PackageReference Include="Paradise.Export" Version="{0}" />
            <PackageReference Include="TUnit" Version="1.57.0" />
          </ItemGroup>
        </Project>
        """;

    [Test]
    public async Task the_central_props_file_wins_when_there_is_one()
    {
        using var fs = Project(
            ("/repo/Directory.Packages.props",
                "<Project><PropertyGroup><ParadiseVersion>0.42.0</ParadiseVersion></PropertyGroup></Project>"),
            ("/repo/Game/Game.csproj", Csproj.Replace("{0}", "0.41.0")));

        await Assert.That(ProjectEngineVersion.Of(fs, "/repo")).IsEqualTo("0.42.0");
    }

    [Test]
    [Arguments("<Project />")]
    [Arguments("<Project><PropertyGroup><ParadiseVersion> </ParadiseVersion></PropertyGroup></Project>")]
    [Arguments("<Project>")]
    public async Task an_empty_or_unreadable_central_pin_falls_back_to_package_references(string props)
    {
        using var fs = Project(
            ("/repo/Directory.Packages.props", props),
            ("/repo/Game/Game.csproj", Csproj.Replace("{0}", "0.41.0")));

        await Assert.That(ProjectEngineVersion.Of(fs, "/repo")).IsEqualTo("0.41.0");
    }

    [Test]
    public async Task package_references_answer_when_every_project_agrees()
    {
        using var fs = Project(
            ("/repo/Core/Core.csproj", Csproj.Replace("{0}", "0.41.0")),
            ("/repo/Launcher/Launcher.csproj", Csproj.Replace("{0}", "0.41.0")));

        await Assert.That(ProjectEngineVersion.Of(fs, "/repo")).IsEqualTo("0.41.0");
    }

    [Test]
    public async Task disagreeing_projects_pin_nothing()
    {
        // Mixed pins cannot select a CLI that safely writes every project's document format.
        using var fs = Project(
            ("/repo/Core/Core.csproj", Csproj.Replace("{0}", "0.41.0")),
            ("/repo/Launcher/Launcher.csproj", Csproj.Replace("{0}", "0.38.0")));

        await Assert.That(ProjectEngineVersion.Of(fs, "/repo")).IsNull();
    }

    [Test]
    public async Task the_addon_has_its_own_release_line_and_does_not_count()
    {
        using var fs = Project(
            ("/repo/Core/Core.csproj", Csproj.Replace("{0}", "0.41.0")),
            ("/repo/Game.Godot.csproj", """
                <Project Sdk="Godot.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Paradise.Godot.Editor" Version="0.40.0" />
                  </ItemGroup>
                </Project>
                """));

        await Assert.That(ProjectEngineVersion.Of(fs, "/repo")).IsEqualTo("0.41.0");
    }

    [Test]
    public async Task build_output_does_not_outvote_the_source()
    {
        // Published output may contain stale copies of the project files.
        using var fs = Project(
            ("/repo/Web/Web.csproj", Csproj.Replace("{0}", "0.41.0")),
            ("/repo/Web/bin/Debug/publish/Web.csproj", Csproj.Replace("{0}", "0.25.0")),
            ("/repo/Web/obj/Web.csproj", Csproj.Replace("{0}", "0.25.0")));

        await Assert.That(ProjectEngineVersion.Of(fs, "/repo")).IsEqualTo("0.41.0");
    }

    [Test]
    public async Task a_project_that_pins_nothing_reads_as_null()
    {
        using var fs = Project(("/repo/Game/Game.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"));

        await Assert.That(ProjectEngineVersion.Of(fs, "/repo")).IsNull();
    }

    [Test]
    public async Task unparseable_xml_pins_nothing_rather_than_throwing()
    {
        using var fs = Project(("/repo/Game/Game.csproj", "<Project><ItemGroup>"));

        await Assert.That(ProjectEngineVersion.Of(fs, "/repo")).IsNull();
    }
}

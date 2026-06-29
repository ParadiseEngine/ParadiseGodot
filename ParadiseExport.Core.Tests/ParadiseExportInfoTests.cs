using Newtonsoft.Json.Linq;

namespace ParadiseExport.Core.Tests;

// TUnit globals ([Test], Assert) come from the package's implicit global usings.
public class ParadiseExportInfoTests
{
    [Test]
    public async Task describe_returns_non_empty_json_object()
    {
        // JObject.Parse throws on invalid/empty input, so reaching the assertion already proves
        // the output parses; assert on property count so an empty "{}" would still fail.
        JObject parsed = JObject.Parse(ParadiseExportInfo.Describe());
        await Assert.That(parsed.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task describe_reports_tool_and_version()
    {
        JObject info = JObject.Parse(ParadiseExportInfo.Describe());
        await Assert.That((string?)info["tool"]).IsEqualTo("ParadiseExport.Core");
        await Assert.That((string?)info["version"]).IsEqualTo(ParadiseExportInfo.Version);
    }

    [Test]
    public async Task describe_resolves_dependency_assemblies_at_runtime()
    {
        // Non-null version strings prove Newtonsoft and DotRecast actually load at runtime,
        // not merely that they compiled.
        JObject info = JObject.Parse(ParadiseExportInfo.Describe());
        await Assert.That(string.IsNullOrWhiteSpace((string?)info["newtonsoft"])).IsFalse();
        await Assert.That(string.IsNullOrWhiteSpace((string?)info["dotRecast"])).IsFalse();
    }
}

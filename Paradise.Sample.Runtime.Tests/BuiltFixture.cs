using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;
using Zio;
using Zio.FileSystems;

namespace Paradise.Sample.Runtime.Tests;

/// <summary>
/// The repo's <c>assets/</c> built into its play tree, once per test run, by the engine's own
/// pipeline — the same thing <c>paradise host play</c> does before it runs the game.
/// </summary>
/// <remarks>
/// <para>
/// Built rather than committed: a built tree is derived output (58 MB of it, mostly textures),
/// and the tests are meant to prove the loader against what the build ACTUALLY writes, which a
/// committed copy would let drift. The build's cache under <c>.editor/cache</c> makes a rerun
/// seconds; the first run on a cold machine pays for the texture encodes.
/// </para>
/// <para>
/// Textures need the <c>ktx</c> CLI. A host without one cannot build the tree, so every test
/// that reads it skips rather than fails — the same policy the GPU tests apply to a host without a
/// WebGPU adapter. CI installs it.
/// </para>
/// </remarks>
internal static class BuiltFixture
{
    private static readonly object Gate = new();
    private static string? _root;
    private static string? _problem;
    private static bool _built;

    /// <summary>The host path of a built scene, or a skipped test when the tree cannot be built here.</summary>
    public static string Scene(string name)
    {
        var play = Build();
        var path = Path.Combine(play, "scenes", name + ".prefab");
        if (!File.Exists(path)) Skip.Test($"{path} was not written by the build.");
        return path;
    }

    /// <summary>The repo root: the directory holding <c>assets/project.toml</c> above the test binary.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "project.toml")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("No assets/project.toml above the test binary.");
    }

    private static string Build()
    {
        lock (Gate)
        {
            if (!_built)
            {
                _built = true;
                try
                {
                    _root = BuildOnce();
                }
                catch (Exception failure) when (failure is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    _problem = failure.Message;
                }
            }
        }

        if (_root is null) Skip.Test($"The asset tree could not be built on this host: {_problem}");
        return _root!;
    }

    private static string BuildOnce()
    {
        var repo = RepoRoot();
        var files = new PhysicalFileSystem();
        var layout = new AssetProjectLayout(files.ConvertPathFromInternal(repo));

        if (!KtxTextureEncoder.TryCreate(repo, out var encoder, out var problem) || encoder is null)
        {
            throw new InvalidOperationException(problem ?? "no ktx encoder (install KTX-Software, or `paradise tools install ktx`)");
        }

        var result = new BuildRunner(files, layout, encoder).Run("dev", ProjectOutputTarget.Play);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("build failed: " + string.Join("; ", result.Errors));
        }

        return files.ConvertPathToInternal(result.Output);
    }
}

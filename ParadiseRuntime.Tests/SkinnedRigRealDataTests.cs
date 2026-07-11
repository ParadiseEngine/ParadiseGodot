using System.Numerics;
using Paradise.Assets.Gltf;

namespace ParadiseRuntime.Tests;

/// <summary>The animation rig against the REAL shipped character rigs (elf 216 joints,
/// dragon 144): at rest pose the joint palettes must collapse to ~identity and CPU-skinned
/// output must reproduce the bind mesh — the invariant that keeps un-animated characters
/// pixel-identical to the static path.</summary>
public class SkinnedRigRealDataTests
{
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "third_party")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName;
    }

    private static GltfAsset? LoadModelOrSkip(string name)
    {
        var root = FindRepoRoot();
        var path = root is null ? null : Path.Combine(root, "data", "Models", $"{name}.glb");
        if (path is null || !File.Exists(path))
        {
            Skip.Test($"data/Models/{name}.glb not present on this host.");
            return null;
        }
        var glbDir = Path.GetDirectoryName(path)!;
        return GltfSceneReader.Read(File.ReadAllBytes(path), uri => File.ReadAllBytes(Path.Combine(glbDir, uri)));
    }

    [Test]
    [Arguments("elf")]
    [Arguments("dragon")]
    public async Task rest_pose_skins_real_rig_to_its_bind_mesh(string model)
    {
        var asset = LoadModelOrSkip(model);
        if (asset is null) return;
        await Assert.That(asset.Skins.Length).IsGreaterThanOrEqualTo(1);

        var rig = new GltfAnimationRig(asset);
        rig.EvaluatePose(null, 0f);

        foreach (var instance in asset.Instances)
        {
            if (instance.SkinIndex < 0) continue;
            var skin = asset.Skins[instance.SkinIndex];
            var palette = new Matrix4x4[skin.JointNodes.Length];
            rig.ComputeJointPalette(instance.SkinIndex, instance.NodeIndex, palette);

            foreach (var primitive in asset.Meshes[instance.MeshIndex].Primitives)
            {
                if (primitive.JointsWeights is null) continue;
                var output = new float[primitive.Vertices.Length];
                GltfAnimationRig.SkinVertices(primitive, palette, output);

                // Positions must land back on the bind mesh. Tolerance covers 200+-joint float
                // chains (invBind × restWorld round trips); anything visibly off would be
                // orders of magnitude larger.
                var worst = 0f;
                for (var v = 0; v < output.Length; v += GltfPrimitive.FloatsPerVertex)
                {
                    var delta = new Vector3(
                        output[v] - primitive.Vertices[v],
                        output[v + 1] - primitive.Vertices[v + 1],
                        output[v + 2] - primitive.Vertices[v + 2]).Length();
                    worst = MathF.Max(worst, delta);
                }
                await Assert.That(worst).IsLessThan(1e-3f);
            }
        }
    }

    [Test]
    public async Task real_rigs_fit_the_reference_palette_cap()
    {
        var asset = LoadModelOrSkip("elf");
        if (asset is null) return;
        // bank-heist's shader palette caps at 256 joints; the CPU skinner has no hard cap but
        // this documents the largest shipped rig so a future GPU path knows its budget.
        await Assert.That(asset.Skins[0].JointNodes.Length).IsLessThanOrEqualTo(256);
    }
}

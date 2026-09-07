#if TOOLS
using Godot;

namespace ParadiseGodot.Authoring
{
    /// <summary>
    /// A model under <c>assets/</c> as an ordinary Godot scene under <c>.editor/godot/</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The GLB is parsed once, here, instead of once per entity per open. A pond with twelve rocks
    /// used to re-parse the same file twelve times on every open; now the twelve instantiate one
    /// saved scene. That is the whole benefit — a mirrored model is NOT more capable than the GLB
    /// it came from, because nothing under a dot-prefixed directory is imported by Godot: no
    /// FileSystem dock, no drag-and-drop, no imported materials.
    /// </para>
    /// <para>
    /// It is saved rather than copied for the same reason. A copied <c>.glb</c> in an unscanned
    /// directory is bytes nothing can load; a saved scene loads by explicit path with no import
    /// step, which is exactly why the working <c>.tscn</c> beside it works.
    /// </para>
    /// </remarks>
    public static class ModelMirror
    {
        /// <summary>Convert one GLB and save it. False with the reason on any failure.</summary>
        /// <param name="glbHostPath">The model under <c>assets/</c>, as an OS path.</param>
        /// <param name="mirrorResPath">Where to save it, as a <c>res://</c> path.</param>
        public static bool Write(string glbHostPath, string mirrorResPath, out string? problem)
        {
            if (ModelPreview.Load(glbHostPath, out problem) is not { } scene) return false;

            // Every node must be owned by the root or Pack writes a scene with nothing in it —
            // measured: the GLB's own hierarchy arrives unowned, and the mirror loaded back with
            // zero meshes. DocumentLoader does the same for the same reason.
            Own(scene, scene);

            var packed = new PackedScene();
            // Pack before freeing: the scene is detached, and PackedScene copies out of the node.
            var packError = packed.Pack(scene);
            scene.QueueFree();
            if (packError != Error.Ok)
            {
                problem = $"'{glbHostPath}' did not pack ({packError}).";
                return false;
            }

            var directory = mirrorResPath[..mirrorResPath.LastIndexOf('/')];
            if (DirAccess.MakeDirRecursiveAbsolute(directory) is var dirError && dirError != Error.Ok)
            {
                problem = $"could not create '{directory}' ({dirError}).";
                return false;
            }

            if (ResourceSaver.Save(packed, mirrorResPath) is var saveError && saveError != Error.Ok)
            {
                problem = $"could not write '{mirrorResPath}' ({saveError}).";
                return false;
            }

            problem = null;
            return true;
        }

        private static void Own(Node node, Node root)
        {
            foreach (Node child in node.GetChildren())
            {
                child.Owner = root;
                Own(child, root);
            }
        }

        /// <summary>Instantiate a mirrored model, or null when it has not been mirrored yet.</summary>
        public static Node3D? Instantiate(string mirrorResPath)
        {
            if (!ResourceLoader.Exists(mirrorResPath)) return null;

            try
            {
                return ResourceLoader.Load<PackedScene>(mirrorResPath)?.Instantiate() as Node3D;
            }
            catch (System.Exception)
            {
                // A mirror that will not load is stale or half-written; the caller falls back to
                // parsing the GLB, which is what it did before any mirror existed.
                return null;
            }
        }
    }
}
#endif

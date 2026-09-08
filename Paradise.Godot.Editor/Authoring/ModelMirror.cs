#if TOOLS
using Godot;

namespace ParadiseGodot.Authoring
{
    /// <summary>Cache an assets/ model as a Godot scene under .editor/godot/.</summary>
    /// <remarks>
    /// Parsing once lets entities instantiate a shared scene. Godot does not scan dot-prefixed
    /// directories, so a copied GLB would not load there; a saved scene loads by explicit path
    /// without import. Mirrors still have no FileSystem dock entry or imported materials.
    /// </remarks>
    public static class ModelMirror
    {
        /// <summary>Convert and save a GLB; false with a reason on failure.</summary>
        /// <param name="glbHostPath">Source model's OS path.</param>
        /// <param name="mirrorResPath">Destination res:// path.</param>
        public static bool Write(string glbHostPath, string mirrorResPath, out string? problem)
        {
            if (ModelPreview.Load(glbHostPath, out problem) is not { } scene) return false;

            // GLB children arrive unowned; Pack silently omits them unless the root owns them.
            Own(scene, scene);

            var packed = new PackedScene();
            // Pack copies the detached scene, so it must run before freeing it.
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

        /// <summary>Instantiate a mirrored model, or null if missing or unreadable.</summary>
        public static Node3D? Instantiate(string mirrorResPath)
        {
            if (!ResourceLoader.Exists(mirrorResPath)) return null;

            try
            {
                return ResourceLoader.Load<PackedScene>(mirrorResPath)?.Instantiate() as Node3D;
            }
            catch (System.Exception)
            {
                // The caller falls back to the GLB if the mirror is stale or incomplete.
                return null;
            }
        }
    }
}
#endif

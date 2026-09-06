#if TOOLS
using Godot;

namespace ParadiseGodot.Authoring
{
    /// <summary>
    /// A GLB as a scene the editor can show, loaded straight off disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Through <see cref="GltfDocument"/> rather than as an imported resource, because
    /// <c>assets/</c> carries a <c>.gdignore</c>: Godot never imports the source tree — and must
    /// not, since its own <c>.mesh</c> extension collides with the engine's mesh document and the
    /// importer chokes on every one of them — so nothing under it can be instanced by path.
    /// </para>
    /// <para>
    /// In an editor <c>GenerateScene</c> produces <see cref="ImporterMeshInstance3D"/> nodes, the
    /// import pipeline's intermediate form, which draw nothing. They are swapped for the
    /// <see cref="MeshInstance3D"/> they would have become, keeping name, transform and the
    /// material the GLB bound.
    /// </para>
    /// </remarks>
    public static class ModelPreview
    {
        /// <summary>Load a GLB at a host path as a drawable scene, or null with the reason.</summary>
        public static Node3D? Load(string glbHostPath, out string? problem)
        {
            var state = new GltfState();
            var document = new GltfDocument();
            // A GLB whose images are external files loads untextured here: Godot localizes any
            // path inside the project to res:// and fetches the images through the resource
            // loader, which cannot see a .gdignore'd tree. Passing a host base path does not
            // change that (it is localized too). Geometry is what a preview is for; textures are
            // the build's.
            Error error = document.AppendFromFile(glbHostPath, state);
            if (error != Error.Ok)
            {
                problem = $"'{glbHostPath}' did not load as glTF ({error}).";
                return null;
            }

            if (document.GenerateScene(state) is not Node3D scene)
            {
                problem = $"'{glbHostPath}' generated no scene.";
                return null;
            }

            Materialize(scene);
            problem = null;
            return scene;
        }

        /// <summary>Replace every importer mesh with the mesh instance it stands for, in place.</summary>
        private static void Materialize(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                Materialize(child);
            }

            if (node is not ImporterMeshInstance3D importer || importer.Mesh is null) return;

            var instance = new MeshInstance3D
            {
                Name = importer.Name,
                Transform = importer.Transform,
                Mesh = importer.Mesh.GetMesh(),
                Skin = importer.Skin,
                Visible = importer.Visible,
            };
            foreach (Node child in importer.GetChildren())
            {
                importer.RemoveChild(child);
                instance.AddChild(child);
            }

            var parent = importer.GetParent();
            int index = importer.GetIndex();
            parent.RemoveChild(importer);
            parent.AddChild(instance);
            parent.MoveChild(instance, index);
            importer.QueueFree();
        }
    }
}
#endif

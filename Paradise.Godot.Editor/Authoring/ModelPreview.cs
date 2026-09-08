#if TOOLS
using Godot;

namespace ParadiseGodot.Authoring
{
    /// <summary>Load a GLB directly from disk as a drawable editor scene.</summary>
    /// <remarks>
    /// assets/ needs .gdignore because engine .mesh documents conflict with Godot's resource
    /// format, so loading must bypass imports. In the editor, GenerateScene returns non-drawing
    /// ImporterMeshInstance3D nodes; replace them with MeshInstance3D, keeping mesh materials,
    /// name, transform, skin, visibility and children.
    /// </remarks>
    public static class ModelPreview
    {
        /// <summary>Load a drawable scene from an OS path, or null with a failure reason.</summary>
        public static Node3D? Load(string glbHostPath, out string? problem)
        {
            var state = new GltfState();
            var document = new GltfDocument();
            // External images under .gdignore cannot load: Godot localizes project paths
            // to res://, even with an absolute base path. Geometry still previews; embedded
            // textures avoid this limitation.
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

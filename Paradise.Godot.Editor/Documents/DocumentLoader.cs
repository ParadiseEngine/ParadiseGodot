#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using Paradise.Assets.Documents;
using ParadiseGodot.Authoring;
using SN = System.Numerics;

namespace ParadiseGodot.Documents
{
    /// <summary>Builds a cached Godot scene tree from an authoring document.</summary>
    /// <remarks>The document remains authoritative. Placement uses separate position, rotation and
    /// scale channels because a <c>Transform3D</c> round trip loses precision.</remarks>
    public static class DocumentLoader
    {
        /// <summary>Marks expanded prefab children that the writer must skip to preserve instances.</summary>
        public const string DerivedMetaKey = "paradise_derived";

        /// <param name="Root">The scene root, or null if it could not be built.</param>
        /// <param name="Objects">Nodes created.</param>
        /// <param name="Components">Payloads seen, including built-ins and unknown components.
        /// Unknown payloads are hidden here and preserved by the writer's merge.</param>
        /// <param name="Problems">Problems phrased for the author.</param>
        public readonly record struct Result(
            Node3D? Root, int Objects, int Components, IReadOnlyList<string> Problems);

        /// <param name="document">A document with prefab instances already resolved.</param>
        /// <param name="sceneName">Wrapper name for a document without a single root, allowing invalid
        /// hierarchies to open for repair.</param>
        public static Result Build(PrefabDocument document, string sceneName)
        {
            ArgumentNullException.ThrowIfNull(document);

            var ordered = DocumentTree.Order(document);
            var problems = new List<string>(ordered.Problems);
            if (ordered.Nodes.Count == 0)
            {
                return new Result(null, 0, 0, problems);
            }

            var built = new List<Node3D>(ordered.Nodes.Count);
            var roots = new List<Node3D>();
            int components = 0;
            foreach (var node in ordered.Nodes)
            {
                if (Create(node.Object, problems) is not { } created)
                {
                    // A bare Node3D fallback would silently drop all components on save.
                    return new Result(null, 0, 0, problems);
                }

                components += node.Object.Components.Count;
                built.Add(created);
                if (node.ParentIndex >= 0) built[node.ParentIndex].AddChild(created);
                else roots.Add(created);
            }

            Node3D scene;
            if (roots.Count == 1) scene = roots[0];
            else
            {
                // Open invalid multi-root documents under a non-entity holder so authors can repair them.
                problems.Add(
                    $"The document has {roots.Count} root objects; they are shown under a '{sceneName}' " +
                    "holder, which is NOT part of the document. Parent them beneath one root.");
                scene = new Node3D { Name = sceneName };
                foreach (var root in roots) scene.AddChild(root);
            }

            Own(scene, scene);
            return new Result(scene, built.Count, components, problems);
        }

        private static Node3D? Create(PrefabObject entry, List<string> problems)
        {
            if (AuthoredEntityCore.CreateNode() is not { } entity)
            {
                problems.Add(
                    "The AuthoredEntityNode script is missing from this project, so no entity node " +
                    "can be created. Rebuild the C# project to restore the addon payload.");
                return null;
            }

            var node = entity.Node;
            node.Name = entry.Name is { Length: > 0 } name ? name : "Object";
            if (entry.Guid is { } guid) entity.RestoreEntityGuid(guid);

            if (entry.Component(WellKnownComponents.TransformId) is { } transform)
            {
                Place(node, LocalTransformCodec.Read(transform.Data));
            }

            entity.AdoptDocumentComponents(entry.Components);

            // Mark override carriers and resolved prefab children so the writer skips them.
            if (entry.Target is not null || entry.Prefab is not null)
            {
                node.SetMeta(DerivedMetaKey, true);
            }

            return node;
        }

        /// <summary>Set scale last: rotating after non-uniform scaling can bake shear into Godot's basis.</summary>
        private static void Place(Node3D node, LocalTransform transform)
        {
            node.Position = ToGodot(transform.Position);
            node.Quaternion = new Quaternion(
                transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W);
            node.Scale = ToGodot(transform.Scale);
        }

        /// <summary>PackedScene omits nodes unless their Owner is the scene root.</summary>
        private static void Own(Node node, Node owner)
        {
            foreach (var child in node.GetChildren())
            {
                child.Owner = owner;
                Own(child, owner);
            }
        }

        // The document uses Godot/glTF coordinates: Y-up, -Z forward; no handedness conversion.
        private static Vector3 ToGodot(SN.Vector3 v) => new(v.X, v.Y, v.Z);
    }
}
#endif

#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using Paradise.Assets.Documents;
using ParadiseGodot.Authoring;
using SN = System.Numerics;

namespace ParadiseGodot.Documents
{
    /// <summary>
    /// Builds a Godot scene tree from an authoring document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One direction only, and one direction is the point of this half: <c>assets/</c> is the
    /// source of truth, the <c>.tscn</c> is a cache of it, and re-materializing is always correct.
    /// The other direction — nodes back to a document — is a separate problem with its own rules
    /// (re-read, merge, refuse on drift) and does not belong in the reader.
    /// </para>
    /// <para>
    /// <b>Placement is assigned as CHANNELS, never as a matrix.</b> Round-tripping a TRS through a
    /// <c>Transform3D</c> is lossy at about 1e-6 — enough to move objects on a save that changed
    /// nothing, which is how the Blender host learned it (it moved 25 of ShiningPie's 321 objects
    /// per export). Position, rotation and scale go to the properties that hold them.
    /// </para>
    /// </remarks>
    public static class DocumentLoader
    {
        /// <summary>Set on a node the RESOLVER produced rather than the document: a prefab
        /// instance's expanded children. Saving one back would flatten the instance, so the writer
        /// must skip them.</summary>
        public const string DerivedMetaKey = "paradise_derived";

        /// <summary>What a build produced, for the caller to report.</summary>
        /// <param name="Root">The scene root, or null when nothing could be built.</param>
        /// <param name="Objects">Nodes created.</param>
        /// <param name="Components">Component payloads seen, including the format's own two. A
        /// payload the schema does not describe is counted but not shown: it cannot be drawn, and
        /// the writer preserves it by re-reading the document rather than by echoing it here.</param>
        /// <param name="Problems">Everything that could not be honoured, phrased for an author.</param>
        public readonly record struct Result(
            Node3D? Root, int Objects, int Components, IReadOnlyList<string> Problems);

        /// <summary>
        /// Build <paramref name="document"/> into nodes.
        /// </summary>
        /// <param name="document">A document, already resolved if it had instances.</param>
        /// <param name="sceneName">Names a wrapper root, used only when the document has no single
        /// root of its own — which <c>PrefabDocument.Validate</c> refuses, but an author has to be
        /// able to open a document in order to fix it.</param>
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
            int components = 0;
            foreach (var node in ordered.Nodes)
            {
                if (Create(node.Object, problems) is not { } created)
                {
                    // Placeholder-free on purpose: a null here means the addon payload is missing,
                    // which Create has already reported, and inventing a bare Node3D would produce
                    // a scene that saves back as an object with every component silently dropped.
                    return new Result(null, 0, 0, problems);
                }

                components += node.Object.Components.Count;
                built.Add(created);
                if (node.ParentIndex >= 0) built[node.ParentIndex].AddChild(created);
            }

            var roots = Roots(ordered.Nodes, built);
            if (roots.Count == 1)
            {
                Own(roots[0], roots[0]);
                return new Result(roots[0], built.Count, components, problems);
            }

            // More than one root: the document is invalid (an instance places exactly one thing),
            // but it opens, under a holder that is not itself an entity.
            problems.Add(
                $"The document has {roots.Count} root objects; they are shown under a '{sceneName}' " +
                "holder, which is NOT part of the document. Parent them beneath one root.");
            var wrapper = new Node3D { Name = sceneName };
            foreach (var root in roots) wrapper.AddChild(root);
            Own(wrapper, wrapper);
            return new Result(wrapper, built.Count, components, problems);
        }

        private static List<Node3D> Roots(IReadOnlyList<DocumentTree.Node> ordered, List<Node3D> built)
        {
            var roots = new List<Node3D>();
            for (int index = 0; index < ordered.Count; index++)
            {
                if (ordered[index].ParentIndex < 0) roots.Add(built[index]);
            }

            return roots;
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

            // An override carrier addresses a prefab child rather than being one, and a resolved
            // instance's children are the resolver's rather than the document's. Both are marked so
            // the writer can tell them from what an author placed.
            if (entry.Target is not null || entry.Prefab is not null)
            {
                node.SetMeta(DerivedMetaKey, true);
            }

            return node;
        }

        /// <summary>Assign the three channels. Scale last: Godot recomposes the local transform on
        /// every setter, and writing the rotation after a non-uniform scale is what bakes shear
        /// into the basis.</summary>
        private static void Place(Node3D node, LocalTransform transform)
        {
            node.Position = ToGodot(transform.Position);
            node.Quaternion = new Quaternion(
                transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W);
            node.Scale = ToGodot(transform.Scale);
        }

        /// <summary>Every node in a built scene must be OWNED by its root or PackedScene writes an
        /// empty file — the failure that looks like "the save worked and lost everything".</summary>
        private static void Own(Node node, Node owner)
        {
            foreach (var child in node.GetChildren())
            {
                child.Owner = owner;
                Own(child, owner);
            }
        }

        // No handedness conversion: the contract IS Godot/glTF convention (Y-up, -Z forward), which
        // is why the exporter wrote its values verbatim and why this reads them the same way.
        private static Vector3 ToGodot(SN.Vector3 v) => new(v.X, v.Y, v.Z);
    }
}
#endif

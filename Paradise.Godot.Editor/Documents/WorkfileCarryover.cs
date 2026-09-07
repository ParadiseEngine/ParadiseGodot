#if TOOLS
using System;
using System.Collections.Generic;
using Godot;

namespace ParadiseGodot.Documents
{
    /// <summary>
    /// The nodes an author added to a working file, carried across a rebuild of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A document describes entities and component payloads; it has no place for a
    /// <c>CollisionShape3D</c>, a <c>Light3D</c> or a camera rig, and those are exactly what an
    /// author builds in Godot to point components at. Rebuilding the working file used to delete
    /// them — the values they had been baked into survived in the document, the nodes themselves
    /// did not, and the author had to build them again.
    /// </para>
    /// <para>
    /// So a rebuild takes them with it. Anchored by the GUID of the nearest entity ABOVE each one,
    /// not by node path: the rebuild may reorder or rename siblings, and an entity that has gone
    /// from the document takes its scaffolding with it rather than stranding it at the root.
    /// </para>
    /// <para>
    /// Entity nodes themselves are never carried — they are the document's, and the whole point of
    /// a rebuild is that they come from the file. Derived nodes are never carried either: the
    /// instance expansion and the model preview are regenerated, and a stale copy of one would be
    /// indistinguishable from an authored node the next time round.
    /// </para>
    /// </remarks>
    public static class WorkfileCarryover
    {
        private const string GuidMetaKey = "paradise_entity_guid";

        /// <summary>One node an author added, and the entity it hung under.</summary>
        /// <param name="Anchor">The GUID of the nearest entity ancestor, or null for the root.</param>
        public readonly record struct Adopted(string? Anchor, Node Node);

        /// <summary>Take the author's own nodes out of a scene, detached and ready to re-attach.
        /// The scene itself is left to the caller to free.</summary>
        public static List<Adopted> Take(Node root)
        {
            ArgumentNullException.ThrowIfNull(root);

            var adopted = new List<Adopted>();
            Collect(root, anchor: null, adopted);
            foreach (var entry in adopted) entry.Node.GetParent()?.RemoveChild(entry.Node);
            return adopted;
        }

        /// <summary>Re-attach what <see cref="Take"/> removed, and own it so a save keeps it.</summary>
        /// <returns>How many were placed; the rest had no entity left to hang under.</returns>
        public static int Restore(Node root, IReadOnlyList<Adopted> adopted)
        {
            ArgumentNullException.ThrowIfNull(root);
            ArgumentNullException.ThrowIfNull(adopted);

            var byGuid = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
            Index(root, byGuid);

            var placed = 0;
            foreach (var entry in adopted)
            {
                var parent = entry.Anchor is null
                    ? root
                    : byGuid.TryGetValue(entry.Anchor, out var found) ? found : null;
                if (parent is null)
                {
                    // Its entity is gone from the document. Dropping it is the honest answer:
                    // re-rooting would move it somewhere the author never put it.
                    entry.Node.QueueFree();
                    continue;
                }

                parent.AddChild(entry.Node);
                Own(entry.Node, root);
                placed++;
            }
            return placed;
        }

        private static void Collect(Node node, string? anchor, List<Adopted> adopted)
        {
            foreach (var child in node.GetChildren())
            {
                if (child.HasMeta(DocumentLoader.DerivedMetaKey)) continue;

                if (GuidOf(child) is { } guid)
                {
                    Collect(child, guid, adopted);
                    continue;
                }

                // Not an entity: the author's, and so is everything under it — recursing further
                // would take a subtree apart and re-attach the pieces separately.
                adopted.Add(new Adopted(anchor, child));
            }
        }

        private static void Index(Node node, Dictionary<string, Node> byGuid)
        {
            foreach (var child in node.GetChildren())
            {
                if (GuidOf(child) is { } guid) byGuid[guid] = child;
                Index(child, byGuid);
            }
        }

        // Every node the scene keeps must be owned by its root, or PackedScene writes it away.
        private static void Own(Node node, Node root)
        {
            node.Owner = root;
            foreach (var child in node.GetChildren()) Own(child, root);
        }

        private static string? GuidOf(Node node) =>
            node.HasMeta(GuidMetaKey) ? node.GetMeta(GuidMetaKey).AsString() : null;
    }
}
#endif

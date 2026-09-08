#if TOOLS
using System;
using System.Collections.Generic;
using Godot;

namespace ParadiseGodot.Documents
{
    /// <summary>Preserves author-added nodes when rebuilding a working file.</summary>
    /// <remarks>
    /// Documents cannot store Godot colliders, lights or rigs. Carry these under the nearest entity
    /// ancestor's GUID so renames and reordering do not break their placement. If that entity was
    /// deleted, drop its nodes. Entity and derived nodes are regenerated from the document.
    /// </remarks>
    public static class WorkfileCarryover
    {
        private const string GuidMetaKey = "paradise_entity_guid";

        /// <param name="Anchor">Nearest entity ancestor's GUID, or null for the scene root.</param>
        public readonly record struct Adopted(string? Anchor, Node Node);

        /// <summary>Detach author nodes for reattachment; the caller must free the remaining scene.</summary>
        public static List<Adopted> Take(Node root)
        {
            ArgumentNullException.ThrowIfNull(root);

            var adopted = new List<Adopted>();
            Collect(root, anchor: null, adopted);
            return adopted;
        }

        /// <summary>Reattach detached nodes and set ownership so PackedScene keeps them.</summary>
        /// <returns>Nodes placed; nodes whose entity was deleted are dropped.</returns>
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
                    : byGuid.GetValueOrDefault(entry.Anchor);
                if (parent is null)
                {
                    // Re-rooting nodes whose entity was deleted would change their authored placement.
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

                // Take the whole authored subtree; recursing would split it into separate attachments.
                node.RemoveChild(child);
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

        // PackedScene only retains nodes owned by the scene root.
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

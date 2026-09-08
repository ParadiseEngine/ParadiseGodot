#if TOOLS
using System;
using System.Collections.Generic;
using Paradise.Assets.Documents;

namespace ParadiseGodot.Documents
{
    /// <summary>Orders document objects parent-first for Godot while preserving sibling order.</summary>
    /// <remarks>The document's own order stays intact because runtime handle assignment may depend
    /// on it. Missing parents and cycles become reported roots so invalid documents open for repair.</remarks>
    public static class DocumentTree
    {
        /// <param name="ParentIndex">Index in the ordered list, or -1 for a root.</param>
        public readonly record struct Node(PrefabObject Object, int ParentIndex);

        public readonly record struct Result(IReadOnlyList<Node> Nodes, IReadOnlyList<string> Problems);

        public static Result Order(PrefabDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);

            var problems = new List<string>();
            var byGuid = new Dictionary<Guid, PrefabObject>();
            foreach (var entry in document.Objects)
            {
                if (entry.Guid is not { } guid) continue;
                // Match PrefabDocument.ByGuid's last-wins rule, but report the objects this drops.
                if (!byGuid.TryAdd(guid, entry))
                {
                    problems.Add(
                        $"'{Describe(entry)}' repeats the identity {guid:D}; only the last object with it is placed.");
                    byGuid[guid] = entry;
                }
            }

            var children = new Dictionary<Guid, List<PrefabObject>>();
            var roots = new List<PrefabObject>();
            foreach (var entry in document.Objects)
            {
                var parent = EffectiveParent(entry, byGuid, problems);
                if (parent is not { } parentGuid)
                {
                    roots.Add(entry);
                    continue;
                }

                if (!children.TryGetValue(parentGuid, out var list))
                {
                    children[parentGuid] = list = [];
                }

                list.Add(entry);
            }

            var nodes = new List<Node>(document.Objects.Count);
            foreach (var root in roots) Place(root, -1, children, nodes);
            return new Result(nodes, problems);
        }

        /// <summary>Depth-first traversal keeps each subtree contiguous.</summary>
        private static void Place(
            PrefabObject entry,
            int parentIndex,
            Dictionary<Guid, List<PrefabObject>> children,
            List<Node> nodes)
        {
            int index = nodes.Count;
            nodes.Add(new Node(entry, parentIndex));
            if (entry.Guid is not { } guid || !children.TryGetValue(guid, out var mine)) return;

            foreach (var child in mine) Place(child, index, children, nodes);
        }

        private static Guid? EffectiveParent(
            PrefabObject entry, Dictionary<Guid, PrefabObject> byGuid, List<string> problems)
        {
            if (entry.Parent is not { } parent) return null;

            if (!byGuid.ContainsKey(parent))
            {
                problems.Add(
                    $"'{Describe(entry)}' names a parent ({parent:D}) this document does not contain; " +
                    "placed at the root.");
                return null;
            }

            // A parent chain longer than the document must contain a cycle.
            var current = parent;
            for (int depth = 0; depth <= byGuid.Count; depth++)
            {
                if (entry.Guid is { } self && current == self)
                {
                    problems.Add(
                        $"'{Describe(entry)}' is inside a parent cycle; placed at the root so the " +
                        "document can be opened and fixed.");
                    return null;
                }

                if (!byGuid.TryGetValue(current, out var next) || next.Parent is not { } up) return parent;
                current = up;
            }

            problems.Add($"'{Describe(entry)}' has a parent chain too deep to resolve; placed at the root.");
            return null;
        }

        private static string Describe(PrefabObject entry) =>
            entry.Name is { Length: > 0 } name ? name : DocumentGuid.Format(entry.Guid ?? Guid.Empty);
    }
}
#endif

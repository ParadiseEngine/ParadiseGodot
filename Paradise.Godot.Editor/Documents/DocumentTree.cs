#if TOOLS
using System;
using System.Collections.Generic;
using Paradise.Assets.Documents;

namespace ParadiseGodot.Documents
{
    /// <summary>
    /// A document's objects, ordered so a parent always precedes its children.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Godot needs the parent node to exist before a child can be added to it, and a document does
    /// not promise that order: object order is the EXPORT's, and it is load-bearing for other
    /// reasons — a runtime that assigns handles in walk order depends on it — so it cannot simply
    /// be sorted. This produces a second order for building, leaving the document's own untouched.
    /// </para>
    /// <para>
    /// Sibling order is preserved exactly. A stable order is what keeps a re-open from reshuffling
    /// the scene tree an author is looking at.
    /// </para>
    /// <para>
    /// Nothing here throws. A document that names a parent it does not contain, or that parents two
    /// objects to each other, is a document an author has to be able to OPEN in order to fix — so
    /// the offending objects become roots and the problem is reported rather than raised.
    /// </para>
    /// </remarks>
    public static class DocumentTree
    {
        /// <summary>One object, and where it hangs.</summary>
        /// <param name="Object">The object itself.</param>
        /// <param name="ParentIndex">Index into the ordered list, or -1 for a root.</param>
        public readonly record struct Node(PrefabObject Object, int ParentIndex);

        /// <summary>The ordered nodes, and what could not be honoured.</summary>
        public readonly record struct Result(IReadOnlyList<Node> Nodes, IReadOnlyList<string> Problems);

        /// <summary>Order <paramref name="document"/>'s objects parent-first.</summary>
        public static Result Order(PrefabDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);

            var problems = new List<string>();
            var byGuid = new Dictionary<Guid, PrefabObject>();
            foreach (var entry in document.Objects)
            {
                if (entry.Guid is not { } guid) continue;
                // Last wins, matching PrefabDocument.ByGuid — but unlike a lookup, building a TREE
                // from duplicates silently drops objects, so it is named here.
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

        /// <summary>Depth-first, so a subtree is contiguous — which is what makes the built scene
        /// read like the document rather than like a breadth-first shuffle of it.</summary>
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

        /// <summary>The parent to hang <paramref name="entry"/> from, or null to make it a root.</summary>
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

            // Walk up. A chain longer than the document cannot be acyclic, so this bounds the walk
            // and is the cycle guard in one.
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

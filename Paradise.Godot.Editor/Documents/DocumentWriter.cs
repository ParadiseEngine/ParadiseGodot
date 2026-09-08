#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Paradise.Assets.Documents;
using ParadiseGodot.Authoring;
using ParadiseGodot.Project;
using Zio;
using SN = System.Numerics;

namespace ParadiseGodot.Documents
{
    /// <summary>Merges an open scene's edits into its source document.</summary>
    /// <remarks>Re-read before merging to preserve unknown payloads; refuse if the source changed.
    /// The working <c>.tscn</c> still saves on refusal, retaining edits until the conflict is resolved.</remarks>
    public static class DocumentWriter
    {
        public enum Outcome
        {
            /// <summary>An ordinary Godot scene with no source document.</summary>
            NotADocument,
            Written,
            /// <summary>Identical content; the file was left untouched.</summary>
            Unchanged,
            Refused,
        }

        public static Outcome Save(ParadiseProject project, Node root)
        {
            ArgumentNullException.ThrowIfNull(project);
            ArgumentNullException.ThrowIfNull(root);

            if (DocumentSession.DocumentOf(root) is not { } authoringPath) return Outcome.NotADocument;

            var document = project.Paths.FromAssetReferencePath(authoringPath);
            if (!DocumentSession.IsUnchanged(project.Files, document, authoringPath))
            {
                GD.PushError(
                    $"[Paradise] '{authoringPath}' changed on disk since it was opened, so this save " +
                    "was NOT written to it — merging blind would drop whatever changed it. Your edits " +
                    "are still in the working scene; reopen the document to take the new version.");
                return Outcome.Refused;
            }

            PrefabDocument current;
            try
            {
                current = PrefabDocumentSerializer.Load(project.Files, document);
            }
            catch (PrefabDocumentException failure)
            {
                GD.PushError($"[Paradise] '{authoringPath}' does not read, so nothing was written: {failure.Message}");
                return Outcome.Refused;
            }

            var states = new List<DocumentMerge.ObjectState>();
            Harvest(root, parent: null, AssetReferenceResolver.For(project), states);
            var merged = DocumentMerge.Apply(current, states);
            foreach (var problem in merged.Problems) GD.PushWarning($"[Paradise] {problem}");

            var before = PrefabDocumentSerializer.Write(current);
            var after = PrefabDocumentSerializer.Write(merged.Document);
            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                // Identical bytes must not change mtime or invalidate dependent caches.
                Forget(root);
                return Outcome.Unchanged;
            }

            try
            {
                project.Files.WriteAllBytes(document, Encoding.UTF8.GetBytes(after));
            }
            catch (Exception failure) when (failure is System.IO.IOException or UnauthorizedAccessException)
            {
                GD.PushError($"[Paradise] Could not write '{authoringPath}': {failure.Message}");
                return Outcome.Refused;
            }

            DocumentSession.Restamp(project.Files, document, authoringPath);
            // Restamp the workfile too so this save does not trigger a rebuild on the next open.
            if (project.Paths.WorkfileFor(document) is { } workfile)
            {
                WorkfileStamp.Write(project.Files, workfile, document);
            }
            Forget(root);
            GD.Print($"[Paradise] Wrote '{authoringPath}': {states.Count} object(s).");
            return Outcome.Written;
        }

        /// <summary>Clear edits now represented in the saved document.</summary>
        private static void Forget(Node node)
        {
            if (node is IAuthoredEntity entity) entity.Edits.Clear();
            foreach (var child in node.GetChildren()) Forget(child);
        }

        /// <summary>Collect document entities, skipping derived prefab subtrees to preserve instances.</summary>
        private static void Harvest(
            Node node, Guid? parent, AssetReferenceResolver assets, List<DocumentMerge.ObjectState> states)
        {
            var childParent = parent;
            if (node is IAuthoredEntity entity && node is Node3D placed)
            {
                if (node.HasMeta(DocumentLoader.DerivedMetaKey)) return;

                var guid = entity.EnsureEntityGuid();
                var values = new Dictionary<string, AuthoredValue>(entity.AuthoredValues(), StringComparer.Ordinal);
                var baked = entity.BakedHostValues(assets);
                foreach (var (key, value) in baked) values[key] = value;

                states.Add(new DocumentMerge.ObjectState(
                    guid,
                    node.Name.ToString(),
                    parent,
                    Local(placed),
                    entity.Edits,
                    values,
                    baked.Keys.ToList()));
                childParent = guid;
            }

            foreach (var child in node.GetChildren()) Harvest(child, childParent, assets, states);
        }

        /// <summary>Read TRS channels directly; matrix round trips lose precision even without edits.</summary>
        private static LocalTransform Local(Node3D node)
        {
            var position = node.Position;
            var rotation = node.Quaternion;
            var scale = node.Scale;
            return new LocalTransform(
                new SN.Vector3(position.X, position.Y, position.Z),
                new SN.Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W),
                new SN.Vector3(scale.X, scale.Y, scale.Z));
        }
    }
}
#endif

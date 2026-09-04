#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Paradise.Assets.Documents;
using ParadiseGodot.Authoring;
using ParadiseGodot.Project;
using Zio;
using SN = System.Numerics;

namespace ParadiseGodot.Documents
{
    /// <summary>
    /// Writes an open scene back to the document it came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The document is RE-READ and merged into, never regenerated from the scene. That is what lets
    /// a component this addon has never heard of round-trip verbatim, and it is why a save refuses
    /// rather than proceeds when the file has moved underneath it: merging blind would drop
    /// whatever changed it.
    /// </para>
    /// <para>
    /// A refusal is reported, not raised. The working <c>.tscn</c> still saves, so the author's
    /// edits are not lost — they sit in the workfile while the refusal stands, and re-opening the
    /// document is what resolves it.
    /// </para>
    /// </remarks>
    public static class DocumentWriter
    {
        /// <summary>What a save did.</summary>
        public enum Outcome
        {
            /// <summary>This scene is not a materialized document; nothing to do.</summary>
            NotADocument,
            Written,
            /// <summary>Nothing had changed, so the file was left alone.</summary>
            Unchanged,
            Refused,
        }

        /// <summary>Write the scene rooted at <paramref name="root"/> back to its document.</summary>
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
            Harvest(root, parent: null, states);
            var merged = DocumentMerge.Apply(current, states);
            foreach (var problem in merged.Problems) GD.PushWarning($"[Paradise] {problem}");

            var before = PrefabDocumentSerializer.Write(current);
            var after = PrefabDocumentSerializer.Write(merged.Document);
            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                // Nothing to write. Not an optimization: touching the file would restamp it, dirty
                // git, and invalidate anything keyed on its mtime, all to say the same bytes.
                Forget(root);
                return Outcome.Unchanged;
            }

            try
            {
                PrefabDocumentSerializer.Save(project.Files, document, merged.Document);
            }
            catch (Exception failure) when (failure is System.IO.IOException or UnauthorizedAccessException)
            {
                GD.PushError($"[Paradise] Could not write '{authoringPath}': {failure.Message}");
                return Outcome.Refused;
            }

            DocumentSession.Restamp(project.Files, document, authoringPath);
            Forget(root);
            GD.Print($"[Paradise] Wrote '{authoringPath}': {states.Count} object(s).");
            return Outcome.Written;
        }

        /// <summary>The overlay has been applied, so it is no longer what the author changed: the
        /// document on disk now says what it used to.</summary>
        private static void Forget(Node node)
        {
            if (node is IAuthoredEntity entity) entity.Edits.Clear();
            foreach (var child in node.GetChildren()) Forget(child);
        }

        /// <summary>
        /// Walk the scene, collecting every entity that belongs to the document.
        /// </summary>
        /// <remarks>A DERIVED node is a prefab instance's expanded child: it belongs to the prefab,
        /// not to this document, and writing it back would flatten the instance. Its own children
        /// are skipped with it, because a child of something that is not here has nowhere to
        /// hang.</remarks>
        private static void Harvest(Node node, Guid? parent, List<DocumentMerge.ObjectState> states)
        {
            var childParent = parent;
            if (node is IAuthoredEntity entity && node is Node3D placed)
            {
                if (node.HasMeta(DocumentLoader.DerivedMetaKey)) return;

                var guid = entity.EnsureEntityGuid();
                var values = new Dictionary<string, AuthoredValue>(entity.AuthoredValues(), StringComparer.Ordinal);
                var baked = entity.BakedHostValues();
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

            foreach (var child in node.GetChildren()) Harvest(child, childParent, states);
        }

        /// <summary>Read the three channels, never the matrix — the same reason the loader assigns
        /// them: a TRS round trip through a Transform3D is lossy at about 1e-7, and a save that
        /// changed nothing would move things.</summary>
        private static LocalTransform Local(Node3D node) => new(
            new SN.Vector3(node.Position.X, node.Position.Y, node.Position.Z),
            new SN.Quaternion(node.Quaternion.X, node.Quaternion.Y, node.Quaternion.Z, node.Quaternion.W),
            new SN.Vector3(node.Scale.X, node.Scale.Y, node.Scale.Z));
    }
}
#endif

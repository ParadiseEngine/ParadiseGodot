#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Paradise.Assets.Documents;

namespace ParadiseGodot.Documents
{
    /// <summary>
    /// Applies what an author changed on top of the document as it stands on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The document is the base, not the scene.</b> The file is re-read and merged into, rather
    /// than regenerated from the nodes, and that is what lets a component this addon has never
    /// heard of round-trip verbatim: nothing rewrites a payload the overlay does not name.
    /// </para>
    /// <para>
    /// <b>Object order is the document's.</b> Surviving objects keep their positions and new ones
    /// are appended, because the loader orders parents-first for Godot's sake and emitting THAT
    /// order would reshuffle a document on every save of a scene nobody edited.
    /// </para>
    /// <para>
    /// Free of Godot on purpose: a <c>Variant</c> cannot exist in a unit test (it segfaults the
    /// host), so the merge takes <see cref="AuthoredValue"/> and the harvesting of it from nodes
    /// stays at the editor edge.
    /// </para>
    /// </remarks>
    public static class DocumentMerge
    {
        /// <summary>
        /// How close a scene TRS has to be to the document's to count as untouched, relative to the
        /// value's own magnitude.
        /// </summary>
        /// <remarks>
        /// Round-tripping a TRS through Godot's <c>Transform3D</c> costs about 1e-7 relative; float32
        /// itself is ~1.2e-7. A deliberate edit smaller than this is a sub-micron move on a
        /// metre-scale object, which no viewport drag produces — so the trade is "ignore an edit
        /// nobody can see" against "rewrite every number in the file on every save". Without it a
        /// save of an untouched scene is a diff of the whole document.
        /// </remarks>
        public const float TransformEpsilon = 1e-6f;

        /// <summary>One entity as the scene now has it.</summary>
        /// <param name="Guid">Its identity, carried from the document.</param>
        /// <param name="Name">Its node name.</param>
        /// <param name="Parent">Its parent's identity, or null at the root.</param>
        /// <param name="Transform">Its local TRS.</param>
        /// <param name="Edits">What the author changed.</param>
        /// <param name="Values">Every authored value, keyed <c>&lt;componentId&gt;/&lt;path&gt;</c>.
        /// Read for components the author ADDED, which have nothing in the document to override, and
        /// for the fields named in <paramref name="HostBaked"/>.</param>
        /// <param name="HostBaked">Keys whose value is read off a host object rather than typed, and
        /// which are therefore written on EVERY save. An author who moves the shape a collider
        /// points at has changed that collider, and no edit was recorded against this entity to say
        /// so. Writing them costs nothing when they have not moved: the same value produces the same
        /// bytes.</param>
        public readonly record struct ObjectState(
            Guid Guid,
            string? Name,
            Guid? Parent,
            LocalTransform Transform,
            AuthoredEdits Edits,
            IReadOnlyDictionary<string, AuthoredValue> Values,
            IReadOnlyCollection<string>? HostBaked = null);

        /// <summary>The merged document, and what could not be honoured.</summary>
        public readonly record struct Result(PrefabDocument Document, IReadOnlyList<string> Problems);

        /// <summary>Merge <paramref name="states"/> into <paramref name="current"/>.</summary>
        /// <param name="current">The document as it stands on disk, freshly read.</param>
        /// <param name="states">The scene's entities. An object in the document with no state here
        /// was deleted by the author — except an override carrier, which never had a node.</param>
        public static Result Apply(PrefabDocument current, IReadOnlyList<ObjectState> states)
        {
            ArgumentNullException.ThrowIfNull(current);
            ArgumentNullException.ThrowIfNull(states);

            var problems = new List<string>();
            var byGuid = new Dictionary<Guid, ObjectState>();
            foreach (var state in states)
            {
                if (!byGuid.TryAdd(state.Guid, state))
                {
                    problems.Add(
                        $"Two objects in the scene claim the identity {state.Guid:D} " +
                        $"('{state.Name}'); only the first is written.");
                }
            }

            var merged = new PrefabDocument();
            var written = new HashSet<Guid>();
            foreach (var entry in current.Objects)
            {
                // An override carrier addresses a prefab child rather than being one, so it has no
                // node and cannot be "missing". It travels untouched.
                if (entry.Target is not null)
                {
                    merged.Objects.Add(entry);
                    continue;
                }

                if (entry.Guid is not { } guid || !byGuid.TryGetValue(guid, out var state)) continue;

                merged.Objects.Add(Merge(entry, state));
                written.Add(guid);
            }

            foreach (var state in states)
            {
                if (written.Add(state.Guid)) merged.Objects.Add(Create(state));
            }

            return new Result(merged, problems);
        }

        /// <summary>An existing object, with the author's changes over it.</summary>
        private static PrefabObject Merge(PrefabObject entry, ObjectState state)
        {
            var result = new PrefabObject { Prefab = entry.Prefab };
            foreach (var component in entry.Components)
            {
                if (component.Id == WellKnownComponents.MetaId)
                {
                    result.Components.Add(Meta(component, state));
                    continue;
                }

                if (component.Id == WellKnownComponents.TransformId)
                {
                    result.Components.Add(Transform(component, state.Transform));
                    continue;
                }

                var id = component.Id.ToString();
                if (state.Edits.Removed.Contains(id)) continue;

                result.Components.Add(Edited(component, id, state));
            }

            // Since v6 nothing downstream synthesizes placement, so an entity without these is one
            // a runtime cannot place. A document that arrived without them gets them here.
            if (entry.Component(WellKnownComponents.MetaId) is null)
            {
                result.Components.Insert(0, MetaFor(state));
            }

            if (entry.Component(WellKnownComponents.TransformId) is null)
            {
                result.Components.Insert(1, LocalTransformCodec.Write(state.Transform));
            }

            AppendAdded(result, state, entry);
            return result;
        }

        /// <summary>An object the author placed, which the document has never seen.</summary>
        private static PrefabObject Create(ObjectState state)
        {
            var result = new PrefabObject();
            result.Components.Add(MetaFor(state));
            result.Components.Add(LocalTransformCodec.Write(state.Transform));
            AppendAdded(result, state, existing: null);
            return result;
        }

        /// <summary>Components the author turned on, in the overlay's order. A component that was
        /// not in the document has nothing there to override, so its whole payload comes from the
        /// scene.</summary>
        private static void AppendAdded(PrefabObject result, ObjectState state, PrefabObject? existing)
        {
            foreach (var id in state.Edits.Added)
            {
                if (!Guid.TryParse(id, out var componentId)) continue;
                if (existing?.Component(componentId) is not null) continue;

                var data = new CanonicalTomlTable();
                foreach (var (path, value) in FieldsOf(state.Values, id)) data = Set(data, path, value);
                result.Components.Add(new PrefabComponent(componentId, type: null, data));
            }
        }

        /// <summary>The <c>meta</c> table, with the author's name and parent over it. Rebuilt rather
        /// than replaced so fields this addon does not know about survive.</summary>
        private static PrefabComponent Meta(PrefabComponent component, ObjectState state)
        {
            var data = new CanonicalTomlTable();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, value) in component.Data)
            {
                seen.Add(key);
                switch (key)
                {
                    case WellKnownComponents.Name when state.Name is { Length: > 0 } name:
                        data.Add(key, name);
                        break;
                    case WellKnownComponents.Parent when state.Parent is { } parent:
                        data.Add(key, DocumentGuid.Format(parent));
                        break;
                    // A reparent to the root DROPS the key rather than writing an empty guid: absent
                    // is how the format spells "root", and an empty one reads as a broken reference.
                    case WellKnownComponents.Parent:
                        break;
                    default:
                        data.Add(key, value);
                        break;
                }
            }

            if (!seen.Contains(WellKnownComponents.Guid)) data.Add(WellKnownComponents.Guid, DocumentGuid.Format(state.Guid));
            if (!seen.Contains(WellKnownComponents.Name) && state.Name is { Length: > 0 } added) data.Add(WellKnownComponents.Name, added);
            if (!seen.Contains(WellKnownComponents.Parent) && state.Parent is { } gained)
            {
                data.Add(WellKnownComponents.Parent, DocumentGuid.Format(gained));
            }

            return new PrefabComponent(component.Id, component.Type, data, component.Removed);
        }

        private static PrefabComponent MetaFor(ObjectState state) =>
            PrefabObject.WithMeta(state.Guid, state.Name, state.Parent).Components[0];

        /// <summary>The document's own transform when nothing moved — the SAME component, so the
        /// canonical writer emits the same bytes it read.</summary>
        private static PrefabComponent Transform(PrefabComponent component, LocalTransform now)
        {
            var authored = LocalTransformCodec.Read(component.Data);
            return Unchanged(authored, now) ? component : LocalTransformCodec.Write(now);
        }

        /// <summary>One component with the author's edited fields, and its host-baked ones, over it.</summary>
        private static PrefabComponent Edited(PrefabComponent component, string id, ObjectState state)
        {
            var prefix = id + "/";
            var edited = state.Edits.FieldsOf(id)
                .Concat((state.HostBaked ?? [])
                    .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(key => key[prefix.Length..]))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (edited.Count == 0) return component;

            var data = component.Data;
            foreach (var path in edited)
            {
                if (state.Values.TryGetValue(id + "/" + path, out var value)) data = Set(data, path, value);
            }

            return new PrefabComponent(component.Id, component.Type, data, component.Removed);
        }

        /// <summary>A copy of <paramref name="table"/> with one slash path set.</summary>
        /// <remarks>Returns a new table because a canonical one is append-only by design, and its
        /// KEY ORDER is the written order — so a set has to rebuild around the key's existing
        /// position. That is what keeps an edited document diffable to the line that changed
        /// instead of reordering the file.</remarks>
        private static CanonicalTomlTable Set(CanonicalTomlTable table, string path, AuthoredValue value)
        {
            int slash = path.IndexOf('/');
            var key = slash < 0 ? path : path[..slash];

            object? replacement;
            if (slash < 0)
            {
                replacement = ToCanonical(value);
                // A value with no canonical spelling (an unreadable field) leaves the table alone
                // rather than deleting the key: the author's edit is dropped, never their data.
                if (replacement is null) return table;
            }
            else
            {
                var nested = table.Value(key) as CanonicalTomlTable ?? new CanonicalTomlTable();
                replacement = Set(nested, path[(slash + 1)..], value);
            }

            var rebuilt = new CanonicalTomlTable();
            var replaced = false;
            foreach (var (name, held) in table)
            {
                if (string.Equals(name, key, StringComparison.Ordinal))
                {
                    rebuilt.Add(name, replacement);
                    replaced = true;
                }
                else
                {
                    rebuilt.Add(name, held);
                }
            }

            if (!replaced) rebuilt.Add(key, replacement);
            return rebuilt;
        }

        private static object? ToCanonical(AuthoredValue value) => value.Kind switch
        {
            AuthoredValueKind.Bool => value.Bool,
            AuthoredValueKind.Integer => value.Integer,
            AuthoredValueKind.Number => value.Number,
            AuthoredValueKind.Text => value.Text,
            AuthoredValueKind.Numbers => value.Numbers!.Select(number => (object)(double)number).ToList(),
            AuthoredValueKind.Rgba => Rgba(value.Numbers!),
            // Through the codec rather than by hand: it is what decides an AssetReference is written
            // INLINE, and the reader recognises one by exactly that shape. A table built here would
            // come back out as a [header] and stop being a reference.
            AuthoredValueKind.Reference => AssetReferenceCodec.Write(
                value.Identity == Guid.Empty && string.IsNullOrEmpty(value.Text)
                    ? null
                    : new Paradise.Authoring.AssetReference(value.Identity, value.Text ?? "")),
            _ => null,
        };

        private static CanonicalTomlTable Rgba(float[] channels)
        {
            var table = new CanonicalTomlTable();
            var names = new[] { "r", "g", "b", "a" };
            for (int index = 0; index < names.Length; index++) table.Add(names[index], (double)channels[index]);
            return table;
        }

        private static IEnumerable<(string Path, AuthoredValue Value)> FieldsOf(
            IReadOnlyDictionary<string, AuthoredValue> values, string componentId)
        {
            var prefix = componentId + "/";
            foreach (var (key, value) in values)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal)) yield return (key[prefix.Length..], value);
            }
        }

        private static bool Unchanged(LocalTransform authored, LocalTransform now) =>
            Close(authored.Position.X, now.Position.X) &&
            Close(authored.Position.Y, now.Position.Y) &&
            Close(authored.Position.Z, now.Position.Z) &&
            Close(authored.Rotation.X, now.Rotation.X) &&
            Close(authored.Rotation.Y, now.Rotation.Y) &&
            Close(authored.Rotation.Z, now.Rotation.Z) &&
            Close(authored.Rotation.W, now.Rotation.W) &&
            Close(authored.Scale.X, now.Scale.X) &&
            Close(authored.Scale.Y, now.Scale.Y) &&
            Close(authored.Scale.Z, now.Scale.Z);

        /// <summary>Relative where the value is large enough for relative to mean anything, absolute
        /// near zero — a position at the origin has no magnitude to be relative to.</summary>
        private static bool Close(float authored, float now)
        {
            var difference = MathF.Abs(authored - now);
            var magnitude = MathF.Max(MathF.Abs(authored), MathF.Abs(now));
            return magnitude > 1f ? difference <= TransformEpsilon * magnitude : difference <= TransformEpsilon;
        }
    }
}
#endif

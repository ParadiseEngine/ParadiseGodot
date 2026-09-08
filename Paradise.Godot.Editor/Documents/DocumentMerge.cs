#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Paradise.Assets.Documents;

namespace ParadiseGodot.Documents
{
    /// <summary>Merges scene edits into the freshly read document.</summary>
    /// <remarks>
    /// Untouched payloads, including unknown components, survive verbatim. Existing objects retain
    /// file order; new objects append, avoiding the loader's parent-first reordering on save.
    /// Uses <see cref="AuthoredValue"/> because constructing a Godot Variant crashes unit tests.
    /// </remarks>
    public static class DocumentMerge
    {
        /// <summary>Tolerance for treating scene TRS values as unchanged.</summary>
        /// <remarks>Absorbs Godot/float32 round-trip error so an untouched save preserves the document.
        /// Edits below this threshold are also ignored; comparison is relative above unit magnitude.</remarks>
        public const float TransformEpsilon = 1e-6f;

        /// <summary>An entity's current scene state.</summary>
        /// <param name="Guid">Document identity.</param>
        /// <param name="Name">Node name.</param>
        /// <param name="Parent">Parent identity, or null at the root.</param>
        /// <param name="Transform">Local TRS.</param>
        /// <param name="Edits">Recorded author edits.</param>
        /// <param name="Values">Authored values keyed as <c>&lt;componentId&gt;/&lt;path&gt;</c>,
        /// including full payloads for added components and values for <paramref name="HostBaked"/>.</param>
        /// <param name="HostBaked">Fields sampled from host objects on every save, since moving a linked
        /// shape can change a component without recording an edit on this entity.</param>
        public readonly record struct ObjectState(
            Guid Guid,
            string? Name,
            Guid? Parent,
            LocalTransform Transform,
            AuthoredEdits Edits,
            IReadOnlyDictionary<string, AuthoredValue> Values,
            IReadOnlyCollection<string>? HostBaked = null);

        public readonly record struct Result(PrefabDocument Document, IReadOnlyList<string> Problems);

        /// <param name="current">The freshly read document.</param>
        /// <param name="states">Scene entities. Missing objects count as deletions, except override
        /// carriers, which have no scene nodes.</param>
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
                // Override carriers have no scene node; absence is not a deletion.
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

            // The v6 runtime requires explicit metadata and placement; repair missing components here.
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

        private static PrefabObject Create(ObjectState state)
        {
            var result = new PrefabObject();
            result.Components.Add(MetaFor(state));
            result.Components.Add(LocalTransformCodec.Write(state.Transform));
            AppendAdded(result, state, existing: null);
            return result;
        }

        /// <summary>New components need their full payload from the scene.</summary>
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

        /// <summary>Update name and parent while preserving unknown metadata fields.</summary>
        private static PrefabComponent Meta(PrefabComponent component, ObjectState state)
        {
            var data = new CanonicalTomlTable();
            foreach (var (key, value) in component.Data)
            {
                switch (key)
                {
                    case WellKnownComponents.Name when state.Name is { Length: > 0 } name:
                        data.Add(key, name);
                        break;
                    case WellKnownComponents.Parent when state.Parent is { } parent:
                        data.Add(key, DocumentGuid.Format(parent));
                        break;
                    // Root objects omit parent; an empty GUID would be a broken reference.
                    case WellKnownComponents.Parent:
                        break;
                    default:
                        data.Add(key, value);
                        break;
                }
            }

            if (!data.ContainsKey(WellKnownComponents.Guid)) data.Add(WellKnownComponents.Guid, DocumentGuid.Format(state.Guid));
            if (!data.ContainsKey(WellKnownComponents.Name) && state.Name is { Length: > 0 } added) data.Add(WellKnownComponents.Name, added);
            if (!data.ContainsKey(WellKnownComponents.Parent) && state.Parent is { } gained)
            {
                data.Add(WellKnownComponents.Parent, DocumentGuid.Format(gained));
            }

            return new PrefabComponent(component.Id, component.Type, data, component.Removed);
        }

        private static PrefabComponent MetaFor(ObjectState state) =>
            PrefabObject.WithMeta(state.Guid, state.Name, state.Parent).Components[0];

        /// <summary>Reuse untouched transforms so canonical output keeps the same bytes.</summary>
        private static PrefabComponent Transform(PrefabComponent component, LocalTransform now)
        {
            var authored = LocalTransformCodec.Read(component.Data);
            return Unchanged(authored, now) ? component : LocalTransformCodec.Write(now);
        }

        private static PrefabComponent Edited(PrefabComponent component, string id, ObjectState state)
        {
            var prefix = id + "/";
            var edited = state.Edits.FieldsOf(id)
                .Concat((state.HostBaked ?? [])
                    .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(key => key[prefix.Length..]))
                .Distinct(StringComparer.Ordinal);

            var data = component.Data;
            foreach (var path in edited)
            {
                if (state.Values.TryGetValue(prefix + path, out var value)) data = Set(data, path, value);
            }

            return ReferenceEquals(data, component.Data)
                ? component
                : new PrefabComponent(component.Id, component.Type, data, component.Removed);
        }

        /// <summary>Set a slash-separated path while preserving key order.</summary>
        /// <remarks>Canonical tables are append-only and serialize in key order, so edits rebuild
        /// around each key's existing position.</remarks>
        private static CanonicalTomlTable Set(CanonicalTomlTable table, string path, AuthoredValue value)
        {
            int slash = path.IndexOf('/');
            var key = slash < 0 ? path : path[..slash];

            object? replacement;
            if (slash < 0)
            {
                replacement = ToCanonical(value);
                // Unreadable edits must not delete existing data.
                if (replacement is null) return table;
            }
            else
            {
                var nested = table.Value(key) as CanonicalTomlTable ?? new CanonicalTomlTable();
                replacement = Set(nested, path[(slash + 1)..], value);
            }

            var rebuilt = new CanonicalTomlTable();
            foreach (var (name, held) in table)
            {
                rebuilt.Add(name, string.Equals(name, key, StringComparison.Ordinal) ? replacement : held);
            }

            if (!table.ContainsKey(key)) rebuilt.Add(key, replacement);
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
            // The codec emits an inline table; a header table would no longer parse as a reference.
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

        /// <summary>Use absolute tolerance near zero, where relative tolerance would vanish.</summary>
        private static bool Close(float authored, float now)
        {
            var difference = MathF.Abs(authored - now);
            var magnitude = MathF.Max(MathF.Abs(authored), MathF.Abs(now));
            return difference <= TransformEpsilon * MathF.Max(1f, magnitude);
        }
    }
}
#endif

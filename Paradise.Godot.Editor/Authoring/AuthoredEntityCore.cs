#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using Paradise.Authoring;
using Paradise.Export.Data;
using Paradise.Assets.Documents;
using ParadiseGodot.Documents;
using ParadiseGodot.Project;

namespace ParadiseGodot.Authoring
{
    /// <summary>Schema-driven authoring logic for the consuming project's AuthoredEntityNode shim.</summary>
    /// <remarks>
    /// The shim owns the Godot hooks and <c>[GlobalClass]</c> registration. Godot needs a
    /// <c>res://</c> script to attach and serialize it; an assembly-only type is insufficient.
    /// Keep the hooks handwritten: Godot's source generator cannot see another generator's
    /// output, so generated exports or property hooks silently lose authored values.
    /// Identity, node transforms and host-object baking remain outside the schema.
    /// </remarks>
    public sealed class AuthoredEntityCore
    {
        // Composition avoids Godot's cross-assembly script inheritance reload bug
        // (godotengine/godot#75352); this core must remain a plain class.
        private readonly Node3D _host;

        public AuthoredEntityCore(Node3D host) => _host = host;

        /// <summary>Author changes since this entity was materialized.</summary>
        public AuthoredEdits Edits => _edits;

        // PackedScene replays stored properties before tree entry. Only writes in the tree
        // count as author edits, so reopening a workfile does not dirty untouched payloads.
        private bool IsAuthorEdit => _host.IsInsideTree();

        private const string GuidMetaKey = "paradise_entity_guid";
        private const string SchemaFileName = "authoring-schema.json";

        private const string ShimGlobalClassName = "AuthoredEntityNode";

        /// <summary>Create the consuming project's registered shim through its script resource.</summary>
        /// <remarks>
        /// A bare Node3D would lose authored values when packed. Resolving the global class path
        /// also supports addons relocated with ParadiseGodotAddonDir.
        /// </remarks>
        /// <returns>The new entity, or null if its shim cannot be found or loaded.</returns>
        public static IAuthoredEntity? CreateNode()
        {
            foreach (Godot.Collections.Dictionary entry in ProjectSettings.GetGlobalClassList())
            {
                if (entry["class"].AsString() != ShimGlobalClassName)
                {
                    continue;
                }

                string path = entry["path"].AsString();
                if (GD.Load<Script>(path) is not Script script)
                {
                    GD.PushError($"[Paradise] Could not load '{path}'.");
                    return null;
                }

                return script.Call("new").As<Node3D>() as IAuthoredEntity;
            }

            GD.PushError(
                $"[Paradise] No '{ShimGlobalClassName}' script in this project. It is part of the " +
                "Paradise.Godot.Editor package payload and belongs at " +
                "res://addons/paradise/Authoring/AuthoredEntityNode.cs; rebuild to restore it.");
            return null;
        }

        /// <summary>Per-component toggle, stored within the component group.</summary>
        private const string EnabledSuffix = "/Enabled";

        /// <summary>Host reference that supplies values for the whole component.</summary>
        private const string SourceSuffix = "/Source";

        /// <summary>Transient add action; must not be stored in the workfile.</summary>
        private const string AddProperty = "Add Component";

        private const string AddNone = "(add…)";

        private readonly List<ComponentSchema> _components = new();
        private readonly Dictionary<string, ComponentSchema> _byId = new(StringComparer.Ordinal);

        // String enums return item text. Rebuild this label-to-id map with the menu
        // in BuildPropertyList so it cannot resolve stale labels.
        private readonly Dictionary<string, string> _byAddLabel = new(StringComparer.Ordinal);
        private readonly HashSet<string> _enabled = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Variant> _values = new(StringComparer.Ordinal);

        // The writer applies only these edits over the document it re-reads.
        private readonly AuthoredEdits _edits = new();
        private bool _loaded;

        // Schema fingerprint used to detect re-dumps without reopening the project.
        private ulong _schemaStamp;
        private MeshInstance3D? _wire;

        private sealed class ComponentSchema
        {
            /// <summary>Canonical GUID text used in Godot property names: &lt;id&gt;/&lt;path&gt;.</summary>
            public string Id = "";

            /// <summary>Fully qualified CLR name used to disambiguate display names.</summary>
            public string Type = "";

            public string DisplayName = "";
            /// <summary>Host kind supplying the whole component, or null.</summary>
            public string? AuthoredBy;
            public AuthoredGizmoSchema? Gizmo;
            /// <summary>Leaves with slash-separated paths within the component.</summary>
            public readonly List<SchemaField> Fields = new();
            public readonly List<HostRef> Hosts = new();
        }

        private readonly record struct SchemaField(
            string Path,
            Variant.Type Type,
            double? Minimum,
            double? Maximum,
            IReadOnlyList<string>? EnumValues,
            string? GuardField,
            JsonElement? GuardValue,
            Variant Default,
            /// <summary>File-picker extensions for a path stored verbatim, without reference baking.</summary>
            IReadOnlyList<string>? AssetKinds);

        private readonly record struct HostRef(
            string Path,
            string Kind,
            bool IsList,
            IReadOnlyList<string>? AssetKinds,
            /// <summary>Declared leaves the bake may fill. Records can request different
            /// fields from the same host object, such as Size or SizeX/SizeY/SizeZ.</summary>
            IReadOnlyList<string> Fields);

        // Schema

        private void EnsureSchema()
        {
            if (_loaded)
            {
                return;
            }
            // Latch before loading to avoid repeating initialization errors on every redraw.
            // Catch all load errors so inspector redraws do not fail silently.
            _loaded = true;
            _schemaStamp = SchemaStamp();

            try
            {
                LoadSchema();
            }
            catch (Exception e)
            {
                GD.PushError($"[Paradise.Export] The authoring schema could not be loaded: {e}");
            }
        }

        /// <summary>Reload changed schemas so new components appear without reopening the project.</summary>
        /// <remarks>Called only from BuildPropertyList: one file stat per inspector rebuild.</remarks>
        private void RefreshSchemaIfChanged()
        {
            EnsureSchema();

            ulong stamp = SchemaStamp();
            if (stamp == _schemaStamp)
            {
                return;
            }

            // Preserve authored values and enabled components across schema changes.
            // Advance the stamp only after loading returns, so thrown failures can be retried.
            try
            {
                LoadSchema();
            }
            catch (Exception e)
            {
                GD.PushError($"[Paradise.Export] The authoring schema could not be reloaded: {e}");
                return;
            }
            _schemaStamp = stamp;
        }

        /// <summary>Schema modification time and length, or zero when the file is absent.</summary>
        private static ulong SchemaStamp()
        {
            if (SchemaPath() is not { } path || !System.IO.File.Exists(path))
            {
                return 0;
            }

            var info = new System.IO.FileInfo(path);
            return ((ulong)info.LastWriteTimeUtc.Ticks * 31) ^ (ulong)info.Length;
        }

        private static string? s_schemaPath;
        private static bool s_schemaPathResolved;

        /// <summary>The game's .editor/authoring-schema.json, or null without an asset project.</summary>
        /// <remarks>Resolved once per session because the project root stays fixed.</remarks>
        private static string? SchemaPath()
        {
            if (s_schemaPathResolved) return s_schemaPath;
            s_schemaPathResolved = true;

            if (ParadiseProject.TryOpen(out var project, out var problem))
            {
                using (project)
                {
                    s_schemaPath = project.Files.ConvertPathToInternal(project.Layout.Editor / SchemaFileName);
                }
            }
            else
            {
                GD.PushError($"[Paradise] No authoring schema can be read: {problem}");
            }

            return s_schemaPath;
        }

        private void LoadSchema()
        {
            _components.Clear();
            _byId.Clear();

            // The game's dump is the sole schema. ParadiseAuthoringScanReferences includes
            // referenced assemblies; the engine no longer publishes a separate component schema.
            string? gamePath = SchemaPath();
            if (gamePath is null) return;

            string text = System.IO.File.Exists(gamePath)
                ? System.IO.File.ReadAllText(gamePath)
                : "";
            if (text.Length == 0)
            {
                GD.PushWarning(
                    $"[Paradise] '{gamePath}' is missing, so no components can be authored. Build the " +
                    "game's launcher once (`paradise host build`) — its build dumps the schema there.");
                return;
            }

            AuthoringSchemaDocument document;
            try
            {
                document = AuthoringSchemaReader.Read(text);
            }
            catch (Exception e)
            {
                GD.PushError($"[Paradise.Export] '{gamePath}' is not a readable authoring schema: {e.Message}");
                return;
            }

            foreach (AuthoredComponentSchema source in AuthoringSchemaReader.Merge([document]).Components)
            {
                var component = new ComponentSchema
                {
                    Id = source.Id.ToString(),
                    Type = source.Type,
                    // Use the CLR type as a readable fallback for an unnamed component.
                    DisplayName = string.IsNullOrEmpty(source.DisplayName)
                        ? source.Type
                        : source.DisplayName,
                    AuthoredBy = source.AuthoredBy,
                    Gizmo = source.Gizmo is { Kind: "box" } gizmo ? gizmo : null,
                };
                ReadFields(source.Fields, "", component);
                _components.Add(component);
                _byId[component.Id] = component;
            }
        }

        /// <summary>Flatten fields into slash-separated paths shared by inspector groups and export.</summary>
        private static void ReadFields(List<AuthoredFieldSchema> fields, string prefix, ComponentSchema into)
        {
            foreach (AuthoredFieldSchema field in fields)
            {
                string path = prefix + field.Name;

                if (field.Type == AuthoredFieldTypes.Array)
                {
                    // Only arrays of host references have an inspector control.
                    if (field.Items is { AuthoredBy: { } listKind })
                    {
                        into.Hosts.Add(new HostRef(
                            path, listKind, IsList: true, field.Items.AssetKinds, LeafNames(field.Items.Fields)));
                    }
                    else
                    {
                        GD.PushWarning(
                            $"[Paradise.Export] '{path}' is an array of typed rows, which this editor "
                            + "cannot draw yet. It is not authored here.");
                    }
                    continue;
                }

                if (field.AuthoredBy is { } kind)
                {
                    into.Hosts.Add(new HostRef(
                        path, kind, IsList: false, field.AssetKinds, LeafNames(field.Fields)));
                    // Nested fields are bake outputs, not editable inputs.
                    continue;
                }

                if (field.Fields is { Count: > 0 } nested)
                {
                    ReadFields(nested, path + "/", into);
                    continue;
                }

                Variant.Type type = VariantTypeOf(field.Type);
                into.Fields.Add(new SchemaField(
                    path,
                    type,
                    field.Minimum,
                    field.Maximum,
                    field.Values,
                    field.VisibleWhen?.Field,
                    field.VisibleWhen?.EqualTo,
                    DefaultOf(type, field),
                    field.AssetKinds));
            }
        }

        /// <summary>Declared leaf names a bake may fill.</summary>
        private static IReadOnlyList<string> LeafNames(List<AuthoredFieldSchema>? fields) =>
            fields is null ? Array.Empty<string>() : fields.Select(f => f.Name).ToList();

        /// <summary>Read defaults at their schema type.</summary>
        /// <remarks>Reading a JSON boolean as a double throws InvalidOperationException.</remarks>
        private static Variant DefaultOf(Variant.Type type, AuthoredFieldSchema field)
        {
            if (field.Default is not { } value)
            {
                return type switch
                {
                    Variant.Type.Bool => Variant.From(false),
                    Variant.Type.Int => Variant.From(0L),
                    Variant.Type.String => Variant.From(EnumFallback(field)),
                    _ => Variant.From(0d),
                };
            }

            return type switch
            {
                Variant.Type.Bool => Variant.From(value.ValueKind == JsonValueKind.True),
                Variant.Type.Int => Variant.From(value.TryGetInt64(out long i) ? i : 0L),
                Variant.Type.String => Variant.From(value.GetString() ?? EnumFallback(field)),
                _ => Variant.From(value.TryGetDouble(out double d) ? d : 0d),
            };
        }

        /// <summary>Choose a legal enum member when no default is declared.</summary>
        private static string EnumFallback(AuthoredFieldSchema field) =>
            field.Values is { Count: > 0 } values ? values[0] : "";

        /// <summary>Enum strings store member names, matching the document contract.</summary>
        private static Variant.Type VariantTypeOf(string schemaType) => schemaType switch
        {
            AuthoredFieldTypes.Float => Variant.Type.Float,
            AuthoredFieldTypes.Int => Variant.Type.Int,
            AuthoredFieldTypes.Bool => Variant.Type.Bool,
            AuthoredFieldTypes.Vector2 => Variant.Type.Vector2,
            AuthoredFieldTypes.Vector3 => Variant.Type.Vector3,
            AuthoredFieldTypes.Quaternion => Variant.Type.Quaternion,
            AuthoredFieldTypes.Color => Variant.Type.Color,
            _ => Variant.Type.String,
        };

        // Inspector

        /// <summary>Build a unique, readable menu label, qualifying shared names with the CLR type.</summary>
        /// <remarks>Remove commas because Godot splits enum hints on them; empty or colliding
        /// labels fall back to the component id.</remarks>
        private string AddLabel(ComponentSchema component)
        {
            string name = EnumSafe(component.DisplayName);
            bool shared = _components.Count(c => EnumSafe(c.DisplayName) == name) > 1;
            string label = shared ? EnumSafe($"{component.DisplayName} ({component.Type})") : name;
            return label.Length > 0 && !_byAddLabel.ContainsKey(label) ? label : component.Id;
        }

        private static string EnumSafe(string text) => text.Replace(',', ' ').Trim();

        public global::Godot.Collections.Array<global::Godot.Collections.Dictionary> BuildPropertyList()
        {
            RefreshSchemaIfChanged();
            var list = new global::Godot.Collections.Array<global::Godot.Collections.Dictionary>();

            // Show carried components; put the rest in a menu with readable, unique labels.
            _byAddLabel.Clear();
            var addable = new List<string> { AddNone };
            foreach (ComponentSchema component in _components.Where(c => !_enabled.Contains(c.Id)))
            {
                string label = AddLabel(component);
                _byAddLabel[label] = component.Id;
                addable.Add(label);
            }
            // Default includes Storage, which would persist this transient menu value.
            list.Add(InspectorProperty(AddProperty, Variant.Type.String,
                PropertyUsageFlags.Editor, PropertyHint.Enum, string.Join(",", addable)));

            foreach (ComponentSchema component in _components)
            {
                if (!_enabled.Contains(component.Id))
                {
                    continue;
                }

                list.Add(InspectorProperty(component.DisplayName, Variant.Type.Nil,
                    PropertyUsageFlags.Group, hintString: component.Id + "/"));

                // Preserve the stored toggle so older workfiles load and components can be removed.
                list.Add(InspectorProperty(component.Id + EnabledSuffix, Variant.Type.Bool));

                if (component.AuthoredBy is { } componentKind)
                {
                    list.Add(HostPicker(component.Id + SourceSuffix, componentKind, isList: false, null));
                }


                foreach (HostRef host in component.Hosts)
                {
                    // Identity, name and placement come from the entity itself, so need no picker.
                    if (IsSelfSupplied(host.Kind)) continue;

                    list.Add(HostPicker(
                        component.Id + "/" + host.Path, host.Kind, host.IsList, host.AssetKinds));
                }

                foreach (SchemaField field in component.Fields)
                {
                    if (!IsVisible(component, field))
                    {
                        continue;
                    }

                    var entry = InspectorProperty(component.Id + "/" + field.Path, field.Type);

                    if (field.AssetKinds is { Count: > 0 } fileKinds)
                    {
                        // This path is stored verbatim; it is not a baked asset reference.
                        entry["hint"] = (int)PropertyHint.File;
                        entry["hint_string"] = string.Join(",", fileKinds.Select(k => "*" + k));
                    }
                    else if (field.EnumValues is { Count: > 0 } values)
                    {
                        entry["hint"] = (int)PropertyHint.Enum;
                        entry["hint_string"] = string.Join(",", values);
                    }
                    else if (field.Minimum is { } min && field.Maximum is { } max)
                    {
                        // Advisory: only the runtime can validate against the rest of the configuration.
                        entry["hint"] = (int)PropertyHint.Range;
                        entry["hint_string"] = $"{min},{max}";
                    }
                    list.Add(entry);
                }
            }

            return list;
        }

        private static global::Godot.Collections.Dictionary InspectorProperty(
            string name, Variant.Type type,
            PropertyUsageFlags usage = PropertyUsageFlags.Default,
            PropertyHint hint = PropertyHint.None, string? hintString = null)
        {
            var property = new global::Godot.Collections.Dictionary
            {
                { "name", name },
                { "type", (int)type },
                { "usage", (int)usage },
            };
            if (hint != PropertyHint.None) property["hint"] = (int)hint;
            if (hintString is not null) property["hint_string"] = hintString;
            return property;
        }

        /// <summary>A Godot object picker filtered by the schema host kind.</summary>
        private static global::Godot.Collections.Dictionary HostPicker(
            string name, string kind, bool isList, IReadOnlyList<string>? assetKinds)
        {
            if (kind is AuthoredBySources.Asset or AuthoredBySources.Mesh)
            {
                // Translate semantic asset kinds into Godot filters here, keeping the schema
                // host-neutral. Mesh defaults accept geometry documents and their source GLBs.
                string filter = assetKinds is { Count: > 0 }
                    ? string.Join(",", assetKinds.Select(k => "*" + k).Concat(kind == AuthoredBySources.Mesh ? ["*.glb", "*.gltf"] : []))
                    : kind == AuthoredBySources.Mesh ? "*.mesh,*.skinnedmesh,*.glb,*.gltf" : "*";
                // GlobalFile includes assets/ despite .gdignore; File would hide the source tree.
                // The bake separately rejects paths outside assets/.
                return InspectorProperty(name, Variant.Type.String,
                    hint: PropertyHint.GlobalFile, hintString: filter);
            }

            string nodeType = kind switch
            {
                AuthoredBySources.Shape => "CollisionShape3D",
                // Sprite and sprite-sheet references read geometry from the same node.
                AuthoredBySources.Sprite or AuthoredBySources.SpriteSheet => "Sprite3D",
                AuthoredBySources.Light => "Light3D",
                AuthoredBySources.Camera => "Camera3D",
                AuthoredBySources.Environment => "WorldEnvironment",
                // The shim lives in the consuming assembly; Godot filters by its registered class name.
                AuthoredBySources.Entity => ShimGlobalClassName,
                _ => "Node3D",
            };

            return isList
                ? InspectorProperty(name, Variant.Type.Array, hint: PropertyHint.TypeString,
                    hintString: $"{(int)Variant.Type.NodePath}/{(int)PropertyHint.NodePathValidTypes}:{nodeType}")
                : InspectorProperty(name, Variant.Type.NodePath,
                    hint: PropertyHint.NodePathValidTypes, hintString: nodeType);
        }

        /// <summary>Apply the schema visibility guard shared by authoring hosts.</summary>
        private bool IsVisible(ComponentSchema component, SchemaField field)
        {
            if (field.GuardField is not { } guard || field.GuardValue is not { } expected)
            {
                return true;
            }
            if (!_values.TryGetValue(component.Id + "/" + guard, out Variant actual))
            {
                return true;
            }

            return expected.ValueKind switch
            {
                JsonValueKind.True => actual.AsBool(),
                JsonValueKind.False => !actual.AsBool(),
                JsonValueKind.String => actual.AsString() == expected.GetString(),
                JsonValueKind.Number => Math.Abs(actual.AsDouble() - expected.GetDouble()) < 1e-9,
                _ => true,
            };
        }

        public Variant GetAuthored(StringName property)
        {
            EnsureSchema();
            string name = property.ToString();

            // Setting the picker performs an action; reading it always returns the resting value.
            if (name == AddProperty)
            {
                return AddNone;
            }
            if (name.EndsWith(EnabledSuffix, StringComparison.Ordinal))
            {
                return _enabled.Contains(name[..^EnabledSuffix.Length]);
            }
            return _values.TryGetValue(name, out Variant value) ? value : default;
        }

        public bool SetAuthored(StringName property, Variant value)
        {
            EnsureSchema();
            string name = property.ToString();

            if (name == AddProperty)
            {
                string chosen = value.AsString();
                // Accept menu labels and raw ids for fallback labels or scripted callers.
                if (!_byAddLabel.TryGetValue(chosen, out string? id))
                {
                    id = chosen;
                }
                if (_byId.TryGetValue(id, out ComponentSchema? added) && EnableComponent(added))
                {
                    OnAuthoredChanged();
                }
                // Reset the picker and remove the newly added component from its menu.
                _host.NotifyPropertyListChanged();
                return true;
            }

            if (name.EndsWith(EnabledSuffix, StringComparison.Ordinal))
            {
                string id = name[..^EnabledSuffix.Length];
                if (!_byId.TryGetValue(id, out ComponentSchema? component))
                {
                    return false;
                }
                if (value.AsBool())
                {
                    EnableComponent(component);
                }
                else if (_enabled.Remove(id))
                {
                    if (IsAuthorEdit) _edits.ComponentRemoved(id);
                    // Re-adding a removed component must not resurrect its old values.
                    foreach (string key in _values.Keys
                                 .Where(k => k.StartsWith(id + "/", StringComparison.Ordinal))
                                 .ToList())
                    {
                        _values.Remove(key);
                    }
                }
                _host.NotifyPropertyListChanged();
                OnAuthoredChanged();
                return true;
            }

            if (!IsKnownProperty(name))
            {
                return false;
            }
            _values[name] = value;
            if (IsAuthorEdit)
            {
                int slash = name.IndexOf('/');
                _edits.FieldChanged(name[..slash], name[(slash + 1)..]);
            }
            // Refresh fields whose visibility depends on the changed value.
            _host.NotifyPropertyListChanged();
            OnAuthoredChanged();
            return true;
        }

        private bool IsKnownProperty(string name)
        {
            int slash = name.IndexOf('/');
            if (slash < 0 || !_byId.TryGetValue(name[..slash], out ComponentSchema? component))
            {
                return false;
            }
            string path = name[(slash + 1)..];
            return path == "Source"
                || component.Hosts.Any(h => h.Path == path)
                || component.Fields.Any(f => f.Path == path);
        }

        private bool EnableComponent(ComponentSchema component)
        {
            if (!_enabled.Add(component.Id)) return false;

            foreach (SchemaField field in component.Fields)
            {
                _values[component.Id + "/" + field.Path] = field.Default;
            }
            if (IsAuthorEdit) _edits.ComponentAdded(component.Id);
            return true;
        }

        /// <summary>Seed document components without recording edits.</summary>
        /// <remarks>
        /// Unknown components stay in the document: the writer re-reads it and applies only edits.
        /// Meta and transform belong to the node, so they are omitted from the component inspector.
        /// </remarks>
        public void AdoptDocumentComponents(IReadOnlyList<PrefabComponent> components)
        {
            ArgumentNullException.ThrowIfNull(components);
            EnsureSchema();

            foreach (PrefabComponent component in components)
            {
                if (component.Id == WellKnownComponents.MetaId ||
                    component.Id == WellKnownComponents.TransformId)
                {
                    continue;
                }

                string id = component.Id.ToString();
                if (!_byId.TryGetValue(id, out ComponentSchema? schema))
                {
                    continue;
                }

                _enabled.Add(id);
                foreach (SchemaField field in schema.Fields)
                {
                    AuthoredValue read = AuthoredPayload.Read(component.Data, field.Path, field.Type);
                    // Missing or incompatible values retain the schema default.
                    _values[id + "/" + field.Path] = ToVariant(read, field.Type, field.Default);
                }
            }

            _host.NotifyPropertyListChanged();
        }

        /// <summary>Enabled components' values, keyed &lt;componentId&gt;/&lt;path&gt;.</summary>
        /// <remarks>Neutral AuthoredValue values keep the document merge testable outside Godot.</remarks>
        public IReadOnlyDictionary<string, AuthoredValue> AuthoredValues()
        {
            EnsureSchema();
            var values = new Dictionary<string, AuthoredValue>(StringComparer.Ordinal);
            foreach (ComponentSchema component in _components)
            {
                if (!_enabled.Contains(component.Id)) continue;

                foreach (SchemaField field in component.Fields)
                {
                    string key = component.Id + "/" + field.Path;
                    Variant value = _values.TryGetValue(key, out Variant stored) ? stored : field.Default;
                    values[key] = FromVariant(value, field.Type);
                }
            }

            return values;
        }

        private static AuthoredValue FromVariant(Variant value, Variant.Type type) => type switch
        {
            Variant.Type.Bool => HostObjectBaker.Boolean(value.AsBool()),
            Variant.Type.Int => HostObjectBaker.Integer(value.AsInt64()),
            Variant.Type.Float => HostObjectBaker.Number(value.AsDouble()),
            Variant.Type.String => HostObjectBaker.Text(value.AsString()),
            Variant.Type.Vector2 => HostObjectBaker.Numbers(value.AsVector2().X, value.AsVector2().Y),
            Variant.Type.Vector3 => HostObjectBaker.Numbers(value.AsVector3().X, value.AsVector3().Y, value.AsVector3().Z),
            Variant.Type.Quaternion => HostObjectBaker.Numbers(
                value.AsQuaternion().X, value.AsQuaternion().Y,
                value.AsQuaternion().Z, value.AsQuaternion().W),
            Variant.Type.Color => HostObjectBaker.Rgba(value.AsColor()),
            _ => AuthoredValue.None,
        };

        /// <summary>Convert neutral values at the Godot boundary.</summary>
        /// <remarks>Constructing a Variant outside Godot segfaults the test host; keep testable
        /// conversion logic in <see cref="AuthoredPayload"/>.</remarks>
        private static Variant ToVariant(AuthoredValue value, Variant.Type type, Variant fallback) =>
            (type, value.Kind) switch
            {
                (Variant.Type.Bool, AuthoredValueKind.Bool) => value.Bool,
                (Variant.Type.Int, AuthoredValueKind.Integer) => value.Integer,
                (Variant.Type.Float, AuthoredValueKind.Number) => value.Number,
                (Variant.Type.String, AuthoredValueKind.Text or AuthoredValueKind.Reference) => value.Text ?? "",
                (Variant.Type.Vector2, AuthoredValueKind.Numbers) =>
                    new Vector2(value.Numbers![0], value.Numbers[1]),
                (Variant.Type.Vector3, AuthoredValueKind.Numbers) =>
                    new Vector3(value.Numbers![0], value.Numbers[1], value.Numbers[2]),
                (Variant.Type.Quaternion, AuthoredValueKind.Numbers) =>
                    new Quaternion(value.Numbers![0], value.Numbers[1], value.Numbers[2], value.Numbers[3]),
                (Variant.Type.Color, AuthoredValueKind.Rgba) =>
                    new Color(value.Numbers![0], value.Numbers[1], value.Numbers[2], value.Numbers[3]),
                _ => fallback,
            };

        // Model path and identity

        /// <summary>The entity's .mesh/.skinnedmesh document or source GLB under assets/.</summary>
        /// <remarks>Setting it enables its component. The first schema field accepting a mesh
        /// supplies the model; baking converts either path form into a document reference.</remarks>
        public string ModelPath
        {
            get => ModelField() is { } slot ? StoredValue(slot.Component, slot.Path).AsString() : "";
            set
            {
                if (ModelField() is not { } slot)
                {
                    GD.PushWarning(
                        $"[Paradise] '{_host.Name}': no authored component declares a mesh field, "
                        + "so there is nowhere to put a model. Declare one with "
                        + "[AuthoredByHost<HostMesh>] and [AuthorAssetKinds(\".mesh\")].");
                    return;
                }
                SetAuthored(slot.Component, slot.Path, value);
            }
        }

        /// <summary>The first schema field accepting a model reference, or null.</summary>
        private (string Component, string Path)? ModelField()
        {
            EnsureSchema();
            foreach (ComponentSchema component in _components)
            {
                foreach (HostRef host in component.Hosts)
                {
                    if (host.Kind == AuthoredBySources.Mesh ||
                        (host.Kind == AuthoredBySources.Asset && AcceptsModel(host.AssetKinds)))
                    {
                        return (component.Id, host.Path);
                    }
                }
            }

            return null;
        }

        private static bool AcceptsModel(IReadOnlyList<string>? assetKinds) =>
            assetKinds is not null &&
            assetKinds.Any(kind =>
                kind.Equals(MeshReferenceDocument.MeshSuffix, StringComparison.OrdinalIgnoreCase) ||
                kind.Equals(MeshReferenceDocument.SkinnedMeshSuffix, StringComparison.OrdinalIgnoreCase));

        private Variant StoredValue(string componentId, string field) =>
            _values.TryGetValue(componentId + "/" + field, out Variant value) ? value : default;

        private void SetAuthored(string componentId, string field, Variant value)
        {
            ComponentSchema component = _byId[componentId];
            EnableComponent(component);
            _values[componentId + "/" + field] = value;
            if (IsAuthorEdit) _edits.FieldChanged(componentId, field);
        }

        /// <summary>Stable per-placement identity; <see cref="Guid.Empty"/> until minted.</summary>
        public Guid EntityGuid =>
            _host.HasMeta(GuidMetaKey) && Guid.TryParse(_host.GetMeta(GuidMetaKey).AsString(), out Guid g) ? g : Guid.Empty;

        /// <summary>Restore document identity when rebuilding a node; rejects Guid.Empty.</summary>
        public bool RestoreEntityGuid(Guid value)
        {
            if (value == Guid.Empty)
            {
                return false;
            }
            _host.SetMeta(GuidMetaKey, value.ToString("N"));
            return true;
        }

        /// <summary>Mint and persist an identity if absent, including for unsaved entities.</summary>
        public Guid EnsureEntityGuid()
        {
            Guid current = EntityGuid;
            if (current != Guid.Empty)
            {
                return current;
            }

            Guid minted = Guid.NewGuid();
            _host.SetMeta(GuidMetaKey, minted.ToString("N"));
            return minted;
        }

        public void OnNotification(int what)
        {
            if (what == Node.NotificationEditorPreSave)
            {
                EnsureUniqueGuid();
            }
        }

        // Duplicated nodes inherit metadata; regenerate this GUID if another entity already owns it.
        private void EnsureUniqueGuid()
        {
            EnsureEntityGuid();

            Node? sceneRoot = _host.GetTree()?.EditedSceneRoot;
            if (sceneRoot is null)
            {
                return;
            }

            foreach (Node node in Descendants(sceneRoot))
            {
                if (node != _host && node is IAuthoredEntity other && other.EntityGuid == EntityGuid)
                {
                    _host.SetMeta(GuidMetaKey, Guid.NewGuid().ToString("N"));
                    return;
                }
            }
        }

        private static IEnumerable<Node> Descendants(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                yield return child;
                foreach (Node descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }

        // Host references

        /// <summary>Baked host values, keyed &lt;componentId&gt;/&lt;path&gt;.</summary>
        /// <remarks>Recompute on every save: referenced objects can change independently of this
        /// inspector. Documents store their values, since node paths have no runtime meaning.</remarks>
        /// <param name="assets">Resolves asset identities. Without a project, asset references are
        /// skipped rather than stored as bare paths.</param>
        public IReadOnlyDictionary<string, AuthoredValue> BakedHostValues(AssetReferenceResolver? assets = null)
        {
            EnsureSchema();
            var baked = new Dictionary<string, AuthoredValue>(StringComparer.Ordinal);

            foreach (ComponentSchema component in _components)
            {
                if (!_enabled.Contains(component.Id))
                {
                    continue;
                }

                if (component.AuthoredBy is { } wholeKind)
                {
                    // Host leaves supplement typed fields, such as sprite geometry beside authored fps.
                    BakeRef(
                        new HostRef("", wholeKind, IsList: false, null, Array.Empty<string>()),
                        component, assets, baked);
                }

                foreach (HostRef host in component.Hosts)
                {
                    BakeRef(host, component, assets, baked);
                }
            }

            return baked;
        }

        private void BakeRef(
            HostRef host,
            ComponentSchema component,
            AssetReferenceResolver? assets,
            Dictionary<string, AuthoredValue> into)
        {
            string prefix = component.Id + "/";
            string at = host.Path;

            if (SelfSupplied(host.Kind) is { } own)
            {
                into[prefix + at] = own;
                return;
            }

            string key = at.Length == 0 ? component.Id + SourceSuffix : prefix + at;
            if (!_values.TryGetValue(key, out Variant stored))
            {
                return;
            }

            // File pickers store strings. Older workfiles store mesh NodePaths, which
            // the node-reference flow below resolves to the same document.
            if (host.Kind == AuthoredBySources.Asset ||
                (host.Kind == AuthoredBySources.Mesh && stored.VariantType == Variant.Type.String))
            {
                string file = stored.AsString();
                if (string.IsNullOrEmpty(file)) return;

                if (assets is null)
                {
                    GD.PushWarning(
                        $"[Paradise] '{_host.Name}': '{host.Path}' references '{file}', but no asset " +
                        "project is open to give it an identity, so it is not saved.");
                    return;
                }

                AuthoredValue? reference = host.Kind == AuthoredBySources.Mesh
                    ? assets.MeshDocument(file)
                    : assets.Reference(file);
                if (reference is { } value) into[prefix + at] = value;
                return;
            }

            if (host.IsList)
            {
                // Canonical documents do not yet support leaves for reference-list elements.
                GD.PushWarning(
                    $"[Paradise] '{_host.Name}': '{host.Path}' is a LIST of {host.Kind} references, " +
                    "which this addon cannot write to a document yet. It is not saved.");
                return;
            }

            if (BakeOne(host.Kind, stored.AsNodePath(), assets) is not { } leaves) return;

            if (leaves.Count == 1 && leaves.TryGetValue(string.Empty, out AuthoredValue single))
            {
                into[prefix + at] = single;
                return;
            }

            WarnOnShapeMismatch(host, leaves);
            IEnumerable<string> fields = host.Fields.Count > 0 ? host.Fields : leaves.Keys;
            foreach (string wanted in fields)
            {
                if (leaves.TryGetValue(wanted, out AuthoredValue value))
                {
                    into[prefix + (at.Length == 0 ? wanted : at + "/" + wanted)] = value;
                }
            }
        }

        private static bool IsSelfSupplied(string kind) =>
            kind is AuthoredBySources.Id or AuthoredBySources.Name or AuthoredBySources.Parent
                or AuthoredBySources.LocalPosition or AuthoredBySources.LocalRotation
                or AuthoredBySources.LocalScale;

        /// <summary>The sprite sheet reference, or null without a standalone image.</summary>
        private AuthoredValue? SheetReference(Sprite3D sprite, AssetReferenceResolver? assets)
        {
            if (sprite.Texture?.ResourcePath is not { Length: > 0 } texture) return null;

            // Sub-resources have no file to identify.
            if (texture.Contains("::", StringComparison.Ordinal))
            {
                GD.PushWarning(
                    $"[Paradise] '{_host.Name}': the sprite's sheet is a sub-resource ('{texture}'), " +
                    "which has no file to reference. Save the image as its own file under assets/.");
                return null;
            }

            return Reference(texture, assets);
        }

        private AuthoredValue? Reference(string file, AssetReferenceResolver? assets)
        {
            if (assets is not null) return assets.Reference(file);

            WarnNoProject(file);
            return null;
        }

        private AuthoredValue? MeshDocument(string file, AssetReferenceResolver? assets)
        {
            if (assets is not null) return assets.MeshDocument(file);

            WarnNoProject(file);
            return null;
        }

        private void WarnNoProject(string file) =>
            GD.PushWarning(
                $"[Paradise] '{_host.Name}': '{file}' cannot be given an identity with no asset " +
                "project open, so the reference is not saved.");

        private AuthoredValue? SelfSupplied(string kind) => kind switch
        {
            AuthoredBySources.Id => HostObjectBaker.Text(DocumentGuid.Format(EnsureEntityGuid())),
            AuthoredBySources.Name => HostObjectBaker.Text(_host.Name.ToString()),
            AuthoredBySources.Parent => HostObjectBaker.Text(ParentEntityGuid()),
            AuthoredBySources.LocalPosition =>
                HostObjectBaker.Numbers(_host.Position.X, _host.Position.Y, _host.Position.Z),
            AuthoredBySources.LocalRotation => HostObjectBaker.Numbers(
                _host.Quaternion.X, _host.Quaternion.Y, _host.Quaternion.Z, _host.Quaternion.W),
            AuthoredBySources.LocalScale =>
                HostObjectBaker.Numbers(_host.Scale.X, _host.Scale.Y, _host.Scale.Z),
            _ => null,
        };

        /// <summary>The nearest ancestor entity GUID; an empty string means no parent.</summary>
        private string ParentEntityGuid()
        {
            for (Node? node = _host.GetParent(); node is not null; node = node.GetParent())
            {
                if (node is IAuthoredEntity entity) return DocumentGuid.Format(entity.EnsureEntityGuid());
            }

            return string.Empty;
        }

        /// <summary>Warn when a record requests box extents from another shape.</summary>
        /// <remarks>Spheres and capsules leave SizeX/SizeY/SizeZ zero.</remarks>
        private void WarnOnShapeMismatch(HostRef host, Dictionary<string, AuthoredValue> leaves)
        {
            if (host.Kind != AuthoredBySources.Shape ||
                !host.Fields.Contains("SizeX") ||
                !leaves.TryGetValue("ShapeType", out AuthoredValue shape) ||
                shape.Text is not { } name ||
                name == "Box")
            {
                return;
            }

            GD.PushWarning(
                $"[Paradise] '{_host.Name}': '{host.Path}' asks for box extents but points at a "
                + $"{name} shape, which leaves them zero. Point it at a BoxShape3D, or give the "
                + "record the fields that shape fills (Size, Radius, Height).");
        }

        /// <summary>Bake a referenced object. Scalar results use the empty key, which maps
        /// to the reference's own path.</summary>
        private Dictionary<string, AuthoredValue>? BakeOne(
            string kind, NodePath path, AssetReferenceResolver? assets)
        {
            if (path.IsEmpty) return null;

            switch (kind)
            {
                case AuthoredBySources.Shape:
                    return _host.GetNodeOrNull<CollisionShape3D>(path) is { Shape: not null } shape
                        ? HostObjectBaker.BakeShape(_host, shape)
                        : null;

                case AuthoredBySources.Light:
                    return _host.GetNodeOrNull<Light3D>(path) is { } light
                        ? HostObjectBaker.BakeLight(light)
                        : null;

                case AuthoredBySources.Camera:
                    return _host.GetNodeOrNull<Camera3D>(path) is { } camera
                        ? HostObjectBaker.BakeCamera(camera)
                        : null;

                case AuthoredBySources.Sprite:
                case AuthoredBySources.SpriteSheet:
                    // BakeRef selects the leaves each sprite or sprite-sheet record declares.
                    return _host.GetNodeOrNull<Sprite3D>(path) is { } sprite
                        ? HostObjectBaker.BakeSprite(sprite, SheetReference(sprite, assets))
                        : null;

                case AuthoredBySources.Environment:
                    return _host.GetNodeOrNull<WorldEnvironment>(path) is { Environment: { } environment }
                        ? HostObjectBaker.BakeEnvironment(environment)
                        : null;

                case AuthoredBySources.Mesh:
                {
                    if (_host.GetNodeOrNull<Node>(path) is not { } node) return null;

                    // Runtime references name the mesh document extracted from the GLB, not the source GLB.
                    string? source = HostObjectBaker.SourceGlbOf(node)
                        ?? HostObjectBaker.ModelDescendants(node)
                            .Select(HostObjectBaker.SourceGlbOf)
                            .FirstOrDefault(p => p is not null);
                    if (source is null || MeshDocument(source, assets) is not { } mesh) return null;

                    return new Dictionary<string, AuthoredValue>(StringComparer.Ordinal)
                    {
                        [string.Empty] = mesh,
                    };
                }

                case AuthoredBySources.Entity:
                {
                    // Entity names are not unique; references use the target metadata GUID.
                    if (_host.GetNodeOrNull<Node>(path) is not IAuthoredEntity target) return null;

                    return new Dictionary<string, AuthoredValue>(StringComparer.Ordinal)
                    {
                        [string.Empty] = HostObjectBaker.Text(
                            DocumentGuid.Format(target.EnsureEntityGuid())),
                    };
                }

                default:
                    return null;
            }
        }



        // Gizmo

        private void OnAuthoredChanged()
        {
            RefreshModelPreview();

            if (_wire is not null)
            {
                _wire.QueueFree();
                _wire = null;
            }

            foreach (ComponentSchema component in _components)
            {
                if (!_enabled.Contains(component.Id) || component.Gizmo is not { } box)
                {
                    continue;
                }

                float hx = FieldValue(component.Id, box.HalfExtentX);
                float hz = FieldValue(component.Id, box.HalfExtentZ);
                float depth = FieldValue(component.Id, box.Depth);
                if (hx <= 0f || hz <= 0f || depth <= 0f)
                {
                    continue;
                }

                DrawBox(hx, hz, depth);
                return;
            }
        }

        private void DrawBox(float hx, float hz, float depth)
        {
            var mesh = new ImmediateMesh();
            var material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            };

            // Y = 0 is the top: the surface an authored volume is measured from.
            var surface = new Color(0.35f, 0.75f, 1.00f, 1.0f);
            var below = new Color(0.30f, 0.45f, 0.60f, 0.9f);
            var post = new Color(0.45f, 0.60f, 0.75f, 0.9f);

            mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, material);
            Rectangle(mesh, hx, hz, 0f, surface);
            Rectangle(mesh, hx, hz, -depth, below);
            foreach ((float x, float z) in new[] { (-hx, -hz), (hx, -hz), (hx, hz), (-hx, hz) })
            {
                mesh.SurfaceSetColor(post);
                mesh.SurfaceAddVertex(new Vector3(x, 0f, z));
                mesh.SurfaceSetColor(post);
                mesh.SurfaceAddVertex(new Vector3(x, -depth, z));
            }
            mesh.SurfaceEnd();

            _wire = new MeshInstance3D { Name = "AuthoredGizmo", Mesh = mesh };
            // Internal children are skipped by GetChildren(), keeping the gizmo and its
            // material out of exports.
            _host.AddChild(_wire, forceReadableName: false, @internal: Node.InternalMode.Front);
        }

        private static void Rectangle(ImmediateMesh mesh, float hx, float hz, float y, Color color)
        {
            Vector3[] corners =
            {
                new(-hx, y, -hz), new(hx, y, -hz), new(hx, y, hz), new(-hx, y, hz),
            };
            for (int i = 0; i < corners.Length; i++)
            {
                mesh.SurfaceSetColor(color);
                mesh.SurfaceAddVertex(corners[i]);
                mesh.SurfaceSetColor(color);
                mesh.SurfaceAddVertex(corners[(i + 1) % corners.Length]);
            }
        }

        private float FieldValue(string componentId, string? fieldName) =>
            fieldName is not null && _values.TryGetValue(componentId + "/" + fieldName, out Variant value)
                ? (float)value.AsDouble()
                : 0f;

        public void OnReady() => OnAuthoredChanged();

        // Model preview

        private const string ModelPreviewName = "ModelPreview";
        private string? _previewedModel;
        private Node? _modelPreview;

        /// <summary>Rebuild the model preview when its field changes; never own or save it.</summary>
        /// <remarks>Project resolution runs per change. See <see cref="ModelPreview"/> for GLB loading.</remarks>
        private void RefreshModelPreview()
        {
            if (!Engine.IsEditorHint() || !_host.IsInsideTree()) return;

            string? model = ModelField() is { } slot && _enabled.Contains(slot.Component)
                ? StoredValue(slot.Component, slot.Path).AsString()
                : null;
            if (string.IsNullOrEmpty(model)) model = null;
            if (string.Equals(model, _previewedModel, StringComparison.Ordinal)) return;
            _previewedModel = model;

            if (_modelPreview is not null)
            {
                _modelPreview.QueueFree();
                _modelPreview = null;
            }
            if (model is null) return;

            if (!ParadiseProject.TryOpen(out var opened, out var problem))
            {
                GD.PushWarning($"[Paradise] '{_host.Name}': cannot preview '{model}': {problem}");
                return;
            }

            string? glb;
            string? mirror;
            using (opened)
            {
                glb = ModelDocuments.IsGlb(model)
                    ? opened.Files.ConvertPathToInternal(opened.Layout.Assets / model)
                    : AssetReferenceResolver.For(opened).SourceGlbOf(model);
                // Key mirrors by source GLB so multiple mesh documents share one scene.
                mirror = glb is null
                    ? null
                    : opened.Paths.MirrorModelFor(opened.Files.ConvertPathFromInternal(glb)) is { } path
                        ? opened.Paths.ToResourcePath(path)
                        : null;
            }
            if (glb is null) return;

            // Reuse the converted scene, falling back to the GLB for unconverted projects.
            var scene = mirror is null ? null : ModelMirror.Instantiate(mirror);
            if (scene is null)
            {
                scene = ModelPreview.Load(glb, out var failure);
                if (scene is null)
                {
                    GD.PushWarning($"[Paradise] '{_host.Name}': {failure}");
                    return;
                }
            }

            scene.Name = ModelPreviewName;
            scene.SetMeta(DocumentLoader.DerivedMetaKey, true);
            _host.AddChild(scene);
            _modelPreview = scene;
        }
    }
}
#endif

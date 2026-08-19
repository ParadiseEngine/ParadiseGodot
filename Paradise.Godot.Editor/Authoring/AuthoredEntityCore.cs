// Editor-only: this is an authoring surface that never runs in a game.
#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using Paradise.Authoring;
using Paradise.Export.Data;
using Paradise.Export.Paths;

namespace ParadiseGodot.Authoring
{
    /// <summary>
    /// THE entity node. One class for every entity there will ever be, and every component it can
    /// carry, built from the authoring schema rather than from fields declared here.
    ///
    /// It replaced <c>EntityExport</c>, which hardcoded 41 <c>[Export]</c> fields — nine of which
    /// any real scene ever used. Ticking a component in the inspector reveals that component's
    /// fields, with the schema's types, units, ranges, docs, defaults and visibility guards. Adding
    /// a component to the engine, or to a game, is an <c>[Authored]</c> record and a re-dump of the
    /// schema: nothing here changes.
    ///
    /// Three things stay real code, because no schema can carry them:
    ///
    /// - <b>Identity minting.</b> A GUID has to be generated and kept unique across the edited
    ///   scene. That is behaviour, and it lives here.
    /// - <b>Transform.</b> Position, rotation and scale belong to <see cref="Node3D"/>; the
    ///   exporter reads <c>GlobalTransform</c>. You keep moving nodes in the viewport.
    /// - <b>Baking references to values.</b> See <see cref="HostObjectBaker"/>.
    ///
    /// It is hand-written on purpose and must stay so. Godot's own ScriptPropertiesGenerator is a
    /// source generator, and two Roslyn generators cannot observe each other's output — a generated
    /// <c>[Export]</c> never reaches the inspector and a generated <c>_Set</c> is never called, so
    /// the scene's values are silently dropped with no error anywhere.
    /// </summary>
    /// <remarks>
    /// This is the BASE half, and it ships in the Paradise.Godot.Editor package. The type scenes
    /// actually attach is the five-line <c>AuthoredEntityNode</c> shim at
    /// <c>res://addons/paradise/Authoring/AuthoredEntityNode.cs</c>, which derives from this and
    /// exists only because Godot serializes a script binding as a res:// path plus uid - a type
    /// that lives only in an assembly cannot be attached to a node at all.
    ///
    /// No <c>[GlobalClass]</c> here: the shim carries it. Godot only registers global classes
    /// declared by res:// scripts, so the attribute on a packaged type would claim a registration
    /// it never gets.
    /// </remarks>
    public sealed class AuthoredEntityCore
    {
        /// <summary>
        /// The node this core drives. Everything Godot-facing goes through it rather than through
        /// inheritance, and that is not a style choice: a res:// script may not derive from a
        /// GodotObject-derived type declared in another assembly. Godot registers the base as a
        /// script type too and its ScriptTypeBiMap then rejects the duplicate on every assembly
        /// reload, which breaks editor hot-reload and pins the inspector to "might be out of date".
        /// See godotengine/godot#75352. Composition sidesteps it entirely: this type is a plain
        /// class, so Godot has no reason to look at it at all.
        /// </summary>
        private readonly Node3D _host;

        public AuthoredEntityCore(Node3D host) => _host = host;

        private const string GuidMetaKey = "paradise_entity_guid";
        private const string SchemaFileName = "authoring-schema.json";

        /// <summary>Name the shim registers under, and the handle used to find it.</summary>
        private const string ShimGlobalClassName = "AuthoredEntityNode";

        /// <summary>
        /// Builds a new entity node - the CONCRETE shim type, not this one.
        ///
        /// Package code cannot <c>new</c> it: the shim is in the consuming assembly, which this
        /// one does not reference. And constructing the base instead would produce a node with no
        /// res:// script, so packing it into a .tscn yields a bare Node3D - every authored value
        /// dropped, no error raised. So go through the script resource, which returns a real shim
        /// instance that is usable here as its base.
        ///
        /// The path is resolved through the global class list rather than hardcoded, so a project
        /// that relocated the addon (ParadiseGodotAddonDir) still works.
        /// </summary>
        /// <returns>The new node, or null if the shim is missing from the project.</returns>
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

        /// <summary>Suffix of the per-component toggle. Inside the component's own group, so it
        /// reads as "this entity has this component" rather than as one of its fields.</summary>
        private const string EnabledSuffix = "/Enabled";

        /// <summary>Property holding a component-level host reference — the whole component is
        /// authored by pointing at one object (a sprite, say) rather than filled in as a form.</summary>
        private const string SourceSuffix = "/Source";

        /// <summary>The picker that ADDS a component. Editor-only: it is a verb, not state, and
        /// storing it in the .tscn would persist a menu selection as though it were data.</summary>
        private const string AddProperty = "Add Component";

        /// <summary>Resting value of the add picker.</summary>
        private const string AddNone = "(add…)";

        private readonly List<ComponentSchema> _components = new();
        private readonly Dictionary<string, ComponentSchema> _byId = new(StringComparer.Ordinal);
        private readonly HashSet<string> _enabled = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Variant> _values = new(StringComparer.Ordinal);
        private bool _loaded;
        private MeshInstance3D? _wire;

        /// <summary>One component, flattened for the inspector.</summary>
        private sealed class ComponentSchema
        {
            public string Id = "";
            public string DisplayName = "";
            /// <summary>Host-object kind the WHOLE component is authored by, or null.</summary>
            public string? AuthoredBy;
            public AuthoredGizmoSchema? Gizmo;
            /// <summary>Leaf rows, by slash-separated path within the component.</summary>
            public readonly List<SchemaField> Fields = new();
            /// <summary>Paths that are host references rather than typed values.</summary>
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
            /// <summary>Whether the record DECLARED a default. An empty string with no declared
            /// default means absent, and exports as null; one with a declared default means that
            /// default, and exports as the empty string.</summary>
            bool HasDefault,
            /// <summary>File extensions this field accepts, if it is a path. Gives it a file
            /// picker without making it a BAKED asset reference.</summary>
            IReadOnlyList<string>? AssetKinds);

        /// <summary>A field authored by pointing at one of Godot's own objects.</summary>
        private readonly record struct HostRef(
            string Path,
            string Kind,
            bool IsList,
            IReadOnlyList<string>? AssetKinds,
            /// <summary>Leaf field names the referencing record declares BENEATH this reference —
            /// what the bake is allowed to fill in. Different records want different slices of the
            /// same object: the engine's collider wants Size and LocalCenter, a game's box part
            /// wants SizeX/SizeY/SizeZ. Baking a fixed record would serve one and corrupt the
            /// other.</summary>
            IReadOnlyList<string> Fields);

        // ---------------------------------------------------------------------------------
        // Schema
        // ---------------------------------------------------------------------------------

        private void EnsureSchema()
        {
            if (_loaded)
            {
                return;
            }
            // Latched before any work, so a failure reports once instead of on every inspector
            // redraw. Everything after it is guarded: a throw that escaped would leave the node
            // permanently componentless with no error, which gives an author nothing to go on.
            _loaded = true;

            try
            {
                LoadSchema();
            }
            catch (Exception e)
            {
                GD.PushError($"[Paradise.Export] The authoring schema could not be loaded: {e}");
            }
        }

        /// <summary>Engine components first, then the game's, so a game cannot redefine an engine id.</summary>
        private void LoadSchema()
        {
            var documents = new List<AuthoringSchemaDocument>();
            try
            {
                documents.Add(AuthoringSchemaReader.Read(Paradise.Export.AuthoringSchema.Json));
            }
            catch (Exception e)
            {
                GD.PushError($"[Paradise.Export] The engine's built-in authoring schema is unreadable: {e.Message}");
            }

            string gamePath = ParadisePaths.DataDirPrefix + SchemaFileName;
            string text = global::Godot.FileAccess.GetFileAsString(gamePath);
            if (!string.IsNullOrEmpty(text))
            {
                try
                {
                    documents.Add(AuthoringSchemaReader.Read(text));
                }
                catch (Exception e)
                {
                    // Named loudly: the symptom of a silently skipped schema is "my component is
                    // missing", which gives an author nothing to go on.
                    GD.PushError($"[Paradise.Export] '{gamePath}' is not a readable authoring schema: {e.Message}");
                }
            }

            foreach (AuthoredComponentSchema source in AuthoringSchemaReader.Merge(documents).Components)
            {
                var component = new ComponentSchema
                {
                    Id = source.Id,
                    DisplayName = string.IsNullOrEmpty(source.DisplayName) ? source.Id : source.DisplayName,
                    AuthoredBy = source.AuthoredBy,
                    Gizmo = source.Gizmo is { Kind: "box" } gizmo ? gizmo : null,
                };
                ReadFields(source.Fields, "", component);
                _components.Add(component);
                _byId[component.Id] = component;
            }
        }

        /// <summary>
        /// Flatten the schema's field tree into inspector PATHS ("Box/SizeX").
        ///
        /// Composition is a tree in the data and a path in the editor: Godot renders a
        /// slash-separated name as a nested group for free, and the path is also what the export
        /// uses to rebuild the nesting, so the two cannot disagree about shape.
        /// </summary>
        private static void ReadFields(List<AuthoredFieldSchema> fields, string prefix, ComponentSchema into)
        {
            foreach (AuthoredFieldSchema field in fields)
            {
                string path = prefix + field.Name;

                if (field.Type == AuthoredFieldTypes.Array)
                {
                    // Only a list of host references is supported — the shape the engine's collider
                    // list has. A list of typed rows has no author asking for it yet, and guessing
                    // at a control nobody wants is how a schema grows things no editor implements.
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
                    // Its nested fields are what the reference BAKES into; they are never typed in.
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
                    field.Default is not null,
                    field.AssetKinds));
            }
        }

        /// <summary>The immediate leaf names of a field list — what a bake may fill in.</summary>
        private static IReadOnlyList<string> LeafNames(List<AuthoredFieldSchema>? fields) =>
            fields is null ? Array.Empty<string>() : fields.Select(f => f.Name).ToList();

        /// <summary>
        /// A field's default, read AT ITS SCHEMA TYPE.
        ///
        /// Reading everything as a double is the obvious shortcut and a latent crash: on a JSON
        /// <c>true</c> it throws InvalidOperationException, which is NOT a JsonException, so it
        /// escapes the guard around schema loading and takes the node down inside the editor.
        /// </summary>
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

        /// <summary>An enum with no declared default still has to start on a legal member, or the
        /// dropdown opens on a value the runtime cannot parse.</summary>
        private static string EnumFallback(AuthoredFieldSchema field) =>
            field.Values is { Count: > 0 } values ? values[0] : "";

        /// <summary>Enums are drawn as a String with an Enum hint: the value STORED is the member
        /// name, exactly what the contract serializes, so no mapping table is needed either side.
        /// Vectors and colours have real Variant types; everything else falls back to a string.</summary>
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

        // ---------------------------------------------------------------------------------
        // Inspector
        // ---------------------------------------------------------------------------------

        public global::Godot.Collections.Array<global::Godot.Collections.Dictionary> BuildPropertyList()
        {
            EnsureSchema();
            var list = new global::Godot.Collections.Array<global::Godot.Collections.Dictionary>();

            // ADD, rather than a toggle for every component that exists. Listing them all put a
            // dozen checkboxes on a node that carries two — and the list grows with every component
            // the engine or the game ever declares, so the wall only gets worse. What an entity
            // HAS should be what the inspector shows; the rest belongs behind a menu.
            //
            // Ids rather than display names: two components may share a name, and the id is what
            // the author is choosing. The group header below shows the friendly name.
            string[] addable =
            [
                AddNone,
                .. _components.Where(c => !_enabled.Contains(c.Id)).Select(c => c.Id),
            ];
            list.Add(new global::Godot.Collections.Dictionary
            {
                { "name", AddProperty },
                { "type", (int)Variant.Type.String },
                // Editor, NOT Default: Default includes Storage, which would write the menu's
                // resting value into the scene as if it were authored data.
                { "usage", (int)PropertyUsageFlags.Editor },
                { "hint", (int)PropertyHint.Enum },
                { "hint_string", string.Join(",", addable) },
            });

            foreach (ComponentSchema component in _components)
            {
                if (!_enabled.Contains(component.Id))
                {
                    continue;
                }

                // A group per component the entity actually carries.
                list.Add(new global::Godot.Collections.Dictionary
                {
                    { "name", component.DisplayName },
                    { "type", (int)Variant.Type.Nil },
                    { "usage", (int)PropertyUsageFlags.Group },
                    { "hint_string", component.Id + "/" },
                });

                // Kept as the way to REMOVE one: unticking is how the component goes away, and it
                // is also what the .tscn stores, so scenes authored before this change still load.
                list.Add(new global::Godot.Collections.Dictionary
                {
                    { "name", component.Id + EnabledSuffix },
                    { "type", (int)Variant.Type.Bool },
                    { "usage", (int)PropertyUsageFlags.Default },
                });

                if (component.AuthoredBy is { } componentKind)
                {
                    list.Add(HostPicker(component.Id + SourceSuffix, componentKind, isList: false, null));
                }


                foreach (HostRef host in component.Hosts)
                {
                    list.Add(HostPicker(
                        component.Id + "/" + host.Path, host.Kind, host.IsList, host.AssetKinds));
                }

                foreach (SchemaField field in component.Fields)
                {
                    if (!IsVisible(component, field))
                    {
                        continue;
                    }

                    var entry = new global::Godot.Collections.Dictionary
                    {
                        { "name", component.Id + "/" + field.Path },
                        { "type", (int)field.Type },
                        { "usage", (int)PropertyUsageFlags.Default },
                    };

                    if (field.AssetKinds is { Count: > 0 } fileKinds)
                    {
                        // A path the author picks but that travels VERBATIM — unlike an asset
                        // REFERENCE, which is baked. Same control, different contract.
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
                        // Advisory only: the runtime still decides what is playable, because only it
                        // can cross-check a value against the rest of the configuration.
                        entry["hint"] = (int)PropertyHint.Range;
                        entry["hint_string"] = $"{min},{max}";
                    }
                    list.Add(entry);
                }
            }

            return list;
        }

        /// <summary>A picker for one of Godot's own objects, filtered to what the kind means here.</summary>
        private static global::Godot.Collections.Dictionary HostPicker(
            string name, string kind, bool isList, IReadOnlyList<string>? assetKinds)
        {
            if (kind == AuthoredBySources.Asset)
            {
                // Godot's filter syntax is built HERE, from the semantic kinds the schema declares.
                // Putting "*.glb,*.gltf" in the document would make Blender speak Godot.
                string filter = assetKinds is { Count: > 0 }
                    ? string.Join(",", assetKinds.Select(k => "*" + k))
                    : "*";
                return new global::Godot.Collections.Dictionary
                {
                    { "name", name },
                    { "type", (int)Variant.Type.String },
                    { "usage", (int)PropertyUsageFlags.Default },
                    { "hint", (int)PropertyHint.File },
                    { "hint_string", filter },
                };
            }

            string nodeType = kind switch
            {
                AuthoredBySources.Shape => "CollisionShape3D",
                AuthoredBySources.Sprite => "Sprite3D",
                AuthoredBySources.Light => "Light3D",
                _ => "Node3D",
            };

            if (isList)
            {
                // A typed array of node paths — the same control EntityExport.PhysicsColliders had,
                // which is what an author was already used to.
                return new global::Godot.Collections.Dictionary
                {
                    { "name", name },
                    { "type", (int)Variant.Type.Array },
                    { "usage", (int)PropertyUsageFlags.Default },
                    { "hint", (int)PropertyHint.TypeString },
                    {
                        "hint_string",
                        $"{(int)Variant.Type.NodePath}/{(int)PropertyHint.NodePathValidTypes}:{nodeType}"
                    },
                };
            }

            return new global::Godot.Collections.Dictionary
            {
                { "name", name },
                { "type", (int)Variant.Type.NodePath },
                { "usage", (int)PropertyUsageFlags.Default },
                { "hint", (int)PropertyHint.NodePathValidTypes },
                { "hint_string", nodeType },
            };
        }

        /// <summary>Evaluate a field's visibility guard. This is what EntityExport did in
        /// _ValidateProperty, except the rule now travels in the schema so Blender and a browser
        /// form get it too instead of reimplementing it.</summary>
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

            // The add picker always reads as its resting value: it is a verb that fires on set,
            // never a selection that persists.
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
                if (chosen != AddNone && _byId.TryGetValue(chosen, out ComponentSchema? added) &&
                    _enabled.Add(chosen))
                {
                    SeedDefaults(added);
                    OnAuthoredChanged();
                }
                // Always redraw: the picker has to fall back to its resting value and drop the id
                // it just added from its own list.
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
                    if (_enabled.Add(id))
                    {
                        SeedDefaults(component);
                    }
                }
                else if (_enabled.Remove(id))
                {
                    // Forget the component's values with it. Keeping them would resurrect numbers
                    // an author removed the moment the box was ticked again.
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
            // A guard field changing reveals or hides its dependants.
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

        private void SeedDefaults(ComponentSchema component)
        {
            foreach (SchemaField field in component.Fields)
            {
                _values[component.Id + "/" + field.Path] = field.Default;
            }
        }

        // ---------------------------------------------------------------------------------
        // Identity — the one thing a schema cannot carry
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The source asset, as <c>paradise.identity</c> authors it.
        ///
        /// A convenience over the authored value, for code that has no inspector to go through:
        /// the model-prefab generator places entities programmatically, and the prefab exporter
        /// reads provenance while building a template. Setting it enables the identity component,
        /// exactly as ticking the box would.
        /// </summary>
        public string ModelPath
        {
            get => AuthoredValue(ParadiseComponentIds.Identity, "Prefab").AsString();

            // Sets BOTH halves, because a model is two authored facts and code that has only a
            // path cannot be expected to know that. paradise.identity/Prefab is provenance — which
            // asset this entity came from — and paradise.renderable/Mesh is what draws. Setting
            // only the first produced a prefab that renders in the Godot editor, where the child
            // GLB is native, and is INVISIBLE at runtime, which no export diff catches.
            set
            {
                SetAuthored(ParadiseComponentIds.Identity, "Prefab", value);
                SetAuthored(ParadiseComponentIds.Renderable, "Mesh", value);
            }
        }

        /// <summary>The authored kind, falling back to "Prop" — the contract treats it as a label
        /// and an unlabelled entity is a prop.</summary>
        public string ResolvedKind
        {
            get
            {
                string kind = AuthoredValue(ParadiseComponentIds.Identity, "Kind").AsString();
                return string.IsNullOrWhiteSpace(kind) ? "Prop" : kind;
            }
        }

        private Variant AuthoredValue(string componentId, string field)
        {
            EnsureSchema();
            return _values.TryGetValue(componentId + "/" + field, out Variant value) ? value : default;
        }

        private void SetAuthored(string componentId, string field, Variant value)
        {
            EnsureSchema();
            if (!_byId.TryGetValue(componentId, out ComponentSchema? component))
            {
                return;
            }
            if (_enabled.Add(componentId))
            {
                SeedDefaults(component);
            }
            _values[componentId + "/" + field] = value;
        }

        /// <summary>Stable per-placement identity; <see cref="Guid.Empty"/> until minted.</summary>
        public Guid EntityGuid =>
            _host.HasMeta(GuidMetaKey) && Guid.TryParse(_host.GetMeta(GuidMetaKey).AsString(), out Guid g) ? g : Guid.Empty;

        /// <summary>Force a specific GUID (used by rebuild pipelines to carry identity across a
        /// destroy/recreate). Rejects <see cref="Guid.Empty"/>.</summary>
        public bool RestoreEntityGuid(Guid value)
        {
            if (value == Guid.Empty)
            {
                return false;
            }
            _host.SetMeta(GuidMetaKey, value.ToString("N"));
            return true;
        }

        /// <summary>Ensure a GUID exists — minting and persisting one if the node has none — and
        /// return it. The exporter calls this so a freshly-placed, never-saved entity still exports
        /// a stable identity instead of the all-zero GUID, which would collide across entities.</summary>
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

        // Ensure a GUID exists and is unique among entity nodes in the edited scene; if a collision
        // is found (e.g. a duplicated node), regenerate this node's.
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

        // ---------------------------------------------------------------------------------
        // Export
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Every enabled component, as the payloads the scene export carries. Field names come from
        /// the schema, which took them from the record, so they match what the runtime deserializes
        /// by construction — and each value is written AT ITS SCHEMA TYPE. Serializing everything as
        /// a number is the obvious shortcut and a silent break: a bool arriving as <c>0</c> makes
        /// the whole component unreadable, since STJ will not widen it back.
        /// </summary>
        public IEnumerable<AuthoredComponentData> ExportAuthoredComponents()
        {
            EnsureSchema();
            ExportPaths paths = ParadisePaths.ExportPaths();

            foreach (ComponentSchema component in _components)
            {
                if (!_enabled.Contains(component.Id))
                {
                    continue;
                }

                var payload = new JsonObject();
                foreach (SchemaField field in component.Fields)
                {
                    Write(payload, field.Path, ValueOf(component, field));
                }

                BakeHosts(component, payload, paths);

                yield return new AuthoredComponentData
                {
                    Id = component.Id,
                    // Round-tripped through a document so the payload is a detached JsonElement:
                    // reading one after its owning JsonDocument is disposed throws.
                    Data = JsonDocument.Parse(payload.ToJsonString()).RootElement.Clone(),
                };
            }
        }

        /// <summary>One authored value as JSON, at its schema type.</summary>
        private JsonNode? ValueOf(ComponentSchema component, SchemaField field)
        {
            Variant value = _values.TryGetValue(component.Id + "/" + field.Path, out Variant stored)
                ? stored
                : field.Default;

            return field.Type switch
            {
                Variant.Type.Bool => JsonValue.Create(value.AsBool()),
                Variant.Type.Int => JsonValue.Create(value.AsInt64()),
                // An empty string with no declared default is ABSENT, not empty: the record that
                // produced this field had no initializer, so its own default is null and the
                // contract writes null. A field that declared "" keeps writing "".
                Variant.Type.String => value.AsString() is { Length: 0 } && !field.HasDefault
                    ? null
                    : JsonValue.Create(value.AsString()),
                Variant.Type.Vector2 => Floats(value.AsVector2().X, value.AsVector2().Y),
                Variant.Type.Vector3 => Floats(value.AsVector3().X, value.AsVector3().Y, value.AsVector3().Z),
                Variant.Type.Quaternion => Floats(
                    value.AsQuaternion().X, value.AsQuaternion().Y,
                    value.AsQuaternion().Z, value.AsQuaternion().W),
                Variant.Type.Color => Rgba(value.AsColor()),
                _ => JsonValue.Create(value.AsDouble()),
            };
        }

        private static JsonArray Floats(params float[] values) =>
            new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

        // The contract writes Color32 as { r, g, b, a } floats — see Color32Converter.
        private static JsonObject Rgba(Color c) => new()
        {
            ["r"] = JsonValue.Create(c.R),
            ["g"] = JsonValue.Create(c.G),
            ["b"] = JsonValue.Create(c.B),
            ["a"] = JsonValue.Create(c.A),
        };

        /// <summary>Write a slash-separated path into a nested object, creating groups as needed —
        /// the inverse of the flattening that produced the path.</summary>
        private static void Write(JsonObject root, string path, JsonNode? value)
        {
            JsonObject target = root;
            string[] parts = path.Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (target[parts[i]] is not JsonObject next)
                {
                    next = new JsonObject();
                    target[parts[i]] = next;
                }
                target = next;
            }
            target[parts[^1]] = value;
        }

        /// <summary>
        /// Resolve every reference this component was authored with and bake it into values.
        ///
        /// The asymmetry the whole approach rests on: authored as a REFERENCE, exported as a VALUE.
        /// </summary>
        private void BakeHosts(ComponentSchema component, JsonObject payload, ExportPaths paths)
        {
            if (component.AuthoredBy is { } componentKind)
            {
                var whole = new HostRef("", componentKind, false, null, Array.Empty<string>());
                BakeInto(payload, whole, component.Id + SourceSuffix, paths);
            }

            foreach (HostRef host in component.Hosts)
            {
                BakeInto(payload, host, component.Id + "/" + host.Path, paths);
            }
        }

        private void BakeInto(JsonObject payload, HostRef host, string valueKey, ExportPaths paths)
        {
            string path = host.Path;
            string kind = host.Kind;
            if (!_values.TryGetValue(valueKey, out Variant stored))
            {
                return;
            }

            if (host.IsList)
            {
                var array = new JsonArray();
                foreach (Variant element in stored.AsGodotArray())
                {
                    if (BakeOne(kind, element.AsNodePath(), paths) is { } baked)
                    {
                        array.Add(Select(baked, host.Fields));
                    }
                }
                Write(payload, path, array);
                return;
            }

            if (kind == AuthoredBySources.Asset)
            {
                string file = stored.AsString();
                if (string.IsNullOrEmpty(file))
                {
                    return;
                }
                // Which resolver applies is decided by what the field says it ACCEPTS: a model goes
                // through the mesh resolver, an image through the spritesheet one (which also
                // rewrites the extension to the runtime's .ktx2 sidecar).
                string? baked = HostObjectBaker.IsGlbPath(file)
                    ? HostObjectBaker.MeshField(_host, file, paths)
                    : HostObjectBaker.SheetField(_host, file, paths);
                Write(payload, path, baked is null ? null : JsonValue.Create(baked));
                return;
            }

            if (BakeOne(kind, stored.AsNodePath(), paths) is not { } value)
            {
                return;
            }
            if (value is JsonObject candidates && host.Fields.Count > 0)
            {
                WarnOnShapeMismatch(host, candidates);
                value = Select(candidates, host.Fields);
            }

            if (path.Length == 0 && value is JsonObject fields)
            {
                // A component-level reference contributes its baked fields ALONGSIDE the authored
                // ones, rather than replacing the payload: sprite animation reads its sheet and
                // quad off the sprite while fps and loop stay typed in.
                foreach (var pair in fields.ToList())
                {
                    payload[pair.Key] = pair.Value?.DeepClone();
                }
                return;
            }
            Write(payload, path, value);
        }

        /// <summary>
        /// A record asking for box EXTENTS, pointed at something that is not a box.
        ///
        /// The engine's collider is unaffected — it takes Size and LocalCenter, which every shape
        /// fills — but a game part that declared SizeX/SizeY/SizeZ gets zeroes from a sphere or
        /// capsule, since only a box sets Size. The old bake rejected non-box shapes outright with
        /// a warning; keeping the warning is what stops an authoring mistake becoming a
        /// zero-sized collider nobody notices.
        /// </summary>
        private void WarnOnShapeMismatch(HostRef host, JsonObject candidates)
        {
            if (host.Kind != AuthoredBySources.Shape ||
                !host.Fields.Contains("SizeX") ||
                candidates["ShapeType"]?.GetValue<string>() is not { } shape ||
                shape == "Box")
            {
                return;
            }

            GD.PushWarning(
                $"[Paradise.Export] '{_host.Name}': '{host.Path}' asks for box extents but points at a "
                + $"{shape} shape, which leaves them zero. Point it at a BoxShape3D, or give the "
                + "record the fields that shape fills (Size, Radius, Height).");
        }

        /// <summary>
        /// Keep only the fields the referencing record actually declares.
        ///
        /// A bake offers every name it knows how to produce; the record decides which it wants. The
        /// engine's collider takes Size and LocalCenter, a game's box part takes SizeX/SizeY/SizeZ,
        /// and both are baked from the same CollisionShape3D. Writing the full set instead would
        /// put keys into the payload that the record has no property for.
        /// </summary>
        private static JsonObject Select(JsonNode baked, IReadOnlyList<string> wanted)
        {
            var result = new JsonObject();
            if (baked is not JsonObject source)
            {
                return result;
            }
            foreach (string name in wanted)
            {
                if (source[name] is { } value)
                {
                    result[name] = value.DeepClone();
                }
            }
            return result;
        }

        /// <summary>Bake one referenced object into the JSON its field expects.</summary>
        private JsonNode? BakeOne(string kind, NodePath path, ExportPaths paths)
        {
            if (path.IsEmpty)
            {
                return null;
            }

            switch (kind)
            {
                case AuthoredBySources.Shape:
                {
                    if (_host.GetNodeOrNull<CollisionShape3D>(path) is not { Shape: not null } shape)
                    {
                        return null;
                    }
                    var data = new ColliderShapeData();
                    if (!HostObjectBaker.TryBakeShape(_host, shape, data))
                    {
                        return null;
                    }

                    // Everything a shape can offer, in BOTH vocabularies the contract and games
                    // use. Select() then keeps whichever the referencing record declared.
                    JsonNode? full = Serialize(data);
                    if (full is not JsonObject candidates)
                    {
                        return null;
                    }
                    candidates["SizeX"] = JsonValue.Create(data.Size.X);
                    candidates["SizeY"] = JsonValue.Create(data.Size.Y);
                    candidates["SizeZ"] = JsonValue.Create(data.Size.Z);
                    candidates["CenterX"] = JsonValue.Create(data.LocalCenter.X);
                    candidates["CenterY"] = JsonValue.Create(data.LocalCenter.Y);
                    candidates["CenterZ"] = JsonValue.Create(data.LocalCenter.Z);
                    return candidates;
                }

                case AuthoredBySources.Mesh:
                {
                    if (_host.GetNodeOrNull<Node>(path) is not { } node)
                    {
                        return null;
                    }
                    string? source = HostObjectBaker.SourceGlbOf(node)
                        ?? HostObjectBaker.ModelDescendants(node)
                            .Select(HostObjectBaker.SourceGlbOf)
                            .FirstOrDefault(p => p is not null);
                    string? field = HostObjectBaker.MeshField(_host, source, paths);
                    return field is null ? null : JsonValue.Create(field);
                }

                case AuthoredBySources.Light:
                {
                    if (_host.GetNodeOrNull<Light3D>(path) is not { } lightNode)
                    {
                        return null;
                    }
                    // Baked through the same reader the scene-level walk uses, so a light cannot
                    // describe itself one way when it is owned and another when it is not.
                    return Serialize(HostObjectBaker.BakeLight(lightNode));
                }

                case AuthoredBySources.Sprite:
                {
                    if (_host.GetNodeOrNull<Sprite3D>(path) is not { } sprite)
                    {
                        return null;
                    }
                    var data = new SpriteAnimationComponentData();
                    HostObjectBaker.BakeSprite(_host, sprite, paths, data);
                    // Only the fields the sprite OWNS; the rest of the component stays authored.
                    return new JsonObject
                    {
                        ["Sheet"] = data.Sheet is null ? null : JsonValue.Create(data.Sheet),
                        ["Columns"] = JsonValue.Create(data.Columns),
                        ["Rows"] = JsonValue.Create(data.Rows),
                        ["QuadSize"] = Floats(data.QuadSize.X, data.QuadSize.Y),
                        ["Billboard"] = JsonValue.Create(data.Billboard),
                    };
                }

                default:
                    return null;
            }
        }

        /// <summary>Serialize a contract record through the contract's own writer, so converters
        /// (enums by name, vectors as float arrays) apply exactly as they do on the way out.</summary>
        private static JsonNode? Serialize<T>(T value) =>
            JsonNode.Parse(Paradise.Export.Serialization.ExportJsonWriter.SerializeToString(value!));

        // ---------------------------------------------------------------------------------
        // Gizmo
        // ---------------------------------------------------------------------------------

        /// <summary>Redraw whatever an enabled component declared. A scene of props draws nothing,
        /// which is the common case and costs nothing.</summary>
        private void OnAuthoredChanged()
        {
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
            // INTERNAL: GetChildren() skips internal children, and the exporter walks children for
            // both entities and MATERIAL SLOTS. As a plain child, this wireframe's material was
            // exported as the entity's own and written into data/materials/.
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
    }
}
#endif

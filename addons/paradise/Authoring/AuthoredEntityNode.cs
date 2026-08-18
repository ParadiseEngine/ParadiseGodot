// Editor-only, like everything it builds on: EntityExport is itself #if TOOLS, and this node is an
// authoring surface that never runs in a game.
#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Paradise.Authoring;
using Paradise.Export.Data;

namespace ParadiseGodot.Authoring
{
    /// <summary>
    /// THE authoring node. One class for every authored component there will ever be: pick a
    /// component in the inspector and that component's fields appear, described by the authoring
    /// schema rather than by code here.
    ///
    /// Nothing in this class is specific to any component. The schema names the components, their
    /// fields, each field's editor-neutral type, its semantic unit, its advisory range and its
    /// default; this class turns that into inspector controls and hands the values back as JSON.
    /// Adding an authored component to a game is therefore an <c>[Authored]</c> record and a
    /// re-dump of the schema — no new node, no new class, no generated code.
    ///
    /// It is hand-written on purpose, and that must not be "improved" into a generator. Godot's own
    /// ScriptPropertiesGenerator builds both the property list and the method dispatch for a C#
    /// script, and two Roslyn generators cannot observe each other's output — so a generated
    /// <c>[Export]</c> never reaches the inspector and a generated <c>_Set</c> is never called,
    /// silently dropping whatever the scene had stored, with no error anywhere. Interpreting data at
    /// runtime sidesteps that completely, and one interpreter is less code than a generator anyway.
    ///
    /// No subclasses either: editor visuals are declared, not coded. A component asks for a
    /// wireframe with <c>[AuthorBoxGizmo]</c> and this class draws it, or points at one of the
    /// host's own objects with <c>[AuthorNativeShape]</c> and the host draws it.
    /// </summary>
    [Tool]
    [GlobalClass]
    public partial class AuthoredEntityNode : EntityExport, IAuthoredEntity
    {
        /// <summary>The game's schema, relative to the configured data directory. Optional: a
        /// project that authors only ENGINE components needs no file at all.</summary>
        private const string SchemaFileName = "authoring-schema.json";

        /// <summary>The inspector property that chooses which component this node authors.</summary>
        private const string ComponentProperty = "ComponentId";

        /// <summary>The dropdown entry for "this entity authors no component". THE DEFAULT, because
        /// most entities are props: they carry a mesh and a transform and nothing else. Defaulting
        /// to the first component in the schema would make every newly added node silently export
        /// data nobody asked for.</summary>
        private const string NoComponent = "(none)";

        private readonly Dictionary<string, List<SchemaField>> _fields = new();
        private readonly Dictionary<string, AuthoredGizmoSchema> _gizmos = new();
        private readonly Dictionary<string, List<string>> _shapeRefs = new();
        private readonly List<string> _componentIds = new();
        private readonly Dictionary<string, Variant> _values = new();
        private string _componentId = "";
        private bool _loaded;
        private MeshInstance3D? _wire;

        /// <summary>One inspector row: a slash-separated PATH into the authored tree, the Variant
        /// type to draw it at, and the schema's advisory bounds, enum names and default.</summary>
        private readonly record struct SchemaField(
            string Path,
            Variant.Type Type,
            double? Minimum,
            double? Maximum,
            string? Doc,
            IReadOnlyList<string>? EnumValues,
            Variant Default);

        // -----------------------------------------------------------------------------------
        // Schema
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Load the schema, lazily — Godot asks for the property list before <c>_Ready</c>.
        ///
        /// Two sources, merged: the ENGINE's own components, compiled into Paradise.Export, and the
        /// game's, dumped to a file next to its exported data. The engine goes first so a game that
        /// reuses an engine component id cannot redefine what the exporter bakes.
        /// </summary>
        private void EnsureSchema()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;

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
                    // Named loudly. The symptom of a silently skipped schema is "my component is
                    // missing from the dropdown", which gives an author nothing to go on.
                    GD.PushError($"[Paradise.Export] '{gamePath}' is not a readable authoring schema: {e.Message}");
                }
            }

            foreach (AuthoredComponentSchema component in AuthoringSchemaReader.Merge(documents).Components)
            {
                var fields = new List<SchemaField>();
                var shapes = new List<string>();
                ReadFields(component.Fields, "", fields, shapes);
                _fields[component.Id] = fields;
                _shapeRefs[component.Id] = shapes;
                if (component.Gizmo is { Kind: "box" } gizmo)
                {
                    _gizmos[component.Id] = gizmo;
                }
                _componentIds.Add(component.Id);
            }
        }

        /// <summary>
        /// Flatten the schema's field tree into inspector PATHS ("Box/SizeX").
        ///
        /// Composition is a tree in the data and a path in the editor: Godot renders a
        /// slash-separated name as a nested group for free, so a composed part shows up folded
        /// under its own heading without this class knowing what the part is. The path is also what
        /// the export uses to rebuild the nesting, so the two cannot disagree about shape.
        /// </summary>
        private static void ReadFields(
            List<AuthoredFieldSchema> fields, string prefix, List<SchemaField> into, List<string> shapes)
        {
            foreach (AuthoredFieldSchema field in fields)
            {
                string path = prefix + field.Name;
                if (field.Fields is { Count: > 0 } nested)
                {
                    if (field.AuthoredBy == AuthoredBySources.NativeShape)
                    {
                        // A reference, not a group of numbers: the inspector shows one picker and
                        // the shape is edited with the host's own handles.
                        shapes.Add(path);
                    }
                    ReadFields(nested, path + "/", into, shapes);
                    continue;
                }

                Variant.Type type = VariantTypeOf(field.Type);
                into.Add(new SchemaField(
                    path,
                    type,
                    field.Minimum,
                    field.Maximum,
                    field.Doc,
                    field.Values,
                    DefaultOf(type, field)));
            }
        }

        /// <summary>
        /// A field's default, read AT ITS SCHEMA TYPE.
        ///
        /// Reading everything as a double is the obvious shortcut and a latent crash: on a JSON
        /// <c>true</c> it throws InvalidOperationException, which is NOT a JsonException, so it
        /// escapes the guard around schema loading and takes the node down inside the editor. It
        /// also mislabels an int default as a float, so the inspector advertises one type and hands
        /// back another.
        /// </summary>
        private static Variant DefaultOf(Variant.Type type, AuthoredFieldSchema field)
        {
            if (field.Default is not { } value)
            {
                // No initializer on the record means no default. Inventing one would write a number
                // into the scene as though a human had chosen it.
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
                Variant.Type.Bool => Variant.From(value.ValueKind == System.Text.Json.JsonValueKind.True),
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
        /// name, which is exactly what the export contract serializes, so no mapping table is
        /// needed on either side.</summary>
        private static Variant.Type VariantTypeOf(string schemaType) => schemaType switch
        {
            AuthoredFieldTypes.Float => Variant.Type.Float,
            AuthoredFieldTypes.Int => Variant.Type.Int,
            AuthoredFieldTypes.Bool => Variant.Type.Bool,
            _ => Variant.Type.String,
        };

        // -----------------------------------------------------------------------------------
        // Inspector
        // -----------------------------------------------------------------------------------

        /// <summary>Switch component: forget the previous component's values and seed this one's
        /// defaults. Values are NOT carried across — two components sharing a field name mean
        /// nothing to each other, and silently keeping a number would be worse than losing it.</summary>
        private void SelectComponent(string id)
        {
            if (id == _componentId)
            {
                // Re-selecting the same component must NOT reseed. A scene that stores ComponentId
                // alongside its values may apply them in either order, and wiping on a no-op switch
                // would silently reset everything the author typed.
                return;
            }
            _componentId = id;
            _values.Clear();
            if (_fields.TryGetValue(id, out List<SchemaField>? fields))
            {
                foreach (SchemaField field in fields)
                {
                    _values[field.Path] = field.Default;
                }
            }
        }

        public override global::Godot.Collections.Array<global::Godot.Collections.Dictionary> _GetPropertyList()
        {
            EnsureSchema();
            var list = new global::Godot.Collections.Array<global::Godot.Collections.Dictionary>
            {
                // The picker itself, as a dropdown of everything the merged schema declares.
                new()
                {
                    { "name", ComponentProperty },
                    { "type", (int)Variant.Type.String },
                    { "usage", (int)PropertyUsageFlags.Default },
                    { "hint", (int)PropertyHint.Enum },
                    { "hint_string", string.Join(",", new[] { NoComponent }.Concat(_componentIds)) },
                },
            };

            if (!_fields.TryGetValue(_componentId, out List<SchemaField>? fields))
            {
                return list;
            }

            List<string> shapes = _shapeRefs.TryGetValue(_componentId, out List<string>? refs)
                ? refs
                : new List<string>();
            foreach (string shape in shapes)
            {
                // A real node slot, filtered to CollisionShape3D — so the shape is picked and then
                // edited with Godot's own handles, rather than retyped as numbers here.
                list.Add(new global::Godot.Collections.Dictionary
                {
                    { "name", shape },
                    { "type", (int)Variant.Type.NodePath },
                    { "usage", (int)PropertyUsageFlags.Default },
                    { "hint", (int)PropertyHint.NodePathValidTypes },
                    { "hint_string", "CollisionShape3D" },
                });
            }

            foreach (SchemaField field in fields)
            {
                // The fields a reference bakes into are not authored by hand.
                if (shapes.Any(shape => field.Path.StartsWith(shape + "/", StringComparison.Ordinal)))
                {
                    continue;
                }

                var entry = new global::Godot.Collections.Dictionary
                {
                    { "name", field.Path },
                    { "type", (int)field.Type },
                    { "usage", (int)PropertyUsageFlags.Default },
                };

                if (field.EnumValues is { Count: > 0 } values)
                {
                    entry["hint"] = (int)PropertyHint.Enum;
                    entry["hint_string"] = string.Join(",", values);
                }
                else if (field.Minimum is { } min && field.Maximum is { } max)
                {
                    // Advisory only. The game still validates on load, because only it can
                    // cross-check a value against the rest of its configuration.
                    entry["hint"] = (int)PropertyHint.Range;
                    entry["hint_string"] = $"{min},{max}";
                }
                list.Add(entry);
            }
            return list;
        }

        public override Variant _Get(StringName property)
        {
            EnsureSchema();
            string name = property.ToString();
            if (name == ComponentProperty)
            {
                return _componentId.Length == 0 ? NoComponent : _componentId;
            }
            return _values.TryGetValue(name, out Variant value) ? value : default;
        }

        /// <summary>True when this property is a shape REFERENCE rather than an authored value.</summary>
        private bool IsShapeRef(string name) =>
            _shapeRefs.TryGetValue(_componentId, out List<string>? refs) && refs.Contains(name);

        public override bool _Set(StringName property, Variant value)
        {
            EnsureSchema();
            string name = property.ToString();

            if (name == ComponentProperty)
            {
                string id = value.AsString();
                if (id == NoComponent)
                {
                    id = "";
                }
                else if (!_fields.ContainsKey(id))
                {
                    return false;
                }
                SelectComponent(id);
                // The inspector is still showing the OLD component's fields; without this it keeps
                // showing them until the node is reselected.
                NotifyPropertyListChanged();
                OnAuthoredChanged();
                return true;
            }

            if (IsShapeRef(name))
            {
                _values[name] = value;
                OnAuthoredChanged();
                return true;
            }

            if (!_values.ContainsKey(name))
            {
                return false;
            }
            _values[name] = value;
            OnAuthoredChanged();
            return true;
        }

        // -----------------------------------------------------------------------------------
        // Export
        // -----------------------------------------------------------------------------------

        /// <summary>The authored values as the payload the scene export carries. Field names come
        /// from the schema, which took them from the record, so they match what the runtime
        /// deserializes by construction — and each value is written AT ITS SCHEMA TYPE. Serializing
        /// everything as a number is the obvious shortcut and a silent break: a bool arriving as
        /// <c>0</c> makes the whole component unreadable, since STJ will not widen it back.</summary>
        public IEnumerable<AuthoredComponentData> ExportAuthoredComponents()
        {
            EnsureSchema();
            if (!_fields.TryGetValue(_componentId, out List<SchemaField>? fields))
            {
                yield break;
            }

            BakeShapeReferences();

            using var buffer = new System.IO.MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
            {
                WriteGroup(writer, fields, "");
            }

            using var document = System.Text.Json.JsonDocument.Parse(buffer.ToArray());
            yield return new AuthoredComponentData
            {
                Id = _componentId,
                // Cloned: the JsonElement outlives the JsonDocument it was parsed from otherwise,
                // and reading it after disposal throws.
                Data = document.RootElement.Clone(),
            };
        }

        /// <summary>
        /// Resolve each shape reference and bake it into the fields underneath it.
        ///
        /// This is the asymmetry the whole approach rests on: authored as a REFERENCE, exported as
        /// a VALUE. A NodePath means nothing to the runtime, so the geometry is read out of the
        /// picked CollisionShape3D — its extents and where it sits relative to this entity — and
        /// written as plain numbers. Editing stays native; the contract stays portable.
        /// </summary>
        private void BakeShapeReferences()
        {
            if (!_shapeRefs.TryGetValue(_componentId, out List<string>? refs))
            {
                return;
            }

            foreach (string shape in refs)
            {
                if (!_values.TryGetValue(shape, out Variant stored))
                {
                    continue;
                }
                NodePath path = stored.AsNodePath();
                if (path.IsEmpty || GetNodeOrNull<CollisionShape3D>(path) is not { } node)
                {
                    GD.PushWarning(
                        $"[Paradise.Export] '{Name}' authors '{shape}' but no CollisionShape3D is "
                        + "assigned — the component exports zeroes.");
                    continue;
                }
                if (node.Shape is not BoxShape3D box)
                {
                    GD.PushWarning(
                        $"[Paradise.Export] '{Name}': '{shape}' points at a "
                        + $"{node.Shape?.GetType().Name ?? "null"} shape; only BoxShape3D is baked so far.");
                    continue;
                }

                Vector3 centre = node.GlobalPosition - GlobalPosition;
                _values[shape + "/SizeX"] = box.Size.X;
                _values[shape + "/SizeY"] = box.Size.Y;
                _values[shape + "/SizeZ"] = box.Size.Z;
                _values[shape + "/CenterX"] = centre.X;
                _values[shape + "/CenterY"] = centre.Y;
                _values[shape + "/CenterZ"] = centre.Z;
            }
        }

        /// <summary>Write one level of the tree, recursing into each composed part.</summary>
        private void WriteGroup(System.Text.Json.Utf8JsonWriter writer, List<SchemaField> fields, string prefix)
        {
            writer.WriteStartObject();
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (SchemaField field in fields)
            {
                if (!field.Path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }
                string rest = field.Path.Substring(prefix.Length);
                int slash = rest.IndexOf('/');
                if (slash >= 0)
                {
                    string group = rest.Substring(0, slash);
                    if (written.Add(group))
                    {
                        writer.WritePropertyName(group);
                        WriteGroup(writer, fields, prefix + group + "/");
                    }
                    continue;
                }

                Variant value = _values.TryGetValue(field.Path, out Variant stored) ? stored : default;
                switch (field.Type)
                {
                    case Variant.Type.Bool:
                        writer.WriteBoolean(rest, value.AsBool());
                        break;
                    case Variant.Type.Int:
                        writer.WriteNumber(rest, value.AsInt64());
                        break;
                    case Variant.Type.String:
                        writer.WriteString(rest, value.AsString());
                        break;
                    default:
                        writer.WriteNumber(rest, value.AsDouble());
                        break;
                }
            }
            writer.WriteEndObject();
        }

        // -----------------------------------------------------------------------------------
        // Gizmo
        // -----------------------------------------------------------------------------------

        /// <summary>Redraw whatever the component declared. A component with no gizmo draws
        /// nothing, which is the common case and costs nothing.</summary>
        private void OnAuthoredChanged()
        {
            if (_wire is not null)
            {
                _wire.QueueFree();
                _wire = null;
            }
            if (!_gizmos.TryGetValue(_componentId, out AuthoredGizmoSchema? box))
            {
                return;
            }

            float hx = FieldValue(box.HalfExtentX);
            float hz = FieldValue(box.HalfExtentZ);
            float depth = FieldValue(box.Depth);
            if (hx <= 0f || hz <= 0f || depth <= 0f)
            {
                return;
            }

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
            AddChild(_wire, forceReadableName: false, @internal: InternalMode.Front);
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

        /// <summary>The current value of an authored field, or 0 when absent.</summary>
        private float FieldValue(string? name) =>
            name is not null && _values.TryGetValue(name, out Variant value) ? (float)value.AsDouble() : 0f;

        public override void _Ready() => OnAuthoredChanged();
    }
}
#endif

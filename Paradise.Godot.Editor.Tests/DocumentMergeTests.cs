using System.Numerics;
using Paradise.Assets.Documents;
using ParadiseGodot.Documents;

namespace Paradise.Godot.Editor.Tests;

/// <summary>
/// Applying an author's changes over the document as it stands on disk.
/// </summary>
/// <remarks>
/// The load-bearing test is <see cref="an_untouched_scene_round_trips_byte_identically"/>. Every
/// other rule here — key order, the epsilon guard, merging rather than regenerating — exists to
/// make that one true, and without it a save of a scene nobody edited is a diff of the whole file.
/// </remarks>
public class DocumentMergeTests
{
    private static readonly Guid RootGuid = new("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ChildGuid = new("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Buoyancy = new("aa11bb22-cc33-4d44-8e55-ff6677889900");

    private const string BuoyancyId = "aa11bb22-cc33-4d44-8e55-ff6677889900";

    /// <summary>A canonical document — written by the serializer, so the bytes it produces are the
    /// bytes a round trip has to reproduce. A hand-typed one is not canonical and would be
    /// rewritten on first save, which is what "canonical" means rather than a defect.</summary>
    private static PrefabDocument Canonical()
    {
        var document = new PrefabDocument();

        var root = PrefabObject.WithMeta(RootGuid, "Root");
        root.Components.Add(LocalTransformCodec.Write(
            new LocalTransform(new Vector3(1f, 2f, 3f), Quaternion.Identity, Vector3.One)));
        document.Objects.Add(root);

        var child = PrefabObject.WithMeta(ChildGuid, "Child", parent: RootGuid);
        child.Components.Add(LocalTransformCodec.Write(
            new LocalTransform(Vector3.Zero, Quaternion.Identity, new Vector3(2f, 2f, 2f))));
        var payload = new CanonicalTomlTable();
        payload.Add("Segments", 12L);
        payload.Add("Density", 0.75);
        payload.Add("Label", "hull");
        child.Components.Add(new PrefabComponent(Buoyancy, "Demo.Buoyancy", payload));
        document.Objects.Add(child);

        return Reread(document);
    }

    /// <summary>Write and read back, so the fixture is exactly what a file would hand over.</summary>
    private static PrefabDocument Reread(PrefabDocument document) =>
        PrefabDocumentSerializer.Parse(PrefabDocumentSerializer.Write(document), "fixture");

    private static DocumentMerge.ObjectState State(
        Guid guid, string name, Guid? parent, LocalTransform transform,
        AuthoredEdits? edits = null, Dictionary<string, AuthoredValue>? values = null) =>
        new(guid, name, parent, transform, edits ?? new AuthoredEdits(),
            values ?? new Dictionary<string, AuthoredValue>());

    private static IReadOnlyList<DocumentMerge.ObjectState> Untouched() =>
    [
        State(RootGuid, "Root", null,
            new LocalTransform(new Vector3(1f, 2f, 3f), Quaternion.Identity, Vector3.One)),
        State(ChildGuid, "Child", RootGuid,
            new LocalTransform(Vector3.Zero, Quaternion.Identity, new Vector3(2f, 2f, 2f))),
    ];

    /// <summary>THE test. A save that changed nothing must change nothing.</summary>
    [Test]
    public async Task an_untouched_scene_round_trips_byte_identically()
    {
        var document = Canonical();
        var before = PrefabDocumentSerializer.Write(document);

        var merged = DocumentMerge.Apply(document, Untouched());

        await Assert.That(PrefabDocumentSerializer.Write(merged.Document)).IsEqualTo(before);
        await Assert.That(merged.Problems).IsEmpty();
    }

    /// <summary>Godot's Transform3D round trip costs about 1e-7. Without the guard, every save of
    /// an untouched scene rewrites every number in the file.</summary>
    [Test]
    public async Task a_transform_nudged_below_the_epsilon_is_not_rewritten()
    {
        var document = Canonical();
        var before = PrefabDocumentSerializer.Write(document);

        var merged = DocumentMerge.Apply(document,
        [
            State(RootGuid, "Root", null, new LocalTransform(
                new Vector3(1f + 1e-8f, 2f, 3f), Quaternion.Identity, Vector3.One)),
            Untouched()[1],
        ]);

        await Assert.That(PrefabDocumentSerializer.Write(merged.Document)).IsEqualTo(before);
    }

    [Test]
    public async Task a_real_move_is_written()
    {
        var document = Canonical();
        var merged = DocumentMerge.Apply(document,
        [
            State(RootGuid, "Root", null, new LocalTransform(
                new Vector3(9f, 2f, 3f), Quaternion.Identity, Vector3.One)),
            Untouched()[1],
        ]);

        var written = Reread(merged.Document);
        var transform = LocalTransformCodec.Read(
            written.Objects[0].Component(WellKnownComponents.TransformId)!.Data);
        await Assert.That(transform.Position.X).IsEqualTo(9f);
    }

    /// <summary>An edited field replaces its own key and nothing else — that is what keeps a diff
    /// to the line that changed.</summary>
    [Test]
    public async Task an_edited_field_replaces_only_itself_and_keeps_key_order()
    {
        var document = Canonical();
        var edits = new AuthoredEdits();
        edits.FieldChanged(BuoyancyId, "Density");

        var merged = DocumentMerge.Apply(document,
        [
            Untouched()[0],
            State(ChildGuid, "Child", RootGuid,
                new LocalTransform(Vector3.Zero, Quaternion.Identity, new Vector3(2f, 2f, 2f)),
                edits,
                new Dictionary<string, AuthoredValue>
                {
                    [BuoyancyId + "/Density"] = new(AuthoredValueKind.Number, Number: 0.25),
                }),
        ]);

        var payload = Reread(merged.Document).Objects[1].Component(Buoyancy)!.Data;
        await Assert.That(payload.Select(pair => pair.Key))
            .IsEquivalentTo(new[] { "Segments", "Density", "Label" });
        await Assert.That(payload.Value("Density")).IsEqualTo(0.25);
        await Assert.That(payload.Value("Segments")).IsEqualTo(12L);
        await Assert.That(payload.Value("Label")).IsEqualTo("hull");
    }

    /// <summary>The reason the document is the base rather than the scene: this addon cannot draw a
    /// component it has no schema for, and must not therefore delete it.</summary>
    [Test]
    public async Task a_component_the_addon_never_showed_survives_a_save()
    {
        var document = Canonical();
        var unknown = new CanonicalTomlTable();
        unknown.Add("SomethingNobodyHereKnows", 42L);
        document.Objects[1].Components.Add(
            new PrefabComponent(new Guid("dddddddd-0000-4000-8000-000000000009"), "Future.Component", unknown));
        document = Reread(document);

        var merged = DocumentMerge.Apply(document, Untouched());

        var carried = Reread(merged.Document).Objects[1]
            .Component(new Guid("dddddddd-0000-4000-8000-000000000009"));
        await Assert.That(carried).IsNotNull();
        await Assert.That(carried!.Data.Value("SomethingNobodyHereKnows")).IsEqualTo(42L);
    }

    [Test]
    public async Task a_removed_component_is_dropped()
    {
        var document = Canonical();
        var edits = new AuthoredEdits();
        edits.ComponentRemoved(BuoyancyId);

        var merged = DocumentMerge.Apply(document,
        [
            Untouched()[0],
            State(ChildGuid, "Child", RootGuid,
                new LocalTransform(Vector3.Zero, Quaternion.Identity, new Vector3(2f, 2f, 2f)), edits),
        ]);

        await Assert.That(Reread(merged.Document).Objects[1].Component(Buoyancy)).IsNull();
    }

    /// <summary>An added component has nothing in the document to override, so its whole payload
    /// comes from the scene.</summary>
    [Test]
    public async Task an_added_component_is_written_whole()
    {
        var document = Canonical();
        var edits = new AuthoredEdits();
        edits.ComponentAdded(BuoyancyId);

        var merged = DocumentMerge.Apply(document,
        [
            State(RootGuid, "Root", null,
                new LocalTransform(new Vector3(1f, 2f, 3f), Quaternion.Identity, Vector3.One),
                edits,
                new Dictionary<string, AuthoredValue>
                {
                    [BuoyancyId + "/Segments"] = new(AuthoredValueKind.Integer, Integer: 3),
                    [BuoyancyId + "/Tint"] = new(AuthoredValueKind.Rgba, Numbers: [1f, 0f, 0f, 1f]),
                }),
            Untouched()[1],
        ]);

        var added = Reread(merged.Document).Objects[0].Component(Buoyancy);
        await Assert.That(added).IsNotNull();
        await Assert.That(added!.Data.Value("Segments")).IsEqualTo(3L);
        await Assert.That((added.Data.Value("Tint") as CanonicalTomlTable)!.Value("r")).IsEqualTo(1.0);
    }

    [Test]
    public async Task a_renamed_node_renames_its_object()
    {
        var document = Canonical();
        var merged = DocumentMerge.Apply(document,
        [
            State(RootGuid, "Renamed", null,
                new LocalTransform(new Vector3(1f, 2f, 3f), Quaternion.Identity, Vector3.One)),
            Untouched()[1],
        ]);

        await Assert.That(Reread(merged.Document).Objects[0].Name).IsEqualTo("Renamed");
    }

    /// <summary>Absent is how the format spells "root"; an empty guid would read as a broken
    /// reference. Reparenting the ROOT object itself is not the case — a document has exactly one
    /// root, so this moves a grandchild up instead.</summary>
    [Test]
    public async Task reparenting_drops_the_parent_key_when_the_new_parent_is_the_root()
    {
        var grandchild = new Guid("44444444-4444-4444-8444-444444444444");
        var document = Canonical();
        var third = PrefabObject.WithMeta(grandchild, "GrandChild", parent: ChildGuid);
        third.Components.Add(LocalTransformCodec.Write(LocalTransform.Identity));
        document.Objects.Add(third);
        document = Reread(document);

        // Root keeps its parentless meta; the grandchild moves under Root, so its Parent key
        // changes rather than disappearing — and the ROOT's stays absent throughout.
        var merged = DocumentMerge.Apply(document,
        [
            .. Untouched(),
            State(grandchild, "GrandChild", RootGuid, LocalTransform.Identity),
        ]);

        var written = Reread(merged.Document);
        await Assert.That(written.Objects[2].Parent).IsEqualTo(RootGuid);
        await Assert.That(written.Objects[0]
            .Component(WellKnownComponents.MetaId)!.Data.ContainsKey(WellKnownComponents.Parent)).IsFalse();
    }

    [Test]
    public async Task an_object_with_no_node_left_is_deleted()
    {
        var document = Canonical();
        var merged = DocumentMerge.Apply(document, [Untouched()[0]]);

        await Assert.That(merged.Document.Objects.Count).IsEqualTo(1);
        await Assert.That(merged.Document.Objects[0].Guid).IsEqualTo(RootGuid);
    }

    /// <summary>The loader orders parents-first for Godot's sake; emitting THAT order would
    /// reshuffle a document on every save of a scene nobody edited.</summary>
    [Test]
    public async Task object_order_follows_the_document_not_the_scene()
    {
        var document = Canonical();
        var reversed = Untouched().Reverse().ToList();

        var merged = DocumentMerge.Apply(document, reversed);

        await Assert.That(merged.Document.Objects.Select(o => o.Name ?? ""))
            .IsEquivalentTo(new[] { "Root", "Child" });
    }

    [Test]
    public async Task a_new_object_is_appended()
    {
        var document = Canonical();
        var fresh = new Guid("99999999-9999-4999-8999-999999999999");
        var merged = DocumentMerge.Apply(document,
        [
            .. Untouched(),
            State(fresh, "Placed", RootGuid,
                new LocalTransform(new Vector3(4f, 0f, 0f), Quaternion.Identity, Vector3.One)),
        ]);

        var written = Reread(merged.Document);
        await Assert.That(written.Objects.Select(o => o.Name ?? ""))
            .IsEquivalentTo(new[] { "Root", "Child", "Placed" });
        await Assert.That(written.Objects[2].Parent).IsEqualTo(RootGuid);
        await Assert.That(LocalTransformCodec.Read(
            written.Objects[2].Component(WellKnownComponents.TransformId)!.Data).Position.X).IsEqualTo(4f);
    }

    /// <summary>An override carrier addresses a prefab child rather than being one, so it never had
    /// a node and must not be read as deleted.</summary>
    [Test]
    public async Task an_override_carrier_survives_having_no_node()
    {
        var document = Canonical();
        var carrier = new CanonicalTomlTable();
        carrier.Add(WellKnownComponents.Parent, DocumentGuid.Format(ChildGuid));
        carrier.Add(WellKnownComponents.Target, DocumentGuid.Format(ChildGuid));
        document.Objects.Add(new PrefabObject
        {
            Components = { new PrefabComponent(WellKnownComponents.MetaId, WellKnownComponents.MetaType, carrier) },
        });
        document = Reread(document);

        var merged = DocumentMerge.Apply(document, Untouched());

        await Assert.That(merged.Document.Objects.Count).IsEqualTo(3);
        await Assert.That(merged.Document.Objects[2].Target).IsEqualTo(ChildGuid);
    }
}

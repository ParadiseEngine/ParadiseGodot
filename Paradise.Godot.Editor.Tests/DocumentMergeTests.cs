using System.Numerics;
using Paradise.Assets.Documents;
using ParadiseGodot.Documents;

namespace Paradise.Godot.Editor.Tests;

public class DocumentMergeTests
{
    private static readonly Guid RootGuid = new("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ChildGuid = new("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Buoyancy = new(BuoyancyId);

    private const string BuoyancyId = "aa11bb22-cc33-4d44-8e55-ff6677889900";

    // Canonical serializer output must round-trip byte-for-byte; hand-written TOML may normalize.
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

    private static PrefabDocument Reread(PrefabDocument document) =>
        PrefabDocumentSerializer.Parse(PrefabDocumentSerializer.Write(document), "fixture");

    private static DocumentMerge.ObjectState State(
        Guid guid, string name, Guid? parent, LocalTransform transform) =>
        new(guid, name, parent, transform, new AuthoredEdits(), new Dictionary<string, AuthoredValue>());

    private static DocumentMerge.ObjectState RootState() =>
        State(RootGuid, "Root", null,
            new LocalTransform(new Vector3(1f, 2f, 3f), Quaternion.Identity, Vector3.One));

    private static DocumentMerge.ObjectState ChildState() =>
        State(ChildGuid, "Child", RootGuid,
            new LocalTransform(Vector3.Zero, Quaternion.Identity, new Vector3(2f, 2f, 2f)));

    private static IReadOnlyList<DocumentMerge.ObjectState> Untouched() => [RootState(), ChildState()];

    [Test]
    public async Task an_untouched_scene_round_trips_byte_identically()
    {
        var document = Canonical();
        var before = PrefabDocumentSerializer.Write(document);

        var merged = DocumentMerge.Apply(document, Untouched());

        await Assert.That(PrefabDocumentSerializer.Write(merged.Document)).IsEqualTo(before);
        await Assert.That(merged.Problems).IsEmpty();
    }

    // Godot Transform3D round trips introduce about 1e-7 of error.
    [Test]
    public async Task a_transform_nudged_below_the_epsilon_is_not_rewritten()
    {
        var document = Canonical();
        var before = PrefabDocumentSerializer.Write(document);

        var merged = DocumentMerge.Apply(document,
        [
            State(RootGuid, "Root", null, new LocalTransform(
                new Vector3(1f + 1e-8f, 2f, 3f), Quaternion.Identity, Vector3.One)),
            ChildState(),
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
            ChildState(),
        ]);

        var written = Reread(merged.Document);
        var transform = LocalTransformCodec.Read(
            written.Objects[0].Component(WellKnownComponents.TransformId)!.Data);
        await Assert.That(transform.Position.X).IsEqualTo(9f);
    }

    [Test]
    public async Task an_edited_field_replaces_only_itself_and_keeps_key_order()
    {
        var document = Canonical();
        var edits = new AuthoredEdits();
        edits.FieldChanged(BuoyancyId, "Density");

        var merged = DocumentMerge.Apply(document,
        [
            RootState(),
            ChildState() with
            {
                Edits = edits,
                Values = new Dictionary<string, AuthoredValue>
                {
                    [BuoyancyId + "/Density"] = new(AuthoredValueKind.Number, Number: 0.25),
                },
            },
        ]);

        var payload = Reread(merged.Document).Objects[1].Component(Buoyancy)!.Data;
        await Assert.That(payload.Select(pair => pair.Key))
            .IsEquivalentTo(new[] { "Segments", "Density", "Label" });
        await Assert.That(payload.Value("Density")).IsEqualTo(0.25);
        await Assert.That(payload.Value("Segments")).IsEqualTo(12L);
        await Assert.That(payload.Value("Label")).IsEqualTo("hull");
    }

    [Test]
    public async Task nested_edits_and_baked_fields_preserve_unrelated_values_and_order()
    {
        var document = Canonical();
        var payload = document.Objects[1].Component(Buoyancy)!.Data;
        var shape = new CanonicalTomlTable();
        shape.Add("Radius", 1.0);
        shape.Add("Height", 2.0);
        payload.Add("Shape", shape);
        var edits = new AuthoredEdits();
        edits.FieldChanged(BuoyancyId, "Shape/Radius");
        edits.FieldChanged(BuoyancyId, "Density");
        edits.FieldChanged(BuoyancyId, "Label");

        var result = DocumentMerge.Apply(document,
        [
            RootState(),
            ChildState() with
            {
                Edits = edits,
                HostBaked = [BuoyancyId + "/Shape/Radius", BuoyancyId + "/Shape/Height"],
                Values = new Dictionary<string, AuthoredValue>
                {
                    [BuoyancyId + "/Shape/Radius"] = new(AuthoredValueKind.Number, Number: 0.5),
                    [BuoyancyId + "/Shape/Height"] = new(AuthoredValueKind.Number, Number: 3.0),
                    [BuoyancyId + "/Density"] = AuthoredValue.None,
                },
            },
        ]);

        var merged = Reread(result.Document).Objects[1].Component(Buoyancy)!.Data;
        await Assert.That(merged.Select(entry => entry.Key))
            .IsEquivalentTo(["Segments", "Density", "Label", "Shape"]);
        await Assert.That(merged.Value("Density")).IsEqualTo(0.75);
        await Assert.That(merged.Value("Label")).IsEqualTo("hull");
        var mergedShape = (CanonicalTomlTable)merged.Value("Shape")!;
        await Assert.That(mergedShape.Select(entry => entry.Key)).IsEquivalentTo(["Radius", "Height"]);
        await Assert.That(mergedShape.Value("Radius")).IsEqualTo(0.5);
        await Assert.That(mergedShape.Value("Height")).IsEqualTo(3.0);
    }

    // Components without a schema cannot be shown, but must survive saves.
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
            RootState(),
            ChildState() with { Edits = edits },
        ]);

        await Assert.That(Reread(merged.Document).Objects[1].Component(Buoyancy)).IsNull();
    }

    [Test]
    public async Task an_added_component_is_written_whole()
    {
        var document = Canonical();
        var edits = new AuthoredEdits();
        edits.ComponentAdded(BuoyancyId);

        var merged = DocumentMerge.Apply(document,
        [
            RootState() with
            {
                Edits = edits,
                Values = new Dictionary<string, AuthoredValue>
                {
                    [BuoyancyId + "/Segments"] = new(AuthoredValueKind.Integer, Integer: 3),
                    [BuoyancyId + "/Tint"] = new(AuthoredValueKind.Rgba, Numbers: [1f, 0f, 0f, 1f]),
                },
            },
            ChildState(),
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
            RootState() with { Name = "Renamed" },
            ChildState(),
        ]);

        await Assert.That(Reread(merged.Document).Objects[0].Name).IsEqualTo("Renamed");
    }

    // A parentless root omits Parent; an empty GUID would be a broken reference.
    [Test]
    public async Task reparenting_drops_the_parent_key_when_the_new_parent_is_the_root()
    {
        var grandchild = new Guid("44444444-4444-4444-8444-444444444444");
        var document = Canonical();
        var third = PrefabObject.WithMeta(grandchild, "GrandChild", parent: ChildGuid);
        third.Components.Add(LocalTransformCodec.Write(LocalTransform.Identity));
        document.Objects.Add(third);
        document = Reread(document);

        // Move the grandchild under Root; Root itself remains parentless.
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
        var merged = DocumentMerge.Apply(document, [RootState()]);

        await Assert.That(merged.Document.Objects.Count).IsEqualTo(1);
        await Assert.That(merged.Document.Objects[0].Guid).IsEqualTo(RootGuid);
    }

    // The loader sorts parents first, but saving that order would create unrelated diffs.
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

    // The reader recognizes references by inline-table shape; a header table would lose that meaning.
    [Test]
    public async Task a_reference_round_trips_as_an_inline_table()
    {
        var document = Canonical();
        var edits = new AuthoredEdits();
        edits.FieldChanged(BuoyancyId, "Label");
        var guid = new Guid("aaaaaaaa-1111-4111-8111-111111111111");

        var merged = DocumentMerge.Apply(document,
        [
            RootState(),
            ChildState() with
            {
                Edits = edits,
                Values = new Dictionary<string, AuthoredValue>
                {
                    [BuoyancyId + "/Label"] = AuthoredValue.Reference(guid, "penguins/adelie.glb"),
                },
            },
        ]);

        var written = PrefabDocumentSerializer.Write(merged.Document);
        await Assert.That(written).Contains("Label = { guid = ");

        var read = AuthoredPayload.Read(
            Reread(merged.Document).Objects[1].Component(Buoyancy)!.Data,
            "Label",
            global::Godot.Variant.Type.String);
        await Assert.That(read.Kind).IsEqualTo(AuthoredValueKind.Reference);
        await Assert.That(read.Identity).IsEqualTo(guid);
        await Assert.That(read.Text).IsEqualTo("penguins/adelie.glb");
    }

    // Override carriers address prefab children and have no corresponding node to delete.
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

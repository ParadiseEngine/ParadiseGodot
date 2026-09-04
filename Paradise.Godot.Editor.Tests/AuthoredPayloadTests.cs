using Paradise.Assets.Documents;
using ParadiseGodot.Documents;
using VariantType = global::Godot.Variant.Type;

namespace Paradise.Godot.Editor.Tests;

/// <summary>
/// Reading a document payload at the schema's declared types.
/// </summary>
/// <remarks>
/// Only <c>Variant.Type</c> appears here — a plain enum. A <c>Variant</c> VALUE would segfault the
/// test host (see <c>.claude/lessons.md</c>), which is exactly why the conversion is split so this
/// half can be tested at all.
/// </remarks>
public class AuthoredPayloadTests
{
    private static CanonicalTomlTable Table(params (string Key, object Value)[] pairs)
    {
        var table = new CanonicalTomlTable();
        foreach (var (key, value) in pairs) table.Add(key, value);
        return table;
    }

    [Test]
    public async Task scalars_read_at_their_declared_type()
    {
        var data = Table(("Flag", true), ("Count", 7L), ("Speed", 2.5), ("Label", "hello"));

        await Assert.That(AuthoredPayload.Read(data, "Flag", VariantType.Bool).Bool).IsTrue();
        await Assert.That(AuthoredPayload.Read(data, "Count", VariantType.Int).Integer).IsEqualTo(7L);
        await Assert.That(AuthoredPayload.Read(data, "Speed", VariantType.Float).Number).IsEqualTo(2.5);
        await Assert.That(AuthoredPayload.Read(data, "Label", VariantType.String).Text).IsEqualTo("hello");
    }

    /// <summary>Canonical TOML widens 1.0 to 1, so a whole number arrives as an integer. A float
    /// field that refused it would drop every round value an author typed.</summary>
    [Test]
    public async Task a_float_field_accepts_a_whole_number_written_as_an_integer()
    {
        var value = AuthoredPayload.Read(Table(("Speed", 3L)), "Speed", VariantType.Float);

        await Assert.That(value.Kind).IsEqualTo(AuthoredValueKind.Number);
        await Assert.That(value.Number).IsEqualTo(3.0);
    }

    /// <summary>The reverse is NOT symmetric: rounding an authored 2.5 into an int field would
    /// change the value silently, and leaving the default is the honest answer.</summary>
    [Test]
    public async Task an_int_field_refuses_a_fractional_number()
    {
        await Assert.That(AuthoredPayload.Read(Table(("Count", 2.5)), "Count", VariantType.Int).Kind)
            .IsEqualTo(AuthoredValueKind.None);
    }

    [Test]
    public async Task vectors_and_quaternions_read_as_float_runs()
    {
        var data = Table(
            ("Size", new object[] { 1.0, 2.0 }),
            ("Offset", new object[] { 1.0, 2.0, 3.0 }),
            ("Spin", new object[] { 0.0, 0.0, 0.0, 1.0 }));

        await Assert.That(AuthoredPayload.Read(data, "Size", VariantType.Vector2).Numbers)
            .IsEquivalentTo(new[] { 1f, 2f });
        await Assert.That(AuthoredPayload.Read(data, "Offset", VariantType.Vector3).Numbers)
            .IsEquivalentTo(new[] { 1f, 2f, 3f });
        await Assert.That(AuthoredPayload.Read(data, "Spin", VariantType.Quaternion).Numbers)
            .IsEquivalentTo(new[] { 0f, 0f, 0f, 1f });
    }

    /// <summary>The failure this guards is real: <c>Position = [0.0, 1.5]</c> once baked silently
    /// as the origin, and a reader that took a short run would put that back.</summary>
    [Test]
    public async Task a_run_of_the_wrong_length_reads_as_absent()
    {
        var data = Table(("Offset", new object[] { 1.0, 2.0 }));

        await Assert.That(AuthoredPayload.Read(data, "Offset", VariantType.Vector3).Kind)
            .IsEqualTo(AuthoredValueKind.None);
    }

    [Test]
    public async Task a_colour_reads_from_the_rgba_table_the_contract_writes()
    {
        var data = Table(("Tint", Table(("r", 1.0), ("g", 0.5), ("b", 0.0), ("a", 0.25))));

        await Assert.That(AuthoredPayload.Read(data, "Tint", VariantType.Color).Numbers)
            .IsEquivalentTo(new[] { 1f, 0.5f, 0f, 0.25f });
    }

    /// <summary>A colour written without alpha is opaque, not invisible.</summary>
    [Test]
    public async Task a_colour_without_alpha_is_opaque()
    {
        var data = Table(("Tint", Table(("r", 1.0), ("g", 1.0), ("b", 1.0))));

        await Assert.That(AuthoredPayload.Read(data, "Tint", VariantType.Color).Numbers![3]).IsEqualTo(1f);
    }

    /// <summary>Hand-edited documents write colours as arrays; taking both costs nothing and saves
    /// an author from a field that silently ignores what they typed.</summary>
    [Test]
    public async Task a_colour_also_reads_from_a_four_float_array()
    {
        var data = Table(("Tint", new object[] { 0.0, 0.25, 0.5, 1.0 }));
        var value = AuthoredPayload.Read(data, "Tint", VariantType.Color);

        await Assert.That(value.Kind).IsEqualTo(AuthoredValueKind.Rgba);
        await Assert.That(value.Numbers).IsEquivalentTo(new[] { 0f, 0.25f, 0.5f, 1f });
    }

    /// <summary>A field path nests, because the exporter writes it nested.</summary>
    [Test]
    public async Task a_slash_path_walks_into_nested_tables()
    {
        var data = Table(("Collider", Table(("Radius", 0.5), ("Shape", Table(("Kind", "Sphere"))))));

        await Assert.That(AuthoredPayload.Read(data, "Collider/Radius", VariantType.Float).Number)
            .IsEqualTo(0.5);
        await Assert.That(AuthoredPayload.Read(data, "Collider/Shape/Kind", VariantType.String).Text)
            .IsEqualTo("Sphere");
    }

    [Test]
    public async Task a_path_through_a_missing_or_non_table_branch_reads_as_absent()
    {
        var data = Table(("Collider", 5.0));

        await Assert.That(AuthoredPayload.Read(data, "Collider/Radius", VariantType.Float).Kind)
            .IsEqualTo(AuthoredValueKind.None);
        await Assert.That(AuthoredPayload.Read(data, "Nothing/At/All", VariantType.Float).Kind)
            .IsEqualTo(AuthoredValueKind.None);
    }

    private static CanonicalInlineTable Inline(params (string Key, object Value)[] pairs)
    {
        var table = new CanonicalInlineTable();
        foreach (var (key, value) in pairs) table.Add(key, value);
        return table;
    }

    /// <summary>A reference and a name share the schema type <c>string</c>, because a GUID travels
    /// as one. Shape is what tells them apart.</summary>
    [Test]
    public async Task an_inline_guid_and_path_table_reads_as_a_reference()
    {
        var data = Table(("Mesh", Inline(
            ("guid", "aaaaaaaa-1111-4111-8111-111111111111"),
            ("path", "penguins/adelie.glb"))));

        var value = AuthoredPayload.Read(data, "Mesh", VariantType.String);

        await Assert.That(value.Kind).IsEqualTo(AuthoredValueKind.Reference);
        await Assert.That(value.Identity).IsEqualTo(new Guid("aaaaaaaa-1111-4111-8111-111111111111"));
        await Assert.That(value.Text).IsEqualTo("penguins/adelie.glb");
    }

    /// <summary>An empty slot is a real value — "no material here, keep the GLB's own" — and is not
    /// the same as a field nobody wrote. Dropping it would shift every material after it onto the
    /// wrong primitive.</summary>
    [Test]
    public async Task an_empty_inline_table_is_a_reference_to_nothing_rather_than_absent()
    {
        var value = AuthoredPayload.Read(Table(("Mesh", Inline())), "Mesh", VariantType.String);

        await Assert.That(value.Kind).IsEqualTo(AuthoredValueKind.Reference);
        await Assert.That(value.Identity).IsEqualTo(Guid.Empty);
        await Assert.That(value.Text).IsEqualTo("");
    }

    /// <summary>A path with no identity is what a hand-written document carries, and it still has to
    /// resolve — the GUID is authoritative, not mandatory.</summary>
    [Test]
    public async Task a_reference_with_only_a_path_still_reads()
    {
        var value = AuthoredPayload.Read(
            Table(("Mesh", Inline(("path", "penguins/adelie.glb")))), "Mesh", VariantType.String);

        await Assert.That(value.Kind).IsEqualTo(AuthoredValueKind.Reference);
        await Assert.That(value.Text).IsEqualTo("penguins/adelie.glb");
    }

    /// <summary>A plain string in a reference-shaped field is still a string: the schema cannot tell
    /// them apart, so the document has to.</summary>
    [Test]
    public async Task a_bare_string_in_the_same_field_is_still_a_name()
    {
        var value = AuthoredPayload.Read(Table(("Mesh", "just a name")), "Mesh", VariantType.String);

        await Assert.That(value.Kind).IsEqualTo(AuthoredValueKind.Text);
        await Assert.That(value.Text).IsEqualTo("just a name");
    }

    /// <summary>The distinction the caller depends on: absent means "use the schema default", and a
    /// zero would overwrite what an author set with something they never typed.</summary>
    [Test]
    public async Task a_value_in_the_wrong_shape_reads_as_absent_rather_than_zero()
    {
        var data = Table(("Speed", "fast"), ("Flag", 1L), ("Label", 3.0));

        await Assert.That(AuthoredPayload.Read(data, "Speed", VariantType.Float).Kind)
            .IsEqualTo(AuthoredValueKind.None);
        await Assert.That(AuthoredPayload.Read(data, "Flag", VariantType.Bool).Kind)
            .IsEqualTo(AuthoredValueKind.None);
        await Assert.That(AuthoredPayload.Read(data, "Label", VariantType.String).Kind)
            .IsEqualTo(AuthoredValueKind.None);
    }
}

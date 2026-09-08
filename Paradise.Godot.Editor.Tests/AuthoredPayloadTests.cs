using Paradise.Assets.Documents;
using ParadiseGodot.Documents;
using VariantType = global::Godot.Variant.Type;

namespace Paradise.Godot.Editor.Tests;

// Variant.Type is safe here; constructing a Variant segfaults the test host.
// See .claude/lessons.md.
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

    // Canonical TOML writes 1.0 as 1, so float fields must accept integers.
    [Test]
    public async Task a_float_field_accepts_a_whole_number_written_as_an_integer()
    {
        var value = AuthoredPayload.Read(Table(("Speed", 3L)), "Speed", VariantType.Float);

        await Assert.That(value.Kind).IsEqualTo(AuthoredValueKind.Number);
        await Assert.That(value.Number).IsEqualTo(3.0);
    }

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

    // Regression: a short Position = [0.0, 1.5] once silently baked as the origin.
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

    [Test]
    public async Task a_colour_without_alpha_is_opaque()
    {
        var data = Table(("Tint", Table(("r", 1.0), ("g", 1.0), ("b", 1.0))));

        await Assert.That(AuthoredPayload.Read(data, "Tint", VariantType.Color).Numbers![3]).IsEqualTo(1f);
    }

    // Hand-edited documents may use arrays for colours.
    [Test]
    public async Task a_colour_also_reads_from_a_four_float_array()
    {
        var data = Table(("Tint", new object[] { 0.0, 0.25, 0.5, 1.0 }));
        var value = AuthoredPayload.Read(data, "Tint", VariantType.Color);

        await Assert.That(value.Kind).IsEqualTo(AuthoredValueKind.Rgba);
        await Assert.That(value.Numbers).IsEquivalentTo(new[] { 0f, 0.25f, 0.5f, 1f });
    }

    // Match the exporter's nested field structure.
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

    // References and names share the schema type string; the payload shape distinguishes them.
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

    // Empty slots retain the GLB material; dropping them shifts later materials to the wrong primitive.
    [Test]
    public async Task an_empty_inline_table_is_a_reference_to_nothing_rather_than_absent()
    {
        var value = AuthoredPayload.Read(Table(("Mesh", Inline())), "Mesh", VariantType.String);

        await Assert.That(value.Kind).IsEqualTo(AuthoredValueKind.Reference);
        await Assert.That(value.Identity).IsEqualTo(Guid.Empty);
        await Assert.That(value.Text).IsEqualTo("");
    }

    [Test]
    public async Task a_reference_with_only_a_path_still_reads()
    {
        var value = AuthoredPayload.Read(
            Table(("Mesh", Inline(("path", "penguins/adelie.glb")))), "Mesh", VariantType.String);

        await Assert.That(value.Kind).IsEqualTo(AuthoredValueKind.Reference);
        await Assert.That(value.Text).IsEqualTo("penguins/adelie.glb");
    }

    [Test]
    public async Task a_bare_string_in_the_same_field_is_still_a_name()
    {
        var value = AuthoredPayload.Read(Table(("Mesh", "just a name")), "Mesh", VariantType.String);

        await Assert.That(value.Kind).IsEqualTo(AuthoredValueKind.Text);
        await Assert.That(value.Text).IsEqualTo("just a name");
    }

    // Absent selects the schema default; zero would overwrite it.
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

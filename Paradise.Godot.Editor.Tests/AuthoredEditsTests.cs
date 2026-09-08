using ParadiseGodot.Documents;

namespace Paradise.Godot.Editor.Tests;

public class AuthoredEditsTests
{
    private const string Rigidbody = "b7ab4dd8-c8da-4dc2-9e5e-192fd74deb11";
    private const string Collider = "e1cd1bc8-86f2-4225-adc9-4a324c70ebf9";

    [Test]
    public async Task a_fresh_overlay_has_nothing_to_apply()
    {
        var edits = new AuthoredEdits();

        await Assert.That(edits.Any).IsFalse();
        await Assert.That(edits.Added).IsEmpty();
        await Assert.That(edits.Removed).IsEmpty();
        await Assert.That(edits.Fields).IsEmpty();
    }

    [Test]
    public async Task an_edited_field_is_recorded_against_its_component()
    {
        var edits = new AuthoredEdits();
        edits.FieldChanged(Rigidbody, "Mass");

        await Assert.That(edits.Any).IsTrue();
        await Assert.That(edits.IsFieldEdited(Rigidbody, "Mass")).IsTrue();
        await Assert.That(edits.IsFieldEdited(Rigidbody, "Friction")).IsFalse();
        await Assert.That(edits.IsFieldEdited(Collider, "Mass")).IsFalse();
        await Assert.That(edits.FieldsOf(Rigidbody)).IsEquivalentTo(new[] { "Mass" });
    }

    // Anchor component prefixes on the separator to keep nested fields in their own component.
    [Test]
    public async Task nested_field_paths_survive_the_round_trip()
    {
        var edits = new AuthoredEdits();
        edits.FieldChanged(Collider, "Shape/Radius");

        await Assert.That(edits.FieldsOf(Collider)).IsEquivalentTo(new[] { "Shape/Radius" });
        await Assert.That(edits.FieldsOf(Rigidbody)).IsEmpty();
    }

    [Test]
    public async Task shared_component_prefixes_do_not_mix_their_fields()
    {
        var edits = new AuthoredEdits();
        edits.FieldChanged("body", "Mass");
        edits.FieldChanged("body-extra", "Shape/Radius");

        await Assert.That(edits.FieldsOf("body")).IsEquivalentTo(["Mass"]);
        edits.ComponentRemoved("body");

        await Assert.That(edits.FieldsOf("body")).IsEmpty();
        await Assert.That(edits.FieldsOf("body-extra")).IsEquivalentTo(["Shape/Radius"]);
    }

    [Test]
    public async Task adding_then_removing_a_component_leaves_only_the_removal()
    {
        var edits = new AuthoredEdits();
        edits.ComponentAdded(Rigidbody);
        edits.ComponentRemoved(Rigidbody);

        await Assert.That(edits.Added).IsEmpty();
        await Assert.That(edits.Removed).IsEquivalentTo(new[] { Rigidbody });
    }

    [Test]
    public async Task removing_then_adding_a_component_leaves_only_the_addition()
    {
        var edits = new AuthoredEdits();
        edits.ComponentRemoved(Rigidbody);
        edits.ComponentAdded(Rigidbody);

        await Assert.That(edits.Removed).IsEmpty();
        await Assert.That(edits.Added).IsEquivalentTo(new[] { Rigidbody });
    }

    [Test]
    public async Task removing_a_component_forgets_its_edited_fields()
    {
        var edits = new AuthoredEdits();
        edits.FieldChanged(Rigidbody, "Mass");
        edits.FieldChanged(Collider, "Layer");
        edits.ComponentRemoved(Rigidbody);

        await Assert.That(edits.FieldsOf(Rigidbody)).IsEmpty();
        await Assert.That(edits.FieldsOf(Collider)).IsEquivalentTo(new[] { "Layer" });
    }

    [Test]
    public async Task editing_a_field_of_a_removed_component_re_adds_it()
    {
        var edits = new AuthoredEdits();
        edits.ComponentRemoved(Rigidbody);
        edits.FieldChanged(Rigidbody, "Mass");

        await Assert.That(edits.Removed).IsEmpty();
        await Assert.That(edits.Added).IsEquivalentTo(new[] { Rigidbody });
        await Assert.That(edits.IsFieldEdited(Rigidbody, "Mass")).IsTrue();
    }

    [Test]
    public async Task clearing_leaves_nothing_to_apply()
    {
        var edits = new AuthoredEdits();
        edits.ComponentAdded(Rigidbody);
        edits.FieldChanged(Rigidbody, "Mass");
        edits.ComponentRemoved(Collider);
        edits.Clear();

        await Assert.That(edits.Any).IsFalse();
    }
}

using Paradise.Assets.Documents;
using ParadiseGodot.Documents;

namespace Paradise.Godot.Editor.Tests;

// Malformed documents must remain openable so authors can repair them.
public class DocumentTreeTests
{
    private static readonly Guid A = new("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = new("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid C = new("cccccccc-0000-4000-8000-000000000003");
    private static readonly Guid Missing = new("dddddddd-0000-4000-8000-000000000004");

    private static PrefabDocument Document(params PrefabObject[] objects)
    {
        var document = new PrefabDocument();
        foreach (var entry in objects) document.Objects.Add(entry);
        return document;
    }

    private static List<string> Names(DocumentTree.Result result) =>
        result.Nodes.Select(n => n.Object.Name ?? "").ToList();

    [Test]
    public async Task a_child_listed_before_its_parent_is_still_placed_after_it()
    {
        var result = DocumentTree.Order(Document(
            PrefabObject.WithMeta(B, "Child", parent: A),
            PrefabObject.WithMeta(A, "Parent")));

        await Assert.That(Names(result)).IsEquivalentTo(new[] { "Parent", "Child" });
        await Assert.That(result.Nodes[0].ParentIndex).IsEqualTo(-1);
        await Assert.That(result.Nodes[1].ParentIndex).IsEqualTo(0);
        await Assert.That(result.Problems).IsEmpty();
    }

    [Test]
    public async Task a_subtree_is_placed_contiguously()
    {
        var result = DocumentTree.Order(Document(
            PrefabObject.WithMeta(A, "Root"),
            PrefabObject.WithMeta(C, "OtherRoot"),
            PrefabObject.WithMeta(B, "Child", parent: A)));

        await Assert.That(Names(result)).IsEquivalentTo(new[] { "Root", "Child", "OtherRoot" });
    }

    [Test]
    public async Task siblings_keep_document_order()
    {
        var result = DocumentTree.Order(Document(
            PrefabObject.WithMeta(A, "Root"),
            PrefabObject.WithMeta(B, "First", parent: A),
            PrefabObject.WithMeta(C, "Second", parent: A)));

        await Assert.That(Names(result)).IsEquivalentTo(new[] { "Root", "First", "Second" });
    }

    [Test]
    public async Task an_object_naming_a_parent_the_document_lacks_becomes_a_root()
    {
        var result = DocumentTree.Order(Document(
            PrefabObject.WithMeta(A, "Orphan", parent: Missing)));

        await Assert.That(Names(result)).IsEquivalentTo(new[] { "Orphan" });
        await Assert.That(result.Nodes[0].ParentIndex).IsEqualTo(-1);
        await Assert.That(result.Problems.Count).IsEqualTo(1);
        await Assert.That(result.Problems[0]).Contains("Orphan");
    }

    [Test]
    public async Task a_parent_cycle_is_reported_and_every_object_still_appears()
    {
        var result = DocumentTree.Order(Document(
            PrefabObject.WithMeta(A, "Loop1", parent: B),
            PrefabObject.WithMeta(B, "Loop2", parent: A)));

        await Assert.That(result.Nodes.Count).IsEqualTo(2);
        await Assert.That(result.Problems).IsNotEmpty();
    }

    [Test]
    public async Task an_object_parented_to_itself_becomes_a_root()
    {
        var result = DocumentTree.Order(Document(PrefabObject.WithMeta(A, "Self", parent: A)));

        await Assert.That(result.Nodes.Count).IsEqualTo(1);
        await Assert.That(result.Nodes[0].ParentIndex).IsEqualTo(-1);
        await Assert.That(result.Problems).IsNotEmpty();
    }

    // Silently accepting a duplicate identity would drop an object from the tree.
    [Test]
    public async Task a_duplicate_identity_is_reported()
    {
        var result = DocumentTree.Order(Document(
            PrefabObject.WithMeta(A, "First"),
            PrefabObject.WithMeta(A, "Second")));

        await Assert.That(result.Problems.Count).IsEqualTo(1);
        await Assert.That(result.Problems[0]).Contains("Second");
    }

    [Test]
    public async Task an_empty_document_orders_to_nothing()
    {
        var result = DocumentTree.Order(new PrefabDocument());

        await Assert.That(result.Nodes).IsEmpty();
        await Assert.That(result.Problems).IsEmpty();
    }
}

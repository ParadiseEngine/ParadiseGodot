using Paradise.Assets.Documents;
using ParadiseGodot.Documents;

namespace Paradise.Godot.Editor.Tests;

/// <summary>
/// Ordering a document for a host that must create a parent before its child. The cases that
/// matter are the malformed ones: a document an author needs to OPEN in order to fix must not be
/// the one the loader refuses.
/// </summary>
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

    /// <summary>The whole reason this type exists: a document may list a child first.</summary>
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

    /// <summary>A subtree is contiguous, so the built scene reads like the document rather than
    /// like a breadth-first shuffle of it.</summary>
    [Test]
    public async Task a_subtree_is_placed_contiguously()
    {
        var result = DocumentTree.Order(Document(
            PrefabObject.WithMeta(A, "Root"),
            PrefabObject.WithMeta(C, "OtherRoot"),
            PrefabObject.WithMeta(B, "Child", parent: A)));

        await Assert.That(Names(result)).IsEquivalentTo(new[] { "Root", "Child", "OtherRoot" });
    }

    /// <summary>Sibling order is the document's, and a re-open that reshuffled it would move the
    /// scene an author is looking at.</summary>
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

    /// <summary>Two objects parented to each other. Every object still reaches the tree — losing
    /// them would leave an author with a document they cannot open and cannot repair.</summary>
    [Test]
    public async Task a_parent_cycle_is_reported_and_every_object_still_appears()
    {
        var result = DocumentTree.Order(Document(
            PrefabObject.WithMeta(A, "Loop1", parent: B),
            PrefabObject.WithMeta(B, "Loop2", parent: A)));

        await Assert.That(result.Nodes.Count).IsEqualTo(2);
        await Assert.That(result.Problems).IsNotEmpty();
    }

    /// <summary>A self-parent is the degenerate cycle, and the one a hand-edited document hits.</summary>
    [Test]
    public async Task an_object_parented_to_itself_becomes_a_root()
    {
        var result = DocumentTree.Order(Document(PrefabObject.WithMeta(A, "Self", parent: A)));

        await Assert.That(result.Nodes.Count).IsEqualTo(1);
        await Assert.That(result.Nodes[0].ParentIndex).IsEqualTo(-1);
        await Assert.That(result.Problems).IsNotEmpty();
    }

    /// <summary>A lookup can be last-wins about duplicates; building a tree from them drops an
    /// object, so it has to be said out loud.</summary>
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

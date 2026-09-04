#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using Paradise.Assets.Documents;
using Paradise.Export.Data;
using ParadiseGodot.Documents;

namespace ParadiseGodot.Authoring
{
    /// <summary>
    /// An entity node that carries components the ENGINE does not define: authored data declared
    /// by a game with <c>[Authored]</c>.
    ///
    /// <see cref="AuthoredEntityCore"/> implements this by interpreting the authoring schema,
    /// which is enough for every component a schema can describe. The interface exists so a game
    /// can also hand-roll a node when it needs authoring behaviour no schema expresses, and still
    /// have the exporter pick it up.
    /// </summary>
    /// It is also the ONLY way the exporters may refer to an authored entity: they cannot name the
    /// concrete node type, because that type is declared in the consuming game's assembly rather
    /// than here. A res:// script may not derive from a GodotObject-derived type in another
    /// assembly without breaking Godot's script reload (godotengine/godot#75352).
    public interface IAuthoredEntity
    {
        /// <summary>The node this entity IS. Exporters read transform, name and parentage through
        /// here, because the interface cannot itself derive from <see cref="Node3D"/>.</summary>
        Node3D Node { get; }

        /// <summary>The source GLB this entity renders, as authored on
        /// <see cref="RenderableComponentData"/>'s asset-bound <c>Mesh</c> field. Setting it
        /// enables the Renderable component exactly as ticking the box would, and the host bakes
        /// the res:// path to its data/-relative contract field at export.</summary>
        string ModelPath { get; set; }

        /// <summary>This entity's GUID as it stands, or <see cref="Guid.Empty"/> if it has none.
        /// Reading never mints one - the uniqueness scan compares every sibling through this, and
        /// must not write to nodes it is merely looking at.</summary>
        Guid EntityGuid { get; }

        /// <summary>Show the components this entity carries in its DOCUMENT. Seeding, not editing:
        /// an entity opened and closed without an author typing anything has nothing to write
        /// back.</summary>
        void AdoptDocumentComponents(IReadOnlyList<PrefabComponent> components);

        /// <summary>What the author has changed since this entity was materialized — the overlay a
        /// save applies over the document it re-reads.</summary>
        AuthoredEdits Edits { get; }

        /// <summary>Every authored value this entity holds, keyed
        /// <c>&lt;componentId&gt;/&lt;path&gt;</c>. Neutral values rather than Godot ones, so the
        /// merge that writes them into a document stays testable.</summary>
        IReadOnlyDictionary<string, AuthoredValue> AuthoredValues();

        /// <summary>Adopt a GUID the caller already has, rather than minting one. What the document
        /// loader uses: identity belongs to the document, and a node built from one that minted its
        /// own would orphan every reference pointing at it.</summary>
        /// <returns>False for <see cref="Guid.Empty"/>, which is not an identity.</returns>
        bool RestoreEntityGuid(Guid value);

        /// <summary>Ensure a stable GUID exists, minting and persisting one if the node has none.
        /// Without it every headless re-export mints a fresh id and the exported scene churns in
        /// git while nothing has actually changed.</summary>
        Guid EnsureEntityGuid();

        /// <summary>Every leaf this entity's host REFERENCES contribute, keyed
        /// <c>&lt;componentId&gt;/&lt;path&gt;</c>. Recomputed at save rather than tracked as an
        /// edit: moving the shape a collider points at changes the value, and nothing in the
        /// inspector would have noticed.</summary>
        IReadOnlyDictionary<string, AuthoredValue> BakedHostValues();

    }
}
#endif

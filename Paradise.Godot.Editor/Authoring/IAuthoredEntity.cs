#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using Paradise.Export.Data;

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

        /// <summary>The source asset, as <c>paradise.identity</c> authors it. Setting it enables
        /// the identity component, exactly as ticking the box would.</summary>
        string ModelPath { get; set; }

        /// <summary>What this entity is, as the exporter should record it.</summary>
        string ResolvedKind { get; }

        /// <summary>Ensure a stable GUID exists, minting and persisting one if the node has none.
        /// Without it every headless re-export mints a fresh id and the exported scene churns in
        /// git while nothing has actually changed.</summary>
        Guid EnsureEntityGuid();

        /// <summary>The components this node authors, ready for
        /// <see cref="EntityComponentsData.Custom"/>. An empty sequence exports nothing.</summary>
        IEnumerable<AuthoredComponentData> ExportAuthoredComponents();
    }
}
#endif

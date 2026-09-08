#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using Paradise.Assets.Documents;
using Paradise.Export.Data;
using ParadiseGodot.Documents;

namespace ParadiseGodot.Authoring
{
    /// <summary>A game-authored entity exposed to document loaders and exporters.</summary>
    /// <remarks>
    /// AuthoredEntityCore provides schema-driven behavior; custom nodes can implement this
    /// interface for behavior a schema cannot express. Exporters use it because the concrete
    /// script lives in the consuming assembly. Cross-assembly GodotObject inheritance breaks
    /// script reload (godotengine/godot#75352).
    /// </remarks>
    public interface IAuthoredEntity
    {
        /// <summary>Host node supplying name, transform and parentage.</summary>
        Node3D Node { get; }

        /// <summary>The entity's .mesh/.skinnedmesh document or source GLB under assets/.
        /// Setting it enables its component; baking resolves it to a document reference.</summary>
        string ModelPath { get; set; }

        /// <summary>Current GUID, or Guid.Empty. Reading never mints an id, so uniqueness
        /// scans do not modify the nodes they inspect.</summary>
        Guid EntityGuid { get; }

        /// <summary>Seed document components without recording author edits.</summary>
        void AdoptDocumentComponents(IReadOnlyList<PrefabComponent> components);

        /// <summary>Changes since materialization, applied over the document re-read at save.</summary>
        AuthoredEdits Edits { get; }

        /// <summary>Neutral authored values keyed &lt;componentId&gt;/&lt;path&gt;, keeping
        /// the document merge testable outside Godot.</summary>
        IReadOnlyDictionary<string, AuthoredValue> AuthoredValues();

        /// <summary>Restore the document's identity so rebuilding a node preserves references.</summary>
        /// <returns>False for Guid.Empty.</returns>
        bool RestoreEntityGuid(Guid value);

        /// <summary>Mint and persist an identity if absent, keeping re-exports stable.</summary>
        Guid EnsureEntityGuid();

        /// <summary>Baked host values keyed &lt;componentId&gt;/&lt;path&gt;. Recomputed on save
        /// because referenced objects can change without an edit to this entity.</summary>
        IReadOnlyDictionary<string, AuthoredValue> BakedHostValues(
            ParadiseGodot.Project.AssetReferenceResolver? assets = null);

    }
}
#endif

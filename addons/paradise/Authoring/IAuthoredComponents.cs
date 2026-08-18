#if TOOLS
using System.Collections.Generic;
using Paradise.Export.Data;

namespace ParadiseGodot.Authoring
{
    /// <summary>
    /// An <see cref="EntityExport"/> that carries components the ENGINE does not define: authored
    /// data declared by a game with <c>[Authored]</c>.
    ///
    /// <see cref="AuthoredComponentNode"/> implements this by interpreting the authoring schema,
    /// which is enough for every component a schema can describe. The interface exists so a game
    /// can also hand-roll a node when it needs authoring behaviour no schema expresses, and still
    /// have the exporter pick it up.
    /// </summary>
    public interface IAuthoredComponents
    {
        /// <summary>The components this node authors, ready for
        /// <see cref="EntityComponentsData.Custom"/>. An empty sequence exports nothing.</summary>
        IEnumerable<AuthoredComponentData> ExportAuthoredComponents();
    }
}
#endif

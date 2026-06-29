#nullable enable
using ParadiseExport.Core.Data;

namespace ParadiseExport.Core.Serialization
{
    /// <summary>
    /// Default <see cref="ISceneDocumentWriter"/>: serializes the scene document to JSON via
    /// <see cref="ExportJsonWriter"/> (Newtonsoft, atomic write).
    /// </summary>
    public sealed class JsonSceneDocumentWriter : ISceneDocumentWriter
    {
        public void Write(string outputPath, LevelData document)
        {
            ExportJsonWriter.WriteJsonDocument(outputPath, document);
        }
    }
}

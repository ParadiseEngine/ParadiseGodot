using System.IO;
using DotRecast.Detour.Io;
using Paradise.Sample.Game.Navigation;

namespace Paradise.Sample.Game.Navigation.Detour;

/// <summary>
/// Loads a DotRecast <c>MeshSet</c> binary (as written by ParadiseExport's
/// <c>NavMeshBinaryWriter</c> — <c>data/scenes/&lt;Scene&gt;.navmesh.bin</c>) into an
/// <see cref="INavigationMesh"/>. This is the engine-independent runtime path: Godot and the
/// ParadiseEngine runtime both load the same file with this loader — neither touches Godot's
/// NavigationRegion3D. The BankHeist <c>DetourNavMeshLoader</c> analog.
/// </summary>
public static class DetourNavMeshLoader
{
    /// <summary>Load from a file path in DotRecast MeshSet format.</summary>
    public static INavigationMesh LoadFromFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return LoadFromStream(stream);
    }

    /// <summary>Load from a byte buffer (e.g. Godot <c>FileAccess.GetFileAsBytes</c>).</summary>
    public static INavigationMesh LoadFromBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return LoadFromStream(stream);
    }

    /// <summary>Load from a stream in DotRecast MeshSet format. The caller retains stream ownership.</summary>
    public static INavigationMesh LoadFromStream(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var meshSetReader = new DtMeshSetReader();
        // Use the no-maxVertsPerPoly overload: NavMeshBinaryWriter writes the modern DotRecast format
        // (cCompatibility=false), which stores the params (incl. nvp) in the header. The
        // maxVertsPerPoly overload is the C++ Recast-demo compatibility path and misreads this format.
        return new DetourNavigationMesh(meshSetReader.Read(reader));
    }
}

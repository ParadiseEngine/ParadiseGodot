using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Paradise.Export.Data;
using Paradise.Export.Serialization.Converters;

namespace Paradise.Sample.Runtime.Tests;

/// <summary>
/// Builds v6 documents for tests that need a scene without an exporter to produce one.
/// </summary>
/// <remarks>
/// <para>
/// Replaces <c>AuthoredComponentList.Entry</c>, which v6 deleted for a good reason: it serialized
/// through the ENGINE's source-generated context, and a game's records are not in it. This writes
/// through a reflection-based serializer instead — legitimate in a test, and exactly what the
/// runtime must not do (a reflection serializer pins Godot's collectible AssemblyLoadContext and
/// breaks C# hot-reload, which is why the whole contract is source-generated).
/// </para>
/// <para>
/// The converters are the contract's own, so the shapes here are the shapes the generated READER
/// expects: multi-float leaves as arrays, enums by name.
/// </para>
/// </remarks>
internal static class AuthoredDocuments
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters =
        {
            new Vector2Converter(),
            new Vector3Converter(),
            new Vector4Converter(),
            new QuaternionConverter(),
            new Matrix4x4Converter(),
            new JsonStringEnumConverter<PhysicsBodyType>(),
            new JsonStringEnumConverter<PhysicsShapeType>(),
        },
    };

    /// <summary>One component entry, resolved by its record's <c>[Guid]</c>.</summary>
    public static AuthoredComponentData Entry<T>(T value)
        where T : class => new()
        {
            Id = typeof(T).GUID,
            Type = typeof(T).FullName,
            Data = JsonSerializer.SerializeToElement(value, Options),
        };

    /// <summary>The format's <c>meta</c>: identity, name and the parent link.</summary>
    public static AuthoredComponentData Meta(string name, Guid? guid = null, Guid? parent = null)
    {
        var payload = new Dictionary<string, object?>
        {
            [WellKnownEntityComponents.Guid] = (guid ?? System.Guid.NewGuid()).ToString("D"),
            [WellKnownEntityComponents.Name] = name,
        };
        if (parent is { } value) payload[WellKnownEntityComponents.Parent] = value.ToString("D");
        return new AuthoredComponentData
        {
            Id = WellKnownEntityComponents.MetaId,
            Type = WellKnownEntityComponents.MetaType,
            Data = JsonSerializer.SerializeToElement(payload),
        };
    }

    /// <summary>The format's <c>transform</c> — LOCAL TRS. There is no baked world matrix in a v6
    /// document; the loader composes one down the parent chain.</summary>
    public static AuthoredComponentData Transform(
        Vector3? position = null, Quaternion? rotation = null, Vector3? scale = null) =>
        new()
        {
            Id = WellKnownEntityComponents.TransformId,
            Type = WellKnownEntityComponents.TransformType,
            Data = JsonSerializer.SerializeToElement(new Dictionary<string, float[]>
            {
                [WellKnownEntityComponents.Position] = Floats(position ?? Vector3.Zero),
                [WellKnownEntityComponents.Rotation] = Floats(rotation ?? Quaternion.Identity),
                [WellKnownEntityComponents.Scale] = Floats(scale ?? Vector3.One),
            }),
        };

    /// <summary>A document holding one entity per component list, read back the way the runtime
    /// reads one — through this assembly's generated registry.</summary>
    public static AuthoredScene Scene(params List<AuthoredComponentData>[] entities)
    {
        var level = new LevelData();
        foreach (var entity in entities) level.Entities.Add(entity);
        return AuthoredScene.Read(level, AuthoredComponents.Default);
    }

    private static float[] Floats(Vector3 v) => [v.X, v.Y, v.Z];

    private static float[] Floats(Quaternion q) => [q.X, q.Y, q.Z, q.W];
}

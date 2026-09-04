using System;
using System.Collections.Generic;
using System.Numerics;
using Paradise.Authoring;
using Paradise.Export.Data;

namespace Paradise.Sample.Runtime;

/// <summary>
/// A v6 level document, read once into something the assemblers can walk.
/// </summary>
/// <remarks>
/// <para>
/// Replaces two things contract v6 removed. <c>AuthoredComponentList.Get&lt;T&gt;()</c> is gone
/// because the engine no longer has a serializer context for a game's records — a payload is
/// materialized through the GAME's generated registry now, which also means it happens once per
/// entity instead of once per lookup.
/// </para>
/// <para>
/// And the baked world matrix is gone. The document carries the format's <c>transform</c> — LOCAL
/// position, rotation and scale — plus a parent link in <c>meta</c>, so composing world space is
/// the loader's job. That is the point of the change: a reparent used to be invisible in the
/// document and is now the only thing that moves.
/// </para>
/// </remarks>
public sealed class AuthoredScene
{
    private readonly List<AuthoredEntity> _entities = [];

    private AuthoredScene() { }

    /// <summary>The document's entities, in document order — which is load-bearing: a runtime that
    /// assigns handles in walk order gets the same handle for the same object every time only
    /// because this order is a pure function of the export.</summary>
    public IReadOnlyList<AuthoredEntity> Entities => _entities;

    /// <summary>
    /// Materialize a document. <paramref name="registry"/> is the game's generated
    /// <c>AuthoredComponents.Default</c>; without it every payload is unresolved, because since v6
    /// the engine declares no components of its own to fall back on.
    /// </summary>
    /// <param name="unresolved">Collects payloads no registry could read — a component the game
    /// removed, or a document from a newer build. Null discards them.</param>
    public static AuthoredScene Read(
        LevelData level,
        IAuthoredComponentRegistry registry,
        List<AuthoredComponentData>? unresolved = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(registry);

        var scene = new AuthoredScene();
        foreach (var components in level.Entities)
        {
            scene._entities.Add(new AuthoredEntity(
                AuthoredComponentRouter.Materialize(components, registry, unresolved),
                components));
        }

        scene.ComposeWorldMatrices();
        return scene;
    }

    /// <summary>
    /// Walk each entity's parent chain and multiply. Depth-first with a visit stamp rather than
    /// recursion into a dictionary lookup per level, because a document is free to list a child
    /// before its parent — order is the EXPORT's, and nothing promises parents come first.
    /// </summary>
    private void ComposeWorldMatrices()
    {
        var byGuid = new Dictionary<Guid, AuthoredEntity>();
        foreach (var entity in _entities)
        {
            if (entity.Guid is { } guid) byGuid[guid] = entity;
        }

        foreach (var entity in _entities)
        {
            entity.World = Compose(entity, byGuid);
        }
    }

    private static Matrix4x4 Compose(AuthoredEntity entity, Dictionary<Guid, AuthoredEntity> byGuid)
    {
        var world = entity.LocalMatrix;
        var current = entity;
        // A parent chain longer than the document cannot be acyclic, so this both bounds the walk
        // and is the cycle guard: a document that parents two objects to each other stops here
        // rather than hanging the loader.
        for (int depth = 0; depth < byGuid.Count; depth++)
        {
            if (current.Parent is not { } parentGuid ||
                !byGuid.TryGetValue(parentGuid, out var parent) ||
                ReferenceEquals(parent, current))
            {
                return world;
            }

            world *= parent.LocalMatrix;
            current = parent;
        }

        return world;
    }
}

/// <summary>One entity: its materialized components, and what the format's own two say about it.</summary>
public sealed class AuthoredEntity
{
    private readonly IReadOnlyList<object> _components;

    internal AuthoredEntity(IReadOnlyList<object> components, IReadOnlyList<AuthoredComponentData> raw)
    {
        _components = components;
        foreach (var component in raw)
        {
            if (component.Id == WellKnownEntityComponents.MetaId) ReadMeta(component);
            else if (component.Id == WellKnownEntityComponents.TransformId) ReadTransform(component);
        }
    }

    /// <summary>Identity, from <c>meta</c>. Absent on an entity the host minted no id for.</summary>
    public Guid? Guid { get; private set; }

    /// <summary>Display name, from <c>meta</c>. Not unique and not identity — for diagnostics, and
    /// for the handful of places a sample scene keys behaviour off a name.</summary>
    public string? Name { get; private set; }

    /// <summary>The parent's identity, or null for a root.</summary>
    public Guid? Parent { get; private set; }

    public Vector3 LocalPosition { get; private set; }

    public Quaternion LocalRotation { get; private set; } = Quaternion.Identity;

    public Vector3 LocalScale { get; private set; } = Vector3.One;

    /// <summary>This entity's own TRS, before its parents.</summary>
    public Matrix4x4 LocalMatrix =>
        Matrix4x4.CreateScale(LocalScale) *
        Matrix4x4.CreateFromQuaternion(LocalRotation) *
        Matrix4x4.CreateTranslation(LocalPosition);

    /// <summary>The composed world matrix, System.Numerics row-vector convention — ready to use,
    /// unlike v5's contract matrix, which was column-vector and had to be transposed first.</summary>
    public Matrix4x4 World { get; internal set; } = Matrix4x4.Identity;

    /// <summary>This entity's <typeparamref name="T"/>, or null.</summary>
    public T? Get<T>() where T : class
    {
        for (int index = 0; index < _components.Count; index++)
        {
            if (_components[index] is T match) return match;
        }

        return null;
    }

    private void ReadMeta(AuthoredComponentData component)
    {
        if (component.Data.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        if (component.Data.TryGetProperty(WellKnownEntityComponents.Guid, out var guid) &&
            System.Guid.TryParse(guid.GetString(), out var parsed))
        {
            Guid = parsed;
        }

        if (component.Data.TryGetProperty(WellKnownEntityComponents.Name, out var name))
        {
            Name = name.GetString();
        }

        if (component.Data.TryGetProperty(WellKnownEntityComponents.Parent, out var parent) &&
            System.Guid.TryParse(parent.GetString(), out var parentGuid) &&
            parentGuid != System.Guid.Empty)
        {
            Parent = parentGuid;
        }
    }

    private void ReadTransform(AuthoredComponentData component)
    {
        if (component.Data.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        if (Floats(component, WellKnownEntityComponents.Position, 3) is { } p)
        {
            LocalPosition = new Vector3(p[0], p[1], p[2]);
        }

        if (Floats(component, WellKnownEntityComponents.Rotation, 4) is { } r)
        {
            LocalRotation = new Quaternion(r[0], r[1], r[2], r[3]);
        }

        if (Floats(component, WellKnownEntityComponents.Scale, 3) is { } s)
        {
            LocalScale = new Vector3(s[0], s[1], s[2]);
        }
    }

    /// <summary>A fixed-length float array, or null when the key is absent or the WRONG LENGTH.
    /// Length is checked because <c>Position = [0.0, 1.5]</c> silently baked as the origin before
    /// the document contract started refusing it, and a loader that reads it should not
    /// reintroduce that.</summary>
    private static float[]? Floats(AuthoredComponentData component, string key, int length)
    {
        if (!component.Data.TryGetProperty(key, out var array) ||
            array.ValueKind != System.Text.Json.JsonValueKind.Array ||
            array.GetArrayLength() != length)
        {
            return null;
        }

        var values = new float[length];
        int index = 0;
        foreach (var element in array.EnumerateArray()) values[index++] = element.GetSingle();
        return values;
    }
}

using System.Numerics;
using Paradise.ECS;
using Paradise.Rendering.Pbr;
using ParadiseExport.Data;
using ParadiseGame;

namespace ParadiseRuntime;

/// <summary>
/// Render half of a flipbook sprite entity in the .NET host: a thin adapter binding the
/// contract component + the sim's clock to the engine's <see cref="PbrSpriteQuad"/>. The frame
/// comes from the SAME sampling rule the sim used (<see cref="SpriteAnimationSystem.SampleFrame"/>
/// over interpolated snapshot time), so this host can never disagree with Godot's.
/// </summary>
public sealed class SpriteQuadState
{
    private readonly PbrSpriteQuad _quad;
    private readonly SpriteAnimationComponentData _sheet;

    public Entity Entity { get; }
    public PbrInstance Instance => _quad.Instance;

    public SpriteQuadState(PbrRenderer pbr, SpriteAnimationComponentData sheet, byte[]? sheetKtx2, Entity entity)
    {
        _sheet = sheet with { };
        _sheet.ValidateAndNormalize();
        Entity = entity;
        _quad = new PbrSpriteQuad(
            pbr, new FlipbookLayout(_sheet.Columns, _sheet.Rows, _sheet.FrameCount), sheetKtx2, Vector4.One);
    }

    public void Update(
        PbrRenderer pbr, in Vector3 position, in Quaternion rotation, float animationTime,
        in Vector3 cameraRight, in Vector3 cameraUp)
    {
        var frame = SpriteAnimationSystem.SampleFrame(animationTime, _sheet.Fps, _sheet.FrameCount, _sheet.Loop);
        Vector3 right;
        Vector3 up;
        if (_sheet.Billboard)
        {
            right = cameraRight;
            up = cameraUp;
        }
        else
        {
            right = Vector3.Transform(Vector3.UnitX, rotation);
            up = Vector3.Transform(Vector3.UnitY, rotation);
        }
        _quad.Update(pbr, position, right, up, _sheet.QuadSize, frame);
    }
}

/// <summary>
/// Render half of a particle emitter in the .NET host: reads the interpolated snapshot particle
/// pools and feeds the engine's <see cref="PbrSpriteBatch"/> (Sprite kind) or
/// <see cref="PbrVoxelBatch"/> (Voxel kind). The twin of the Godot bridge's MultiMesh view:
/// slot interpolation, size-over-life, and the flipbook rule are the same on both sides.
/// </summary>
public sealed class ParticleBatchState
{
    private readonly PbrSpriteBatch? _quads;
    private readonly PbrVoxelBatch? _cubes;
    private readonly SpriteInstance[] _spriteScratch;
    private readonly VoxelInstance[] _voxelScratch;
    private readonly ParticleEmitterComponentData _config;
    private readonly int _capacity;

    public Entity Entity { get; }
    public PbrInstance Instance => _quads?.Instance ?? _cubes!.Instance;

    public ParticleBatchState(
        PbrRenderer pbr, ParticleEmitterComponentData config, byte[]? sheetKtx2, Entity entity)
    {
        _config = config with { };
        _config.ValidateAndNormalize();
        Entity = entity;
        _capacity = _config.MaxParticles;
        var tint = _config.Color.ToVector4();
        if (_config.Kind == ParticleRenderKind.Sprite)
        {
            _quads = new PbrSpriteBatch(
                pbr, _capacity, new FlipbookLayout(_config.Columns, _config.Rows, _config.FrameCount),
                sheetKtx2, tint);
            _spriteScratch = new SpriteInstance[_capacity];
            _voxelScratch = [];
        }
        else
        {
            _cubes = new PbrVoxelBatch(pbr, _capacity, tint);
            _voxelScratch = new VoxelInstance[_capacity];
            _spriteScratch = [];
        }
    }

    public void Update(
        PbrRenderer pbr, in ParticleEmitter worldA, in ParticleEmitter worldB, float alpha,
        in Vector3 cameraRight, in Vector3 cameraUp)
    {
        var count = 0;
        for (var slot = 0; slot < _capacity && slot < worldB.Capacity; slot++)
        {
            var current = worldB.Particles[slot];
            if (current.Lifetime <= 0f)
            {
                continue;
            }

            // Slot reuse guard (same rule as the Godot bridge): a slot that died and respawned
            // between the snapshots (older age in the LATER world) snaps instead of sweeping.
            var previous = worldA.Particles[slot];
            Vector3 position;
            float age;
            if (previous.Lifetime > 0f && previous.Age <= current.Age)
            {
                position = Vector3.Lerp(previous.Position, current.Position, alpha);
                age = float.Lerp(previous.Age, current.Age, alpha);
            }
            else
            {
                position = current.Position;
                age = current.Age;
            }

            var life01 = Math.Clamp(age / current.Lifetime, 0f, 1f);
            var halfSize = float.Lerp(_config.StartSize, _config.EndSize, life01) * 0.5f;
            if (_quads is not null)
            {
                var frame = SpriteAnimationSystem.SampleParticleFrame(
                    age, current.Lifetime, _config.Fps, _config.FrameCount);
                _spriteScratch[count++] = new SpriteInstance(position, halfSize, frame);
            }
            else
            {
                _voxelScratch[count++] = new VoxelInstance(position, halfSize);
            }
        }

        _quads?.Update(pbr, _spriteScratch.AsSpan(0, count), cameraRight, cameraUp);
        _cubes?.Update(pbr, _voxelScratch.AsSpan(0, count));
    }
}

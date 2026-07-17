using System;
using System.Collections.Generic;
using System.Numerics;

namespace ParadiseGame;

/// <summary>One ball's recorded state at a past tick.</summary>
public readonly record struct RewoundBall(Entity Entity, Vector3 Position, Quaternion Rotation, Vector3 Velocity, float Glow, byte Sunk, float SpinY);

/// <summary>Fixed-capacity ring of per-tick dynamic-ball states for the pool game's rewind:
/// the sim records every tick (sim thread), the UI scrubs while paused (any thread), and
/// <c>SimulationRunner.RestoreFromRewind</c> rewrites the present from a chosen frame and
/// discards the recorded frames after it. 900 frames = 15 s at the fixed 60 Hz tick; a frame
/// with a dozen balls is a few hundred bytes, so the whole buffer stays far under a
/// megabyte.</summary>
internal sealed class RewindBuffer
{
    private const int Capacity = 900;

    private readonly object _lock = new();
    private readonly List<RewoundBall>[] _frames = new List<RewoundBall>[Capacity];
    private int _head;  // next write slot
    private int _count;

    public int Count
    {
        get { lock (_lock) { return _count; } }
    }

    /// <summary>Record the given balls from a freshly-ticked (not yet published) world.
    /// Entity handles are stable across the runner's CopyFrom snapshots, so the same handles
    /// restore into any later world. Sim thread only.</summary>
    public void Record(World world, IReadOnlyList<Entity> balls)
    {
        lock (_lock)
        {
            var frame = _frames[_head] ??= new List<RewoundBall>(16);
            frame.Clear();
            foreach (var entity in balls)
            {
                if (!world.IsAlive(entity)) continue;
                ref readonly var transform = ref world.GetComponent<LocalTransform>(entity);
                ref readonly var body = ref world.GetComponent<DynamicBody>(entity);
                frame.Add(new RewoundBall(
                    entity,
                    transform.Position,
                    transform.Rotation,
                    body.Velocity,
                    world.GetComponent<BallGlow>(entity).Intensity,
                    world.GetComponent<PoolBall>(entity).Sunk,
                    body.SpinY));
            }
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>Copy the frame <paramref name="framesBack"/> ticks ago (1 = latest recorded)
    /// into <paramref name="states"/>. False when out of range.</summary>
    public bool TryGet(int framesBack, List<RewoundBall> states)
    {
        lock (_lock)
        {
            if (framesBack < 1 || framesBack > _count) return false;
            var index = ((_head - framesBack) % Capacity + Capacity) % Capacity;
            states.Clear();
            states.AddRange(_frames[index]);
            return true;
        }
    }

    /// <summary>Discard the newest <paramref name="frames"/> recorded frames (after a restore,
    /// history beyond the restore point no longer describes the timeline).</summary>
    public void DropNewest(int frames)
    {
        lock (_lock)
        {
            frames = Math.Min(frames, _count);
            _head = ((_head - frames) % Capacity + Capacity) % Capacity;
            _count -= frames;
        }
    }
}

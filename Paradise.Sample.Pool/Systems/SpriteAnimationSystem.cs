using System;

namespace Paradise.Sample.Pool;

/// <summary>
/// Advances every flipbook clock one fixed tick and derives the current frame index — the sole
/// writer of <see cref="SpriteAnimation"/>. Deriving the frame HERE (not in the renderers)
/// keeps the sampling rule in one place, so the Godot host and the .NET host can never round a
/// frame differently. Looping wraps; non-looping holds the last frame forever.
/// </summary>
public ref partial struct SpriteAnimationSystem : IWorldSystem
{
    public SpriteAnimationsSegments Sprites;

    public void Execute()
    {
        for (int i = 0; i < Sprites.Length; i++)
        {
            float dt = Sprites.SimulationContext[i].DeltaSeconds;
            if (dt <= 0f)
            {
                continue;
            }

            ref SpriteAnimation sprite = ref Sprites.SpriteAnimation[i];
            sprite.Time += dt;
            sprite.Frame = SampleFrame(sprite.Time, sprite.Fps, sprite.FrameCount, sprite.Loop != 0);
        }
    }

    /// <summary>The one flipbook sampling rule (shared with tests): frame = floor(time × fps),
    /// wrapped when looping, clamped to the last frame otherwise.</summary>
    public static int SampleFrame(float time, float fps, int frameCount, bool loop)
    {
        if (frameCount <= 1)
        {
            return 0;
        }

        int raw = (int)(time * fps);
        return loop ? raw % frameCount : Math.Min(raw, frameCount - 1);
    }

    /// <summary>The particle flipbook rule, shared by BOTH render hosts: an authored fps plays
    /// the flipbook on the particle's age (looping); fps 0 stretches it once over the
    /// particle's lifetime.</summary>
    public static int SampleParticleFrame(float age, float lifetime, float fps, int frameCount)
    {
        if (fps > 0f)
        {
            return SampleFrame(age, fps, frameCount, loop: true);
        }

        float life01 = lifetime > 0f ? Math.Clamp(age / lifetime, 0f, 1f) : 0f;
        return Math.Min((int)(life01 * frameCount), frameCount - 1);
    }
}

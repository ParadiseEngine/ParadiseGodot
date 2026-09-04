using System;
using System.Numerics;

namespace Paradise.Sample.Odyssey;

/// <summary>
/// The single owner of every entity's <see cref="Position"/>/<see cref="Rotation"/> — one world system
/// (whole-query segment access, one <see cref="Execute"/> per tick) with TWO disjoint segments: the
/// piloted <see cref="Ships"/> and the orbiting <see cref="Bodies"/>. No other system writes a
/// transform (<c>[assembly: SingleWriter]</c> enforced, so a second writer is a compile error). The two
/// bodies touch DISJOINT entity sets (the ship carries Velocity/Heading; bodies carry OrbitAngle), so
/// iterating them sequentially is race-free and order-independent — the merged-system pattern from
/// immortal-cultivation's MonthlySettlementSystem.
///
/// Under snapshot-read execution its read-only fields (dt, the pilot commands, flight/orbit config) are
/// the PREVIOUS tick's values — the runner seeds/refreshes them before the schedule runs.
/// </summary>
public ref partial struct MotionSystem : IWorldSystem
{
    /// <summary>The piloted ship segment.</summary>
    public Ships.Segments Ship;

    /// <summary>The orbiting/spinning body segment (star, planets, asteroids, gate).</summary>
    public Bodies.Segments Body;

    public void Execute()
    {
        StepShips();
        StepBodies();
    }

    /// <summary>Integrate the ship from its pilot commands: turn the heading, thrust along it, apply
    /// drag, clamp speed, advance the position, keep it inside the sector cube, and face it along the
    /// heading.</summary>
    private void StepShips()
    {
        for (var i = 0; i < Ship.Length; i++)
        {
            float dt = Ship.SimulationContext[i].DeltaSeconds;
            if (dt <= 0f)
            {
                continue;
            }
            ref readonly FlightConfig cfg = ref Ship.FlightConfig[i];

            ref float heading = ref Ship.Heading[i].Value;
            // Subtract: with the chase camera behind the ship looking down its +Z, a positive yaw would
            // swing the nose toward screen-LEFT — so +turn (the D / Right key) must DECREASE the heading
            // to steer right on screen. (The rotation below uses the same heading, so the ship still
            // points exactly where it flies.)
            heading -= Ship.TurnInput[i].Value * cfg.TurnRate * dt;

            // Forward along the heading (0 = +Z). Thrust accelerates along it.
            var forward = new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading));
            ref Vector3 vel = ref Ship.Velocity[i].Value;
            vel += forward * (Ship.ThrustInput[i].Value * cfg.ThrustAccel * dt);

            // Drag (exponential decay) + speed clamp so the ship coasts to rest and never runs away.
            vel *= MathF.Max(0f, 1f - cfg.LinearDamping * dt);
            float speed = vel.Length();
            if (speed > cfg.MaxSpeed && speed > 1e-5f)
            {
                vel *= cfg.MaxSpeed / speed;
            }

            ref Vector3 pos = ref Ship.Position[i].Value;
            pos += vel * dt;

            // Soft sector walls: clamp the position and cancel the outward velocity so the ship stops
            // rather than sticking with residual speed into the wall.
            float b = cfg.SectorBounds;
            if (pos.X < -b) { pos.X = -b; if (vel.X < 0f) vel.X = 0f; }
            else if (pos.X > b) { pos.X = b; if (vel.X > 0f) vel.X = 0f; }
            if (pos.Y < -b) { pos.Y = -b; if (vel.Y < 0f) vel.Y = 0f; }
            else if (pos.Y > b) { pos.Y = b; if (vel.Y > 0f) vel.Y = 0f; }
            if (pos.Z < -b) { pos.Z = -b; if (vel.Z < 0f) vel.Z = 0f; }
            else if (pos.Z > b) { pos.Z = b; if (vel.Z > 0f) vel.Z = 0f; }

            Ship.Rotation[i].Value = Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f);
        }
    }

    /// <summary>Advance every body: revolve it about its orbit centre and spin it in place. A star or
    /// gate has orbit radius/speed 0 (stationary) but may still spin (the gate ring rotates).</summary>
    private void StepBodies()
    {
        for (var i = 0; i < Body.Length; i++)
        {
            float dt = Body.SimulationContext[i].DeltaSeconds;
            if (dt <= 0f)
            {
                continue;
            }

            ref float angle = ref Body.OrbitAngle[i].Value;
            angle += Body.OrbitSpeed[i].Value * dt;

            float r = Body.OrbitRadius[i].Value;
            Vector3 center = Body.OrbitCenter[i].Value;
            Body.Position[i].Value = center + new Vector3(MathF.Cos(angle) * r, 0f, MathF.Sin(angle) * r);

            ref float spin = ref Body.SpinPhase[i].Value;
            spin += Body.SpinSpeed[i].Value * dt;
            Body.Rotation[i].Value = Quaternion.CreateFromAxisAngle(Vector3.UnitY, spin);
        }
    }
}

namespace Paradise.Sample.Odyssey;

/// <summary>
/// Owns the warp drive's charge state (<see cref="WarpEnergy"/>) and the distance readout
/// (<see cref="DistanceLy"/>). Per tick, while the ship is alive: cruise (advance distance) and, if
/// the drive is <see cref="Charging"/>, accumulate energy up to the jump threshold. It is ALSO a
/// reader of the bus — it resets the drive's energy the frame after a successful jump (the drive
/// discharges) and zeroes energy + distance on a <see cref="NewVoyage"/> — folding those cross-cutting
/// events into the state it solely owns.
/// </summary>
public ref partial struct ChargeSystem : IWorldSystem
{
    public Chargers.Segments Ship;

    /// <summary>Last frame's deferred events (warp outcomes + new-voyage). One-frame-deferred.</summary>
    public SystemEventReader Inbox;

    public void Execute()
    {
        var warps = Inbox.Read<WarpResolved>();
        var resets = Inbox.Read<NewVoyage>();

        for (var i = 0; i < Ship.Length; i++)
        {
            ref readonly SectorLadder cfg = ref Ship.SectorLadder[i];

            if (resets.Length > 0)
            {
                Ship.WarpEnergy[i].Value = 0.0;
                Ship.DistanceLy[i].Value = 0.0;
            }

            // The drive discharges the frame a jump lands (success only — a failed jump keeps charge).
            foreach (var w in warps)
            {
                if (w.Succeeded != 0)
                {
                    Ship.WarpEnergy[i].Value = 0.0;
                }
            }

            if (Ship.Destroyed[i].Value != 0)
            {
                continue; // a breached hull drifts — no cruise, no charge
            }

            float dt = Ship.SimulationContext[i].DeltaSeconds;
            if (dt <= 0f)
            {
                continue;
            }

            Ship.DistanceLy[i].Value += cfg.CruiseSpeed * dt;

            if (Ship.Charging[i].Value != 0)
            {
                Ship.WarpEnergy[i].Value = System.Math.Min(
                    cfg.EnergyPerJump, Ship.WarpEnergy[i].Value + cfg.ChargeRate * dt);
            }
        }
    }
}

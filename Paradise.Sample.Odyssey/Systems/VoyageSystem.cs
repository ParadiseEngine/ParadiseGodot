namespace Paradise.Sample.Odyssey;

/// <summary>
/// The voyage-state owner-reactor — the sole writer of <see cref="SectorIndex"/>,
/// <see cref="HullIntegrity"/>, <see cref="Credits"/>, and <see cref="Destroyed"/>. It writes them
/// from last frame's bus events plus a per-tick hull drain, never from another entity's components:
///   • <see cref="NewVoyage"/> (managed-emitted) → reset the voyage (applied first);
///   • <see cref="WarpResolved"/> (system-appended by <see cref="WarpSystem"/>) → on success advance
///     the sector + award credits + repair hull; on failure damage the hull;
///   • per tick while alive → drain hull; a breach (≤ 0) ends the voyage (<see cref="Destroyed"/> = 1).
/// This is the reactor pattern: a jump (rolled elsewhere) changes the sector here, one frame later,
/// with no second writer.
/// </summary>
public ref partial struct VoyageSystem : IWorldSystem
{
    public Voyagers.Segments Ship;

    /// <summary>Last frame's deferred events (warp outcomes + new-voyage). One-frame-deferred.</summary>
    public SystemEventReader Inbox;

    public void Execute()
    {
        var warps = Inbox.Read<WarpResolved>();
        var resets = Inbox.Read<NewVoyage>();

        for (var i = 0; i < Ship.Length; i++)
        {
            ref readonly SectorLadder cfg = ref Ship.SectorLadder[i];

            // Reset first, so a same-frame reset+jump starts the fresh voyage cleanly.
            if (resets.Length > 0)
            {
                Ship.SectorIndex[i].Value = 0;
                Ship.HullIntegrity[i].Value = cfg.FullHull;
                Ship.Credits[i].Value = 0;
                Ship.Destroyed[i].Value = 0;
            }

            foreach (var w in warps)
            {
                if (w.Succeeded != 0)
                {
                    Ship.SectorIndex[i].Value = w.NewSector;
                    Ship.Credits[i].Value += cfg.CreditsPerJump;
                }
                Ship.HullIntegrity[i].Value = System.Math.Min(
                    cfg.FullHull, Ship.HullIntegrity[i].Value + w.HullDelta);
            }

            if (Ship.Destroyed[i].Value == 0)
            {
                float dt = Ship.SimulationContext[i].DeltaSeconds;
                if (dt > 0f)
                {
                    Ship.HullIntegrity[i].Value -= cfg.HullDrainPerSec * dt;
                }
                if (Ship.HullIntegrity[i].Value <= 0.0)
                {
                    Ship.HullIntegrity[i].Value = 0.0;
                    Ship.Destroyed[i].Value = 1;
                }
            }
        }
    }
}

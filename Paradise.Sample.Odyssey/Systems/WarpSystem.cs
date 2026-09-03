namespace Paradise.Sample.Odyssey;

/// <summary>
/// The rng-bound warp jump — the immortal-cultivation intent→system→event seam. The runner writes a
/// <see cref="WarpIntent"/> (managed) on command; this system, the sole SYSTEM writer of the intent
/// and the <see cref="RngState"/> stream, consumes it: if the drive is charged it rolls success off a
/// deterministic xorshift32 stream (the repo's established RNG pattern) against a sector-scaled chance,
/// then <c>Append</c>s a <see cref="WarpResolved"/> to the bus. It NEVER writes sector/hull/credits —
/// the owner-reactor <see cref="VoyageSystem"/> applies the outcome next frame, so the roll and its
/// cross-cutting effects stay single-writer clean.
/// </summary>
public ref partial struct WarpSystem : IWorldSystem
{
    public Warpers.Segments Ship;

    /// <summary>Appends the <see cref="WarpResolved"/> outcome for next frame's reactors.</summary>
    public SystemEventWriter Events;

    public void Execute()
    {
        for (var i = 0; i < Ship.Length; i++)
        {
            if (Ship.WarpIntent[i].Pending != 1)
            {
                continue;
            }
            Ship.WarpIntent[i].Pending = 0; // consume the intent whether or not it fires

            if (Ship.Destroyed[i].Value != 0)
            {
                continue;
            }

            ref readonly SectorLadder cfg = ref Ship.SectorLadder[i];
            if (Ship.WarpEnergy[i].Value < cfg.EnergyPerJump)
            {
                continue; // drive not charged — nothing to roll
            }

            int sector = Ship.SectorIndex[i].Value;
            float chance = System.Math.Max(
                cfg.MinJumpChance, cfg.BaseJumpChance - cfg.ChancePenaltyPerSector * sector);

            ref uint rng = ref Ship.RngState[i].Value;
            bool success = NextFloat(ref rng) < chance;

            Events.Append(new WarpResolved
            {
                Succeeded = (byte)(success ? 1 : 0),
                NewSector = success ? sector + 1 : sector,
                HullDelta = success ? cfg.HullRepairOnJump : -cfg.HullDamageOnFail,
            });
        }
    }

    // xorshift32 — same generator as Paradise.Sample.Pool's ParticleSystem; per-ship state, so the
    // stream is independent of scheduling and bit-identical across hosts.
    private static uint NextUInt(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static float NextFloat(ref uint state) => (NextUInt(ref state) >> 8) * (1f / 16777216f);
}

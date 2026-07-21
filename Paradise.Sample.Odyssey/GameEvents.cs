namespace Paradise.Sample.Odyssey;

/// <summary>
/// Deferred gameplay events on the Paradise.ECS <c>SystemEvents</c> bus (engine 0.5.2) — the same
/// fan-out channel immortal-cultivation uses. An event appended by a system (or emitted by managed
/// code) THIS frame is readable by any number of consumer systems NEXT frame (one-frame deferred,
/// deterministic merge, snapshot-carried). Events are plain unmanaged structs (NOT <c>[Component]</c>).
/// </summary>
/// <remarks>
/// Two producers, both demonstrated here:
/// <list type="bullet">
///   <item>SYSTEM producer — <see cref="WarpSystem"/> rolls the jump and <c>Append</c>s a
///     <see cref="WarpResolved"/> via an injected <c>SystemEventWriter</c> (the intent→system→event seam);</item>
///   <item>MANAGED producer — <see cref="OdysseyRunner.RequestNewVoyage"/> raises
///     <see cref="NewVoyage"/> via <c>world.Events.Emit</c> on the sim thread.</item>
/// </list>
/// Both are read next frame by the owner-reactors (<see cref="ChargeSystem"/> and
/// <see cref="VoyageSystem"/>) via an injected <c>SystemEventReader</c> (<c>Inbox.Read&lt;T&gt;()</c>),
/// which fold them into the state they each solely own — so a jump can change the sector (a
/// cross-cutting effect) without any second writer.
/// </remarks>

/// <summary>Raised by <see cref="WarpSystem"/> the tick a warp jump is rolled.
/// <see cref="VoyageSystem"/> applies it (sector + hull + credits) and <see cref="ChargeSystem"/>
/// resets the drive on success — both next frame.</summary>
public struct WarpResolved
{
    /// <summary>1 = the jump succeeded (sector advances), 0 = it failed (hull takes damage).</summary>
    public byte Succeeded;

    /// <summary>The sector the ship reaches on success (its previous sector on failure).</summary>
    public int NewSector;

    /// <summary>Signed hull change to apply: a repair (+) on success, damage (−) on failure.</summary>
    public double HullDelta;
}

/// <summary>Raised by MANAGED code (<see cref="OdysseyRunner.RequestNewVoyage"/> via
/// <c>world.Events.Emit</c>) to begin a fresh voyage. Payload-free — the request is the signal.
/// <see cref="VoyageSystem"/> and <see cref="ChargeSystem"/> reset their owned state next frame.</summary>
public struct NewVoyage
{
}

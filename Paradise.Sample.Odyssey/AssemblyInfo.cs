// Every [Component] in this assembly is single-writer: at most one system may take write access
// (ref T / Span<T>) to it, enforced at compile time by the PECS3008 analyzer. The Odyssey sim splits
// ownership cleanly — ChargeSystem owns the warp energy, WarpSystem owns the jump roll, VoyageSystem
// owns the sector/hull/credits state — so cross-cutting effects (a jump changing the sector) flow
// through the SystemEvents bus rather than a second writer. Reads via `ref readonly` / ReadOnlySpan
// stay unrestricted; managed-code writes (the runner's command flags, PrepareFrame's dt) are outside
// the system-injection model and intentionally untracked.
[assembly: Paradise.ECS.SingleWriter]

// Snapshot-read codegen: systems' read-only fields bind to the immutable CURRENT world passed to
// SystemSchedule.Run(readWorld) (the previous tick), writable fields bind to the WRITE world. With
// [SingleWriter] (disjoint writes) reads are race-free by construction, so all systems run in one
// fully parallel wave (SnapshotDagScheduler + ParallelWaveScheduler). Managed pre-pass writes to the
// write world (a command flag, the WarpIntent) are visible to systems only through WRITABLE fields.
[assembly: Paradise.ECS.SnapshotReadSystems]

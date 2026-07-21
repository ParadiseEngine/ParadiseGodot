// Every [Component] in this assembly is single-writer: at most one system may take write access
// (ref T / Span<T>) to it — the owner-system architecture (steering writes MoveIntent/NavPath,
// one system owns each piece of state), enforced at compile time by the PECS3008 analyzer.
// Reads via `ref readonly` / ReadOnlySpan stay unrestricted; managed-code writes (integrators,
// planners) are outside the system-injection model and intentionally untracked.
[assembly: Paradise.ECS.SingleWriter]

// Snapshot-read codegen: systems' read-only fields bind to the immutable CURRENT world passed to
// SystemSchedule.Run(readWorld) (the previous tick), writable fields bind to the WRITE world.
// Together with [SingleWriter] (disjoint writes) this makes reads race-free by construction, so
// the runner executes all systems in one fully parallel wave (SnapshotDagScheduler +
// ParallelWaveScheduler). Consequences: read-only views are one tick stale by design, and
// managed pre-pass writes to the write world are visible to systems only through WRITABLE
// fields — which is why SpawnBall seeds SimulationContext.DeltaSeconds.
[assembly: Paradise.ECS.SnapshotReadSystems]

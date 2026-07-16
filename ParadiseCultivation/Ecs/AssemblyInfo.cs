// Same system memory model as ParadiseGame (see its AssemblyInfo.cs for the full rationale):
// every [Component] has at most one writer system (PECS3008-enforced), and snapshot-read codegen
// binds systems' read-only fields to the immutable previous-tick world passed to
// SystemSchedule.Run(readWorld) while writable fields bind to the write world. Managed-code
// writes (the runner's command processing and monthly player settlement) are outside the
// system-injection model and intentionally untracked.
[assembly: Paradise.ECS.SingleWriter]
[assembly: Paradise.ECS.SnapshotReadSystems]

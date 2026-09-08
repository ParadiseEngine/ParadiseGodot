# ParadiseGodotEditor — agent guide

The Godot addon for Paradise Engine (`Paradise.Godot.Editor`, `ParadiseGodot.slnx`,
`project.godot`): Godot as an editor over a game's asset project, the second authoring host
beside `ParadiseBlenderEditor` for the same document contract. Empirical gotchas live in
`.claude/lessons.md`; the plan for engine catch-ups in
`paradise-workspace/GODOT-ADDON-V6-MIGRATION.md` (outside this repo).

This repo is the addon and nothing else. Its `project.godot` authors nothing; it is the smallest
consumer of the addon, kept so the payload materializer, the res:// shims and an editor reload are
exercised here. Samples, workbench scenes and a sample runtime used to live here and were removed
so an engine catch-up is one job, not three.

## Code conventions

**Code explains itself; comments explain why.** Prefer a name, a type, a small method, or a guard
over a comment that says what the code does, and restructure before commenting. A comment is for
what code cannot say: a constraint, a decision and the alternative it rejected, a failure mode
someone would reintroduce, a contract with the engine or another repo. Delete comments that
narrate control flow or restate the next line.

The Godot edge is thin on purpose: everything decidable lives in Godot-free types under
`Documents/`, `Project/` and `Play/` and is unit-tested; a `Variant` appears only in
`Authoring/`. A `Variant` cannot exist in a unit test (it segfaults the host), so anything that
must touch one is proved with a throwaway headless probe plugin, never assumed.

## Git conventions

Independent repository with its own remote. Never create a commit spanning repos. Do not commit
or push unless asked. PRs are assigned to quabug; a PR that fixes an issue carries `Closes #NNN`
(one line per issue) at the top of its body and in the commit message so merging closes it, with
`Towards #NNN` only for deliberately partial work.

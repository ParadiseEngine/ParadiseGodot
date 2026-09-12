# ParadiseGodotEditor — agent guide

This repo contains the Paradise Engine Godot addon. Like `ParadiseBlenderEditor`,
it edits a game's asset documents. The local `project.godot` is a minimal consumer
for testing installation, scripts, and editor reload; it contains no game content.

Use [known pitfalls](.claude/lessons.md) when changing CLI launch, resource loading, inspector
bindings or build paths. For engine migration work in the multi-repo workspace,
`../GODOT-ADDON-V6-MIGRATION.md` records migration history; check current engine APIs and the
game's package pin before applying its old steps.

Complete the requested behavior and relevant verification, fixing failures introduced by the
change. When the task includes checking the editor or Play flow, carry it through that check
and report any concrete blocker.

## Code conventions

Code explains what; comments explain why. Prefer clear names, types, small methods,
and guards. Keep comments for constraints, decisions, failure modes, and contracts
with the engine or other repos. Remove comments that restate the code.

Keep logic that can run without Godot in `Documents/`, `Project/`, and `Play/`.
Keep `Variant` values at the `Authoring/` boundary: constructing one in a unit test crashes
the host. Use unit tests for neutral logic and a temporary headless editor probe when a change
depends on Godot behavior that unit tests cannot exercise; see the lessons file for the probe.

## Git conventions

This is an independent repository. Keep each commit within it and use a separate worktree for
each implementation task. Commit and push only when requested or already authorized by the
task. Assign PRs to quabug. For fixes, put `Closes #NNN`
at the top of the PR body and in the commit message, one line per issue.
Use `Towards #NNN` only for partial work.

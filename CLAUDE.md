# ParadiseGodotEditor — agent guide

This repo contains the Paradise Engine Godot addon. Like `ParadiseBlenderEditor`,
it edits a game's asset documents. The local `project.godot` is a minimal consumer
for testing installation, scripts, and editor reload; it contains no game content.

See [.claude/lessons.md](.claude/lessons.md) for known pitfalls and the workspace's
`GODOT-ADDON-V6-MIGRATION.md` (outside this repo) for engine migration work.

## Code conventions

Code explains what; comments explain why. Prefer clear names, types, small methods,
and guards. Keep comments for constraints, decisions, failure modes, and contracts
with the engine or other repos. Remove comments that restate the code.

Keep logic that can run without Godot in `Documents/`, `Project/`, and `Play/`,
with unit tests. Keep `Variant` values at the `Authoring/` boundary: constructing
one in a unit test crashes the host. Verify Godot-dependent behavior with a
temporary headless editor probe.

## Git conventions

This is an independent repository. Never create commits spanning repos, and do
not commit or push unless asked. Assign PRs to quabug. For fixes, put `Closes #NNN`
at the top of the PR body and in the commit message, one line per issue.
Use `Towards #NNN` only for partial work.

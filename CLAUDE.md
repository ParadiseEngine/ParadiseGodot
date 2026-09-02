# ParadiseGodotEditor — agent guide

Godot-based editor for Paradise Engine (`ParadiseGodot.slnx`, `project.godot`), and the reference
implementation of the export contract that `ParadiseBlenderEditor` mirrors. The contract's pinned
conventions live in `CONVENTIONS.md`; migration notes in `MIGRATION.md`; empirical gotchas in
`.claude/lessons.md`.

## Code conventions

**Code explains itself; comments explain why.** Prefer a name, a type, a small method, or a guard
over a comment that says what the code does, and restructure before commenting. A comment is for
what code cannot say: a constraint, a decision and the alternative it rejected, a failure mode
someone would reintroduce, a contract with the engine or another repo. Delete comments that
narrate control flow or restate the next line. `CONVENTIONS.md` is the record of contract
decisions; the code should not repeat it.

## Git conventions

Independent repository with its own remote. Never create a commit spanning repos. Do not commit
or push unless asked.

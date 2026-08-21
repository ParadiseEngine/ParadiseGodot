#!/usr/bin/env python3
"""One-shot: rekey a .tscn's authored-component properties from NAME ids to GUIDs.

`AuthoredEntityCore` stores an entity's authored data as Godot properties named
`<component-id>/<Field/Path>` (plus `<component-id>/Enabled`). The id used to be a name --
`paradise.identity/Kind` -- and is a GUID since ParadiseEngine #151. A scene saved before that
still names the old ids, so the editor finds no values for the components the schema now
declares, and exports an entity with no Kind, no Prefab and no components AT ALL.

Nothing errors when that happens, which is the point of this script existing: the build passes,
the tests pass (they read the committed JSON, not the scene), and the loss only shows up when
someone diffs a fresh export against the previous one.

    python3 tools/migrate_scene_authoring_guids.py scenes/*.tscn

**Finished once every committed scene is converted.** Delete it then -- the table below is a
snapshot of ids that existed only before the migration.

Only the ENGINE's ten ids are here. A scene authoring a game's own component would need that
game's table too; none of the committed scenes in this repo do (checked), and the script refuses
anything it does not recognise rather than leaving half the properties rekeyed.
"""

from __future__ import annotations

import re
import sys

#: Old name -> [Guid], from Paradise.Export.Data.ParadiseComponentIds.
IDS = {
    "paradise.identity": "0c068bf4-495f-495b-be8d-9b02042a41c2",
    "paradise.renderable": "f2c0357e-94dd-4a5a-9803-518066cb54b2",
    "paradise.collider": "e1cd1bc8-86f2-4225-adc9-4a324c70ebf9",
    "paradise.rigidbody": "b7ab4dd8-c8da-4dc2-9e5e-192fd74deb11",
    "paradise.agent": "5801915b-3d0c-4940-8970-7d1487b991cf",
    "paradise.interactable": "0283ee5f-775b-412b-a91c-03ecd9b61165",
    "paradise.sprite-animation": "d3e53cd4-89c6-4ca8-851e-7596da889c68",
    "paradise.particle-emitter": "1b4d1bdd-dea1-4b86-9b6a-879c46346b9e",
    "paradise.audio-emitter": "e6ec7f42-df09-4ec9-af06-128ddf3eda8e",
    "paradise.light": "fc886b84-c48c-4415-afd9-b03d6faf5ab7",
}

#: A property name that looks like an authored component key: `<something.with-dots>/<Path>`.
KEY = re.compile(r'(?<=[\s"])([a-z][a-z0-9]*(?:\.[a-z0-9-]+)+)(?=/)')


def convert(path: str) -> int:
    with open(path, encoding="utf-8") as file:
        text = file.read()

    unknown = {m for m in KEY.findall(text) if m not in IDS}
    if unknown:
        # A game's component, or an engine id this table predates. Rekeying around it would leave
        # the scene half-migrated, which is worse than not starting.
        print(f"REFUSED: {path} authors unmapped component(s) {sorted(unknown)}", file=sys.stderr)
        return -1

    converted = KEY.sub(lambda m: IDS[m.group(1)], text)
    if converted == text:
        print(f"  {path}: nothing to do")
        return 0

    with open(path, "w", encoding="utf-8") as file:
        file.write(converted)
    moved = sum(text.count(name + "/") for name in IDS)
    print(f"  {path}: rekeyed {moved} authored propert{'y' if moved == 1 else 'ies'}")
    return moved


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print(__doc__)
        return 2
    if any(convert(path) < 0 for path in argv[1:]):
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

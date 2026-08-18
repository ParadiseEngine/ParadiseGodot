#!/usr/bin/env python3
"""One-shot migration: EntityExport nodes -> AuthoredEntityNode with authored components.

Committed for auditability rather than for reuse: 291 nodes across two scenes is far past
hand-editing, and a rewrite nobody can inspect afterwards is worse than the hand-edit.

The values come from the COMMITTED EXPORT, not from the old .tscn properties. That is the whole
trick. Reproducing the export by re-deriving it from the authored fields would mean reimplementing
every conditional the old exporter had (mass is zeroed on a static body, a rigidbody only appears
when colliders do, an agent is kinematic...). Reading the answers off the document those rules
already produced makes the migration exact by construction, and turns the acceptance test into
"re-export and diff".

Node REFERENCES cannot come from the export - a baked shape is a value, not a path - so the
collider list is carried across verbatim from the old PhysicsColliders property.

    python3 scripts/migrate_entity_export.py scenes/sample.tscn data/scenes/sample.json
"""
import json
import re
import sys

OLD_SCRIPT = "res://addons/paradise/Authoring/EntityExport.cs"
NEW_SCRIPT = "res://addons/paradise/Authoring/AuthoredEntityNode.cs"

# Everything EntityExport declared. Dropped wholesale; whatever mattered is re-emitted below.
OLD_PROPERTIES = re.compile(
    r"^(Kind|ActiveOnLoad|ModelPath|InitialAnimation|IsDynamicBody|Body[A-Za-z]+|"
    r"PhysicsColliders|InteractionColliders|Sprite[A-Za-z]*|Particle[A-Za-z]*|"
    r"IsAgent|MoveSpeed|Acceleration|IdleAnimation|WalkAnimation) = "
)

DATA_DIR = "res://data"


def gd(value):
    """A Python value as Godot .tscn syntax."""
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (int, float)):
        return repr(float(value)) if isinstance(value, float) else str(value)
    return '"%s"' % str(value).replace("\\", "\\\\").replace('"', '\\"')


def authored(entity, colliders_literal):
    """The authored properties for one exported entity, in the node's own component order."""
    out = []

    def emit(component, fields):
        out.append("%s/Enabled = true" % component)
        for name, value in fields:
            if value is not None:
                out.append("%s/%s = %s" % (component, name, gd(value)))

    components = entity.get("Components") or {}

    if agent := components.get("Agent"):
        emit("paradise.agent", [
            ("MoveSpeed", agent.get("MoveSpeed")),
            ("Acceleration", agent.get("Acceleration")),
            ("IdleClip", agent.get("IdleClip")),
            ("WalkClip", agent.get("WalkClip")),
        ])

    if components.get("Collider") is not None and colliders_literal:
        out.append("paradise.collider/Enabled = true")
        out.append("paradise.collider/Colliders = %s" % colliders_literal)

    # SpawnPhase and DisplayName are deliberately NOT authored: the exporter still supplies them,
    # and the router only overwrites what was actually set, so authoring blanks would lose them.
    emit("paradise.identity", [
        ("Kind", entity.get("Kind") or "Prop"),
        ("IsActive", bool(entity.get("IsActive", True))),
        ("InitialAnimation", entity.get("InitialAnimation")),
        ("Prefab", entity.get("Prefab")),
    ])

    if interactable := components.get("Interactable"):
        emit("paradise.interactable", [("DisplayName", interactable.get("DisplayName"))])

    if renderable := components.get("Renderable"):
        mesh = renderable.get("Mesh")
        emit("paradise.renderable", [
            # Back to the source path the author picked; the node re-derives the data-relative
            # form on export, which is what makes the round trip exact.
            ("Mesh", "%s/%s" % (DATA_DIR, mesh) if mesh else None),
            ("MeshNode", renderable.get("MeshNode")),
        ])

    if rigidbody := components.get("Rigidbody"):
        emit("paradise.rigidbody", [
            ("BodyType", rigidbody.get("BodyType")),
            ("Mass", rigidbody.get("Mass")),
            ("LinearDamping", rigidbody.get("LinearDamping")),
            ("Restitution", rigidbody.get("Restitution")),
            ("Friction", rigidbody.get("Friction")),
            ("Layer", rigidbody.get("Layer")),
            ("LayerName", rigidbody.get("LayerName")),
        ])

    return out


def migrate(scene_path, export_path):
    scene = open(scene_path).read()
    entities = {e["Id"]: e for e in json.load(open(export_path))["Entities"]}

    match = re.search(
        r'^\[ext_resource type="Script"[^\]]*path="%s" id="([^"]+)"\]$' % re.escape(OLD_SCRIPT),
        scene, re.M)
    if not match:
        print("  %s: no EntityExport script resource; nothing to do" % scene_path)
        return
    script_id = match.group(1)

    # Repoint the script, dropping the uid: it identifies the OLD file, and a stale uid resolves
    # to nothing rather than to the new script.
    scene = re.sub(
        r'^\[ext_resource type="Script"[^\]]*path="%s" id="%s"\]$' % (
            re.escape(OLD_SCRIPT), re.escape(script_id)),
        '[ext_resource type="Script" path="%s" id="%s"]' % (NEW_SCRIPT, script_id),
        scene, flags=re.M)

    blocks = re.split(r"(?=^\[node )", scene, flags=re.M)
    migrated = 0
    missing = []

    for i, block in enumerate(blocks):
        if 'script = ExtResource("%s")' % script_id not in block:
            continue
        name = re.search(r'^\[node name="([^"]+)"', block, re.M).group(1)
        entity = entities.get(name)
        if entity is None:
            missing.append(name)
            continue

        colliders = re.search(r"^PhysicsColliders = (.+)$", block, re.M)
        lines = [ln for ln in block.rstrip("\n").split("\n") if not OLD_PROPERTIES.match(ln)]

        # Authored properties go after `script`, before metadata — the order Godot writes them.
        script_at = next(i for i, ln in enumerate(lines) if ln.startswith("script = "))
        lines[script_at + 1:script_at + 1] = authored(
            entity, colliders.group(1) if colliders else None)

        blocks[i] = "\n".join(lines) + "\n\n"
        migrated += 1

    open(scene_path, "w").write("".join(blocks))
    print("  %s: migrated %d nodes" % (scene_path, migrated))
    if missing:
        # Loud: a node with no exported counterpart would silently lose every component it had.
        print("  !! no exported entity for: %s" % ", ".join(missing))


if __name__ == "__main__":
    migrate(sys.argv[1], sys.argv[2])

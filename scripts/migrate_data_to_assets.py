#!/usr/bin/env python3
"""Turn the committed data/ export into an assets/ project, once.

data/ was the OUTPUT of a Godot exporter that no longer exists: built JSON scenes, material
JSON, GLBs whose textures had been transcoded IN PLACE to KTX2 sidecars. Under engine 0.40 the
source tree is assets/ and `paradise assets build` writes what a runtime reads — and a GLB may
not reference an authored KTX2, because KTX2 is build output. So the migration goes backwards
once: KTX2 → PNG, JSON → TOML documents, data-relative paths → { guid, path } references.

Two phases, because the mesh documents a scene references are minted by the engine:

    python3 scripts/migrate_data_to_assets.py prepare   # models (+PNG), materials, settings, sidecars
    paradise assets watch --no-build --no-tray           # a few seconds: lets tooling own the sidecars
    paradise assets extract --all                        # .mesh / .material documents beside each GLB
    python3 scripts/migrate_data_to_assets.py scenes     # scenes as .prefab, referencing those
    paradise assets prefab-check --fix                    # canonical form

Deterministic: identities are uuid5 over the authoring path, so a re-run changes nothing.
"""
from __future__ import annotations

import json
import os
import struct
import subprocess
import sys
import uuid
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "data"
ASSETS = ROOT / "assets"

# One namespace for every identity this script mints, so a path always mints the same guid.
NAMESPACE = uuid.UUID("2a6f0c1e-4b7d-4e93-9c58-7d1f3a8e5b20")

META_ID = "0f1d4b3a-8c27-4a55-9b6e-2f7c1d40a913"
TRANSFORM_ID = "7e55c210-3d41-4b8a-8f26-9c0a5e71b4d2"

IMPORTERS = {".glb": "glb", ".png": "texture", ".jpg": "texture", ".material": "material", ".prefab": "prefab"}


def guid_for(authoring_path: str) -> str:
    return str(uuid.uuid5(NAMESPACE, authoring_path))


def sidecar(authoring_path: str) -> None:
    """Mint <asset>.meta beside an asset, in the shape `paradise assets watch` writes."""
    target = ASSETS / (authoring_path + ".meta")
    if target.exists():
        return
    importer = IMPORTERS[Path(authoring_path).suffix.lower()]
    target.write_text(f'schema_version = 1\nguid = "{guid_for(authoring_path)}"\nimporter = "{importer}"\n')


def read_guid(authoring_path: str) -> str:
    meta = ASSETS / (authoring_path + ".meta")
    for line in meta.read_text().splitlines():
        if line.startswith("guid"):
            return line.split('"')[1]
    raise SystemExit(f"{meta}: no guid")


# ---------------------------------------------------------------------------------------------
# TOML emission. Deliberately small: scalars, arrays of scalars, tables, arrays of tables — the
# shapes an authored document has. prefab-check --fix canonicalizes what this leaves loose.
# ---------------------------------------------------------------------------------------------

def toml_scalar(value) -> str:
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, int):
        return str(value)
    if isinstance(value, float):
        text = repr(value)
        return text if ("." in text or "e" in text) else text + ".0"
    if isinstance(value, str):
        return json.dumps(value)
    if isinstance(value, list):
        return "[" + ", ".join(toml_scalar(v) for v in value) + "]"
    if isinstance(value, Reference):
        return value.inline()
    raise TypeError(f"not a TOML scalar: {value!r}")


class Reference:
    """An asset reference, emitted as the inline table the document contract reads."""

    def __init__(self, guid: str | None, path: str | None):
        self.guid, self.path = guid, path

    def inline(self) -> str:
        if self.guid is None:
            return "{}"
        return f'{{ guid = "{self.guid}", path = "{self.path}" }}'


def is_scalar(value) -> bool:
    return isinstance(value, (bool, int, float, str, Reference)) or (
        isinstance(value, list) and all(not isinstance(v, dict) for v in value))


def emit_table(out: list[str], header: str, table: dict, array_of_tables: bool = False) -> None:
    out.append(f"[[{header}]]" if array_of_tables else f"[{header}]")
    nested = []
    for key, value in table.items():
        if value is None:
            continue
        if is_scalar(value):
            out.append(f"{key} = {toml_scalar(value)}")
        else:
            nested.append((key, value))
    out.append("")
    for key, value in nested:
        if isinstance(value, dict):
            emit_table(out, f"{header}.{key}", value)
        else:
            for element in value:
                emit_table(out, f"{header}.{key}", element, array_of_tables=True)


# ---------------------------------------------------------------------------------------------
# Phase 1: models, materials, settings
# ---------------------------------------------------------------------------------------------

def png_bytes(width: int, height: int, rgba: bytes) -> bytes:
    stride = width * 4
    raw = b"".join(b"\x00" + rgba[y * stride:(y + 1) * stride] for y in range(height))

    def chunk(kind: bytes, body: bytes) -> bytes:
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def ktx2_to_png(ktx2: Path, png: Path) -> None:
    info = subprocess.run(["ktx", "info", str(ktx2)], capture_output=True, text=True, check=True).stdout
    size = {}
    for line in info.splitlines():
        for key in ("pixelWidth", "pixelHeight"):
            if line.strip().startswith(key + ":"):
                size[key] = int(line.split(":")[1])
    raw = png.with_suffix(".raw")
    subprocess.run(
        ["ktx", "extract", "--transcode", "rgba8", "--raw", "--level", "0", str(ktx2), str(raw)],
        capture_output=True, text=True, check=True)
    try:
        png.write_bytes(png_bytes(size["pixelWidth"], size["pixelHeight"], raw.read_bytes()))
    finally:
        raw.unlink(missing_ok=True)


def read_glb(path: Path) -> tuple[dict, bytes]:
    data = path.read_bytes()
    magic, version, length = struct.unpack_from("<III", data, 0)
    assert magic == 0x46546C67, f"{path}: not a GLB"
    json_length, json_type = struct.unpack_from("<II", data, 12)
    document = json.loads(data[20:20 + json_length])
    offset = 20 + json_length
    binary = b""
    if offset < length:
        bin_length, bin_type = struct.unpack_from("<II", data, offset)
        binary = data[offset + 8:offset + 8 + bin_length]
    return document, binary


def write_glb(path: Path, document: dict, binary: bytes) -> None:
    text = json.dumps(document, separators=(",", ":")).encode()
    text += b" " * (-len(text) % 4)
    binary += b"\x00" * (-len(binary) % 4)
    body = struct.pack("<II", len(text), 0x4E4F534A) + text
    if binary:
        body += struct.pack("<II", len(binary), 0x004E4942) + binary
    path.write_bytes(struct.pack("<III", 0x46546C67, 2, 12 + len(body)) + body)


def drop_unreferenced_skins(document: dict) -> None:
    """A skin no node uses is an exporter leftover (player.glb carries one with no inverse-bind
    matrices beside the real rig), and the engine refuses a GLB with two: one rig per GLB."""
    used = sorted({node["skin"] for node in document.get("nodes", []) if "skin" in node})
    if len(document.get("skins", [])) <= len(used):
        return
    renumber = {old: new for new, old in enumerate(used)}
    document["skins"] = [document["skins"][old] for old in used]
    for node in document["nodes"]:
        if "skin" in node:
            node["skin"] = renumber[node["skin"]]
    print(f"  dropped {len(renumber) and len(document['skins'])} unreferenced skin(s)")


def migrate_glb(source: Path, target: Path) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    document, binary = read_glb(source)
    drop_unreferenced_skins(document)
    for index, image in enumerate(document.get("images", [])):
        uri = image.get("uri")
        if uri is None:
            continue
        if not uri.lower().endswith(".ktx2"):
            copy = source.parent / uri
            if copy.exists():
                (target.parent / uri).write_bytes(copy.read_bytes())
            continue
        png_name = Path(uri).with_suffix(".png").name
        ktx2_to_png(source.parent / uri, target.parent / png_name)
        image["uri"] = png_name
        image["mimeType"] = "image/png"
        print(f"  {uri} -> {png_name}")
    write_glb(target, document, binary)


def migrate_models() -> None:
    mapping = [(DATA / "Models", ASSETS / "models"), (DATA / "primitives", ASSETS / "models" / "primitives")]
    for source_dir, target_dir in mapping:
        for glb in sorted(source_dir.rglob("*.glb")):
            relative = glb.relative_to(source_dir)
            target = target_dir / relative
            print(f"{glb.relative_to(ROOT)} -> {target.relative_to(ROOT)}")
            migrate_glb(glb, target)
            sidecar(target.relative_to(ASSETS).as_posix())
        for png in sorted(source_dir.rglob("*.png")):
            target = target_dir / png.relative_to(source_dir)
            if not target.exists():
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(png.read_bytes())
            sidecar(target.relative_to(ASSETS).as_posix())
    for png in sorted((ASSETS / "models").rglob("*.png")):
        sidecar(png.relative_to(ASSETS).as_posix())


TEXTURE_KEYS = ("BaseColorTexture", "MetallicRoughnessTexture", "NormalTexture", "OcclusionTexture", "EmissiveTexture")


def texture_reference(data_relative: str | None) -> Reference:
    """A material's texture, from the data-relative path the export wrote, as an authored reference."""
    if not data_relative:
        return Reference(None, None)
    relative = data_relative.removeprefix("data/")
    for prefix, replacement in (("primitives/", "models/primitives/"), ("Models/", "models/")):
        if relative.startswith(prefix):
            relative = replacement + relative[len(prefix):]
    if relative.lower().endswith(".ktx2"):
        relative = relative[:-5] + ".png"
    if not (ASSETS / relative).exists():
        raise SystemExit(f"texture '{data_relative}' has no source under assets/ ('{relative}')")
    return Reference(read_guid(relative), relative)


def migrate_materials() -> None:
    (ASSETS / "materials").mkdir(parents=True, exist_ok=True)
    for source in sorted((DATA / "materials").glob("*.json")):
        material = json.loads(source.read_text())
        material.pop("Path", None)
        material.pop("RenderQueue", None)
        for key in TEXTURE_KEYS:
            material[key] = texture_reference(material.get(key))
        out: list[str] = []
        scalars = {k: v for k, v in material.items() if v is not None and is_scalar(v)}
        tables = {k: v for k, v in material.items() if isinstance(v, dict)}
        for key, value in scalars.items():
            out.append(f"{key} = {toml_scalar(value)}")
        out.append("")
        for key, value in tables.items():
            emit_table(out, key, value)
        target = ASSETS / "materials" / (source.stem + ".material")
        target.write_text("\n".join(out).rstrip() + "\n")
        sidecar(f"materials/{source.stem}.material")
        print(f"{source.relative_to(ROOT)} -> {target.relative_to(ROOT)}")


def migrate_settings() -> None:
    settings = json.loads((DATA / "ProjectSettings.json").read_text())
    out: list[str] = [f"SchemaVersion = {settings['SchemaVersion']}", ""]
    for key in ("Physics", "Rendering"):
        emit_table(out, key, settings[key])
    (ASSETS / "ProjectSettings.toml").write_text("\n".join(out).rstrip() + "\n")
    print("data/ProjectSettings.json -> assets/ProjectSettings.toml")


def write_manifest() -> None:
    manifest = ASSETS / "project.toml"
    if manifest.exists():
        return
    manifest.write_text('''name = "paradise-godot"
schema_version = 1

[assets]
ignore = [".DS_Store", "Thumbs.db", "desktop.ini", "*~", "*.tmp", ".#*", "*.import", "*.uid"]

[extract]
static_mesh_component = "RenderableComponentData"

[host]
project = "Paradise.Sample.Runtime/Paradise.Sample.Runtime.csproj"
scene = "scenes/sample.prefab"

[build]

[build.profiles]

[build.profiles.dev]
document_format = "toml"
texture_quality = "fast"

[build.profiles.release]
document_format = "json"
texture_quality = "full"
''')
    (ASSETS / ".gdignore").write_text("")
    print("assets/project.toml, assets/.gdignore")


# ---------------------------------------------------------------------------------------------
# Phase 2: scenes
# ---------------------------------------------------------------------------------------------

def mesh_reference(data_relative: str) -> Reference:
    """The mesh DOCUMENT the engine minted beside the GLB the export named."""
    relative = data_relative
    for prefix, replacement in (("primitives/", "models/primitives/"), ("Models/", "models/")):
        if relative.startswith(prefix):
            relative = replacement + relative[len(prefix):]
    meta = ASSETS / (relative + ".meta")
    if not meta.exists():
        raise SystemExit(f"mesh '{data_relative}': no sidecar at {meta}")
    text = meta.read_text()
    marker = "mesh = { guid = "
    if marker not in text:
        raise SystemExit(f"mesh '{data_relative}': {meta} records no extracted mesh — run `paradise assets extract --all`")
    line = text[text.index(marker):].splitlines()[0]
    guid = line.split('"')[1]
    path = line.split('path = "')[1].split('"')[0]
    return Reference(guid, path)


def material_reference(data_relative: str | None) -> Reference:
    if not data_relative:
        return Reference(None, None)
    stem = Path(data_relative).stem
    relative = f"materials/{stem}.material"
    return Reference(read_guid(relative), relative)


def convert_component(component: dict) -> dict:
    data = dict(component["Data"])
    kind = component.get("Type", "")
    if kind.endswith("RenderableComponentData") and data.get("Mesh"):
        data["Mesh"] = mesh_reference(data["Mesh"])
    if kind.endswith("MaterialsComponentData"):
        data["Slots"] = [material_reference(slot) for slot in data.get("Slots", [])]
    for key in ("Sheet",):
        if isinstance(data.get(key), str) and data[key]:
            raise SystemExit(f"{kind}: a sheet reference '{data[key]}' has no migration rule yet")
    return {"id": component["Id"], "type": kind, **data}


def migrate_scene(source: Path) -> None:
    document = json.loads(source.read_text())
    assert document.get("SchemaVersion") == 6, f"{source}: not a v6 document"
    if not document["Entities"]:
        # A code-driven sample (Odyssey) has an empty export: no document, the Godot scene's
        # `paradise_game` metadata is what launches it.
        print(f"{source.relative_to(ROOT)}: no entities — no document")
        return

    # A document places exactly ONE thing, so it has exactly one root. The v5 export was flat
    # (every entity a root with a baked world matrix), so a root is added for the scene and every
    # entity parented to it; local == world stays true because the root sits at the origin.
    root_guid = guid_for(f"scenes/{source.stem}.prefab#root")
    out: list[str] = ["schema_version = 1", "", "[[objects]]", ""]
    emit_table(out, "objects.components",
               {"id": META_ID, "type": "meta", "Guid": root_guid, "Name": source.stem}, array_of_tables=True)
    emit_table(out, "objects.components",
               {"id": TRANSFORM_ID, "type": "transform",
                "Position": [0.0, 0.0, 0.0], "Rotation": [0.0, 0.0, 0.0, 1.0], "Scale": [1.0, 1.0, 1.0]},
               array_of_tables=True)
    for entity in document["Entities"]:
        out.append("[[objects]]")
        out.append("")
        for component in entity:
            converted = convert_component(component)
            if converted["id"] == META_ID and "Parent" not in converted:
                converted["Parent"] = root_guid
            emit_table(out, "objects.components", converted, array_of_tables=True)
    target = ASSETS / "scenes" / (source.stem + ".prefab")
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text("\n".join(out).rstrip() + "\n")
    sidecar(f"scenes/{source.stem}.prefab")
    print(f"{source.relative_to(ROOT)} -> {target.relative_to(ROOT)} ({len(document['Entities'])} objects)")


def main() -> None:
    phase = sys.argv[1] if len(sys.argv) > 1 else ""
    if phase == "prepare":
        ASSETS.mkdir(exist_ok=True)
        write_manifest()
        migrate_models()
        migrate_materials()
        migrate_settings()
    elif phase == "scenes":
        for scene in sorted((DATA / "scenes").glob("*.json")):
            migrate_scene(scene)
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Merge an animation clip from a clip-only GLB into a rigged character GLB by bone name.

Mixamo-style pipelines (bank-heist) ship animations as separate GLBs whose channels target a
skeleton with the same bone NAMES as the character files. Both of this project's consumers
want the clip inside the character GLB itself — Godot's importer surfaces it on the imported
scene's AnimationPlayer (played via the regular Godot animation system), and ParadiseRuntime's
GltfAnimationRig samples it when the entity contract names it — so this bakes the channels in:
sampler keyframe data is copied into the target's binary chunk and every channel is retargeted
to the character's node of the same name. Channels whose bone has no counterpart are skipped
(reported). Empty stub animations (e.g. Unity's zero-channel "Take 001") are dropped. Re-running
replaces a same-named clip (idempotent).

Usage:
  python3 tools/merge_gltf_animation.py --clip Idle_GLB.glb --into player.glb [--name Idle]
"""
from __future__ import annotations

import argparse
import json
import struct
import sys
from pathlib import Path

COMPONENT_SIZE = {5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4}
TYPE_COUNT = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def read_glb(path: Path):
    data = path.read_bytes()
    magic, _, _ = struct.unpack_from("<III", data, 0)
    if magic != 0x46546C67:
        sys.exit(f"{path}: not a GLB")
    json_len, json_type = struct.unpack_from("<II", data, 12)
    assert json_type == 0x4E4F534A
    gltf = json.loads(data[20:20 + json_len])
    bin_off = 20 + json_len
    blob = b""
    if bin_off < len(data):
        bin_len, bin_type = struct.unpack_from("<II", data, bin_off)
        assert bin_type == 0x004E4942
        blob = data[bin_off + 8:bin_off + 8 + bin_len]
    return gltf, blob


def write_glb(path: Path, gltf, blob: bytes):
    json_bytes = json.dumps(gltf, separators=(",", ":")).encode()
    json_bytes += b" " * (-len(json_bytes) % 4)
    blob += b"\0" * (-len(blob) % 4)
    out = struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(json_bytes) + 8 + len(blob))
    out += struct.pack("<II", len(json_bytes), 0x4E4F534A) + json_bytes
    out += struct.pack("<II", len(blob), 0x004E4942) + blob
    path.write_bytes(out)


def accessor_bytes(gltf, blob: bytes, index: int) -> bytes:
    """Tightly-packed raw bytes of an accessor (handles byteStride)."""
    accessor = gltf["accessors"][index]
    view = gltf["bufferViews"][accessor["bufferView"]]
    element = COMPONENT_SIZE[accessor["componentType"]] * TYPE_COUNT[accessor["type"]]
    start = view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
    stride = view.get("byteStride", element)
    if stride == element:
        return blob[start:start + element * accessor["count"]]
    return b"".join(blob[start + i * stride:start + i * stride + element]
                    for i in range(accessor["count"]))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--clip", required=True, help="animation-only GLB (channels by bone name)")
    parser.add_argument("--into", required=True, help="character GLB to receive the clip (in place)")
    parser.add_argument("--name", default=None, help="clip name to write (default: source name)")
    args = parser.parse_args()

    clip_gltf, clip_blob = read_glb(Path(args.clip))
    if not clip_gltf.get("animations"):
        sys.exit(f"{args.clip}: no animations")
    source = clip_gltf["animations"][0]
    clip_name = args.name or source.get("name", "clip")

    target_path = Path(args.into)
    gltf, blob_in = read_glb(target_path)
    blob = bytearray(blob_in)
    node_by_name = {n.get("name"): i for i, n in enumerate(gltf["nodes"])}
    clip_names = [n.get("name") for n in clip_gltf["nodes"]]

    # Drop same-named clip (idempotent rerun) and zero-channel stubs like Unity's "Take 001".
    gltf["animations"] = [a for a in gltf.get("animations", [])
                          if a.get("name") != clip_name and a.get("channels")]

    views = gltf.setdefault("bufferViews", [])
    accessors = gltf.setdefault("accessors", [])

    def copy_accessor(index: int) -> int:
        raw = accessor_bytes(clip_gltf, clip_blob, index)
        while len(blob) % 4:
            blob.append(0)
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(raw)})
        blob.extend(raw)
        src = clip_gltf["accessors"][index]
        accessor = {"bufferView": len(views) - 1,
                    "componentType": src["componentType"],
                    "count": src["count"],
                    "type": src["type"]}
        for bound in ("min", "max"):
            if bound in src:
                accessor[bound] = src[bound]
        accessors.append(accessor)
        return len(accessors) - 1

    copied: dict[int, int] = {}  # clip accessor -> target accessor (inputs are shared)
    samplers, channels, skipped = [], [], []
    for channel in source["channels"]:
        bone = clip_names[channel["target"]["node"]]
        node_index = node_by_name.get(bone)
        if node_index is None:
            skipped.append(bone)
            continue
        src_sampler = source["samplers"][channel["sampler"]]
        sampler = {"interpolation": src_sampler.get("interpolation", "LINEAR")}
        for slot in ("input", "output"):
            key = src_sampler[slot]
            if key not in copied:
                copied[key] = copy_accessor(key)
            sampler[slot] = copied[key]
        samplers.append(sampler)
        channels.append({"sampler": len(samplers) - 1,
                         "target": {"node": node_index, "path": channel["target"]["path"]}})

    if not channels:
        sys.exit(f"{args.into}: no bones matched {args.clip}")
    gltf["animations"].append({"name": clip_name, "samplers": samplers, "channels": channels})
    gltf["buffers"][0]["byteLength"] = len(blob)
    write_glb(target_path, gltf, bytes(blob))

    print(f"{target_path}: merged '{clip_name}' — {len(channels)} channels"
          + (f", skipped unmatched bones: {sorted(set(skipped))}" if skipped else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())

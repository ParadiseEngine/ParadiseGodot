#!/usr/bin/env python3
"""Retarget a humanoid clip from a clip-only GLB onto a Rigify DEF- rigged GLB.

merge_gltf_animation.py handles same-skeleton transfer (bank-heist character rigs share the
clip's bone names). The styloo characters use Rigify DEF- bones with different names AND
different rest poses, so this does a real (if minimal) retarget:

  For every mapped bone pair, per keyframe:
    delta_world = worldRot_src(t) * conj(worldRot_src_rest)      # how the bone moved, in world
    worldRot_tgt(t) = delta_world * worldRot_tgt_rest            # same world-space motion
    localRot_tgt(t) = conj(worldRot_tgtParent(t)) * worldRot_tgt(t)

Working in world space makes the transfer independent of each rig's bone-roll/rest-pose
conventions (Mixamo T-pose vs Rigify A-pose); unmapped bones (twist .001 links, spine.005,
face) hold their rest locals and inherit motion through their parents. The hips translation
channel transfers as a rest-relative delta scaled by the rigs' hip-height ratio. Output is a
baked LINEAR clip on the source's key grid, appended to the target GLB (same-name clip
replaced, so re-running is idempotent).

Both consumers pick the clip up from the GLB: Godot's importer (native AnimationPlayer) and
Paradise.Sample.Runtime's GltfAnimationRig.

Usage:
  python3 tools/retarget_gltf_animation.py --clip Idle_GLB.glb --into data/Models/elf.glb --name Idle
"""
from __future__ import annotations

import argparse
import json
import math
import struct
import sys
from pathlib import Path

# Unity-humanoid (bank-heist clips) -> Rigify DEF (styloo characters).
# spine.005 (second neck link) and the .001 twist bones are deliberately unmapped.
HUMANOID_TO_RIGIFY = {
    "Hips": "DEF-spine",
    "Spine": "DEF-spine.001",
    "Chest": "DEF-spine.002",
    "UpperChest": "DEF-spine.003",
    "Neck": "DEF-spine.004",
    "Head": "DEF-spine.006",
}
for side, s in (("Left", "L"), ("Right", "R")):
    HUMANOID_TO_RIGIFY.update({
        f"{side}Shoulder": f"DEF-shoulder.{s}",
        f"{side}UpperArm": f"DEF-upper_arm.{s}",
        f"{side}LowerArm": f"DEF-forearm.{s}",
        f"{side}Hand": f"DEF-hand.{s}",
        f"{side}UpperLeg": f"DEF-thigh.{s}",
        f"{side}LowerLeg": f"DEF-shin.{s}",
        f"{side}Foot": f"DEF-foot.{s}",
        f"{side}Toes": f"DEF-toe.{s}",
    })
    for finger, rigify in (("Thumb", "thumb"), ("Index", "f_index"), ("Middle", "f_middle"),
                           ("Ring", "f_ring"), ("Pinky", "f_pinky")):
        for seg in (1, 2, 3):
            HUMANOID_TO_RIGIFY[f"{side}{finger}{seg}"] = f"DEF-{rigify}.0{seg}.{s}"


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
        aw * bw - ax * bx - ay * by - az * bz,
    )


def qconj(q):
    return (-q[0], -q[1], -q[2], q[3])


def qnorm(q):
    n = math.sqrt(sum(c * c for c in q)) or 1.0
    return tuple(c / n for c in q)


class Rig:
    """Node names, parents, rest TRS, and world-rotation evaluation for one glTF."""

    def __init__(self, gltf):
        self.nodes = gltf["nodes"]
        self.names = [n.get("name", "") for n in self.nodes]
        self.index = {name: i for i, name in enumerate(self.names)}
        self.parent = [-1] * len(self.nodes)
        for i, n in enumerate(self.nodes):
            for c in n.get("children", []):
                self.parent[c] = i
        self.rest_r = [tuple(n.get("rotation", (0.0, 0.0, 0.0, 1.0))) for n in self.nodes]
        self.rest_t = [tuple(n.get("translation", (0.0, 0.0, 0.0))) for n in self.nodes]
        self.rest_world_r = self._world_all(self.rest_r)

    def _world_all(self, local_r):
        world = [None] * len(self.nodes)

        def w(i):
            if world[i] is None:
                p = self.parent[i]
                world[i] = local_r[i] if p < 0 else qmul(w(p), local_r[i])
            return world[i]

        for i in range(len(self.nodes)):
            w(i)
        return world

    def world_height(self, node_index):
        y, i = 0.0, node_index
        while i >= 0:  # rest translations only — good enough for a hip-height ratio
            y += self.rest_t[i][1]
            i = self.parent[i]
        return y


def read_glb(path: Path):
    data = path.read_bytes()
    if struct.unpack_from("<I", data, 0)[0] != 0x46546C67:
        sys.exit(f"{path}: not a GLB")
    json_len = struct.unpack_from("<I", data, 12)[0]
    gltf = json.loads(data[20:20 + json_len])
    bin_off = 20 + json_len
    blob = b""
    if bin_off < len(data):
        bin_len = struct.unpack_from("<I", data, bin_off)[0]
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


def accessor_floats(gltf, blob, index):
    a = gltf["accessors"][index]
    comps = {"SCALAR": 1, "VEC3": 3, "VEC4": 4}[a["type"]]
    view = gltf["bufferViews"][a["bufferView"]]
    start = view.get("byteOffset", 0) + a.get("byteOffset", 0)
    stride = view.get("byteStride", comps * 4)
    if stride == comps * 4:
        return list(struct.unpack_from(f"<{a['count'] * comps}f", blob, start))
    out = []
    for k in range(a["count"]):
        out.extend(struct.unpack_from(f"<{comps}f", blob, start + k * stride))
    return out


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--clip", required=True)
    parser.add_argument("--into", required=True)
    parser.add_argument("--name", default=None)
    args = parser.parse_args()

    clip_gltf, clip_blob = read_glb(Path(args.clip))
    animation = clip_gltf["animations"][0]
    clip_name = args.name or animation.get("name", "clip")
    src = Rig(clip_gltf)

    target_path = Path(args.into)
    tgt_gltf, tgt_blob = read_glb(target_path)
    tgt = Rig(tgt_gltf)

    # Source channels by node: rotation values (+ shared key grid) and the hips translation.
    times = None
    rot_values: dict[int, list[float]] = {}
    hips_translation = None
    for channel in animation["channels"]:
        sampler = animation["samplers"][channel["sampler"]]
        node = channel["target"]["node"]
        channel_times = accessor_floats(clip_gltf, clip_blob, sampler["input"])
        if times is None or len(channel_times) > len(times):
            times = channel_times
        values = accessor_floats(clip_gltf, clip_blob, sampler["output"])
        if channel["target"]["path"] == "rotation":
            rot_values[node] = values
        elif channel["target"]["path"] == "translation" and src.names[node] == "Hips":
            hips_translation = values
    key_count = len(times)
    for node, values in rot_values.items():
        if len(values) != key_count * 4:
            sys.exit(f"{args.clip}: channel key grids differ; resampling not implemented")

    pairs = []  # (src node, tgt node)
    unmatched = []
    for src_name, tgt_name in HUMANOID_TO_RIGIFY.items():
        if src_name in src.index and src.index[src_name] in rot_values:
            if tgt_name in tgt.index:
                pairs.append((src.index[src_name], tgt.index[tgt_name]))
            else:
                unmatched.append(f"{src_name}->{tgt_name}")
    if not pairs:
        sys.exit(f"{target_path}: no bones matched the humanoid map")
    mapped_tgt = {t: s for s, t in pairs}

    # Bake per-key local rotations for every mapped target bone.
    baked: dict[int, list[float]] = {t: [] for _, t in pairs}
    prev: dict[int, tuple] = {}
    for k in range(key_count):
        src_local = list(src.rest_r)
        for node, values in rot_values.items():
            src_local[node] = qnorm(tuple(values[k * 4:k * 4 + 4]))
        src_world = src._world_all(src_local)

        tgt_world_cache: dict[int, tuple] = {}

        def tgt_world(i):
            if i in tgt_world_cache:
                return tgt_world_cache[i]
            if i in mapped_tgt:
                s = mapped_tgt[i]
                delta = qmul(src_world[s], qconj(src.rest_world_r[s]))
                w = qmul(delta, tgt.rest_world_r[i])
            else:
                p = tgt.parent[i]
                w = tgt.rest_r[i] if p < 0 else qmul(tgt_world(p), tgt.rest_r[i])
            tgt_world_cache[i] = w
            return w

        for _, t in pairs:
            p = tgt.parent[t]
            local = tgt_world(t) if p < 0 else qmul(qconj(tgt_world(p)), tgt_world(t))
            local = qnorm(local)
            if t in prev and sum(a * b for a, b in zip(local, prev[t])) < 0.0:
                local = tuple(-c for c in local)
            prev[t] = local
            baked[t].extend(local)

    # Hips translation: rest-relative delta scaled by hip height ratio.
    hips_baked = None
    hips_tgt = tgt.index[HUMANOID_TO_RIGIFY["Hips"]]
    if hips_translation is not None:
        hips_src = src.index["Hips"]
        scale = tgt.world_height(hips_tgt) / (src.world_height(hips_src) or 1.0)
        rest_s, rest_t = src.rest_t[hips_src], tgt.rest_t[hips_tgt]
        hips_baked = []
        for k in range(key_count):
            sx, sy, sz = hips_translation[k * 3:k * 3 + 3]
            hips_baked.extend((
                rest_t[0] + (sx - rest_s[0]) * scale,
                rest_t[1] + (sy - rest_s[1]) * scale,
                rest_t[2] + (sz - rest_s[2]) * scale,
            ))

    # Write the clip into the target GLB.
    tgt_gltf["animations"] = [a for a in tgt_gltf.get("animations", [])
                              if a.get("name") != clip_name and a.get("channels")]
    views = tgt_gltf.setdefault("bufferViews", [])
    accessors = tgt_gltf.setdefault("accessors", [])
    blob = bytearray(tgt_blob)

    def add_floats(values, kind, with_bounds=False):
        while len(blob) % 4:
            blob.append(0)
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(values) * 4})
        blob.extend(struct.pack(f"<{len(values)}f", *values))
        comps = {"SCALAR": 1, "VEC3": 3, "VEC4": 4}[kind]
        accessor = {"bufferView": len(views) - 1, "componentType": 5126,
                    "count": len(values) // comps, "type": kind}
        if with_bounds:
            accessor["min"] = [min(values)]
            accessor["max"] = [max(values)]
        accessors.append(accessor)
        return len(accessors) - 1

    input_accessor = add_floats(times, "SCALAR", with_bounds=True)
    samplers, channels = [], []

    def add_channel(node, path, values, kind):
        samplers.append({"input": input_accessor, "interpolation": "LINEAR",
                         "output": add_floats(values, kind)})
        channels.append({"sampler": len(samplers) - 1, "target": {"node": node, "path": path}})

    for _, t in pairs:
        add_channel(t, "rotation", baked[t], "VEC4")
    if hips_baked is not None:
        add_channel(hips_tgt, "translation", hips_baked, "VEC3")

    tgt_gltf["animations"].append({"name": clip_name, "samplers": samplers, "channels": channels})
    tgt_gltf["buffers"][0]["byteLength"] = len(blob)
    write_glb(target_path, tgt_gltf, bytes(blob))
    print(f"{target_path}: retargeted '{clip_name}' — {len(pairs)} bones, {key_count} keys"
          + (f"; unmatched: {unmatched}" if unmatched else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())

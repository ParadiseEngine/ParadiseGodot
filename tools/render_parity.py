#!/usr/bin/env python3
"""Render-parity gate: renders the sample scene in BOTH hosts and fails on regression.

Renders data/scenes/sample.json with the .NET runtime (ParadiseRuntime, headless
screenshot) and scenes/sample.tscn with Godot (temporary capture autoload), then
compares full-frame and per-region mean-absolute pixel differences against the
thresholds below. Exit code 0 = within thresholds, 1 = regression, 2 = setup error.

Usage:
    python3 tools/render_parity.py [--godot /path/to/Godot] [--no-build] [--out DIR]

Requirements:
  - GODOT_BIN env var or --godot pointing at a Godot 4.7 mono binary.
  - A GPU/display context: Godot's 3D renderer does not run truly headless, so CI
    needs a GPU runner (macOS runners work; the capture opens a window briefly).
  - python3 + Pillow (pip install pillow), dotnet SDK.

Methodology notes (see .claude/lessons.md "Rendering / Textures" for the full story):
  - Thresholds are mean-ABS diffs. Godot applies zero-mean dither, so ~1.7/channel
    is the irreducible floor even for a pixel-perfect renderer; thresholds carry
    ~20% headroom over the measured 2026-07-10 baseline. If a change legitimately
    improves parity, tighten the thresholds in the same PR.
  - The scene must be re-exported (PARADISE_EXPORT_SCENE) before running when the
    .tscn changed — the gate renders the COMMITTED sample.json on the .NET side.
  - Godot's import cache can serve stale GLB imports after sidecar KTX2 changes;
    if Godot's render looks stale, delete .godot/imported/<file>-<hash>.* entries.
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

try:
    from PIL import Image, ImageChops
except ImportError:  # pragma: no cover
    print("parity: Pillow is required (pip install pillow)", file=sys.stderr)
    sys.exit(2)

REPO = Path(__file__).resolve().parent.parent
SIZE = (1280, 720)

# Fixed clip time for skinned entities in BOTH captures: Godot seeks its AnimationPlayers
# here while the .NET runtime pins SkinnedMeshState via --anim-time. Mid-clip, so the gate
# compares CPU skinning (.NET) against GPU skinning (Godot) on a real posed rig.
ANIM_TIME = 0.5

# Region name -> (x0, x1, y0, y1) as frame fractions. Chosen to isolate the scene's
# distinct parity surfaces (see ParadiseEngine#91 for the history behind each).
REGIONS: dict[str, tuple[float, float, float, float]] = {
    "full-frame": (0.0, 1.0, 0.0, 1.0),
    "pure-sky": (0.02, 0.30, 0.02, 0.20),
    "dragon": (0.40, 0.62, 0.06, 0.30),
    "characters": (0.0, 1.0, 0.30, 0.62),
    "ground": (0.0, 1.0, 0.62, 1.0),
    "balls": (0.40, 0.62, 0.42, 0.58),
    "bottle-metal": (0.525, 0.60, 0.36, 0.60),
    "cube-face": (0.33, 0.40, 0.42, 0.52),
    "shadow-edge": (0.62, 0.72, 0.52, 0.62),
}

# Measured 2026-07-11 on the 33-light stress scene (full-frame 2.59) + ~20% headroom.
# The 30 extra point lights amplify the per-light metal-specular residual (Karis split-sum
# approximation vs Godot's DFG LUT) on the shiny regions. Tighten when parity improves.
THRESHOLDS: dict[str, float] = {
    "full-frame": 3.1,
    "pure-sky": 2.5,
    "dragon": 3.5,
    "characters": 3.8,
    "ground": 2.8,
    # Balls: Godot is captured PAUSED (authored pose) while the .NET runtime's sim has ticked
    # once, settling the dynamic balls a hair — a sim-vs-authored offset, not a rendering diff.
    "balls": 9.6,
    "bottle-metal": 7.2,
    "cube-face": 5.5,
    "shadow-edge": 4.0,
}

# Renders through an offscreen SubViewport at EXACTLY the target size: the OS window can be
# clamped by small virtual displays (CI runners), which changes the viewport aspect and
# therefore the camera framing. The SubViewport shares the scene's World3D (own_world_3d is
# false by default) and gets a clone of the active camera.
CAPTURE_GD = """extends Node
func _ready():
\t# Pause the tree: the gate compares RENDERING, and the game sim ticks on real-time dt,
\t# making dynamic bodies (the balls) settle nondeterministically run to run. Pausing stops
\t# the sim write-back (nodes hold their authored transforms) while rendering continues.
\tget_tree().paused = true
\tawait get_tree().create_timer(2.0).timeout
\t# Deterministic skinned pose: seek every autoplaying AnimationPlayer to the same fixed
\t# time the .NET side pins via --anim-time (the tree is paused, so it stays there).
\tfor player in get_tree().root.find_children("*", "AnimationPlayer", true, false):
\t\tif player.autoplay != "":
\t\t\tplayer.play(player.autoplay)
\t\t\tplayer.seek({anim_time}, true)
\tvar src_cam := get_viewport().get_camera_3d()
\tvar sub := SubViewport.new()
\tsub.size = Vector2i({width}, {height})
\tsub.render_target_update_mode = SubViewport.UPDATE_ALWAYS
\tadd_child(sub)
\tvar cam := Camera3D.new()
\tsub.add_child(cam)
\tcam.global_transform = src_cam.global_transform
\tcam.fov = src_cam.fov
\tcam.near = src_cam.near
\tcam.far = src_cam.far
\tcam.keep_aspect = src_cam.keep_aspect
\tcam.make_current()
\tawait RenderingServer.frame_post_draw
\tawait RenderingServer.frame_post_draw
\tsub.get_texture().get_image().save_png("{out_png}")
\tprint("[PARITY-CAPTURE] saved")
\tget_tree().quit()
"""


def run(cmd: list[str], **kwargs) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, cwd=REPO, capture_output=True, text=True, **kwargs)


def build() -> None:
    for project in ("ParadiseRuntime/ParadiseRuntime.csproj", "ParadiseGodot.csproj"):
        proc = run(["dotnet", "build", project, "-v", "q", "--nologo"])
        if proc.returncode != 0:
            print(f"parity: build failed for {project}:\n{proc.stdout}\n{proc.stderr}", file=sys.stderr)
            sys.exit(2)


def render_dotnet(out_bmp: Path) -> None:
    dlls = sorted((REPO / "ParadiseRuntime" / "bin").rglob("ParadiseRuntime.dll"))
    if not dlls:
        print("parity: ParadiseRuntime.dll not found — build first", file=sys.stderr)
        sys.exit(2)
    proc = run(["dotnet", str(dlls[-1]), "--scene", "data/scenes/sample.json",
                "--screenshot", str(out_bmp), "--anim-time", str(ANIM_TIME)])
    if proc.returncode != 0 or not out_bmp.exists():
        print(f"parity: .NET render failed:\n{proc.stdout}\n{proc.stderr}", file=sys.stderr)
        sys.exit(2)


def capture_godot(godot: str, out_png: Path) -> None:
    """Temporarily installs a capture autoload; always restores project.godot."""
    project = REPO / "project.godot"
    capture = REPO / "_parity_capture.gd"
    backup = project.read_text()
    try:
        capture.write_text(CAPTURE_GD.format(out_png=out_png, width=SIZE[0], height=SIZE[1], anim_time=ANIM_TIME))
        if "[autoload]" not in backup:
            project.write_text(backup.rstrip() + "\n\n[autoload]\n\nParityCapture=\"*res://_parity_capture.gd\"\n")
        else:
            print("parity: project.godot already has [autoload]; refusing to merge sections", file=sys.stderr)
            sys.exit(2)
        proc = run([godot, "--path", ".", "--resolution", f"{SIZE[0]}x{SIZE[1]}"], timeout=120)
        if "[PARITY-CAPTURE] saved" not in proc.stdout or not out_png.exists():
            print(f"parity: Godot capture failed:\n{proc.stdout[-2000:]}\n{proc.stderr[-2000:]}", file=sys.stderr)
            sys.exit(2)
    finally:
        project.write_text(backup)
        capture.unlink(missing_ok=True)
        (REPO / "_parity_capture.gd.uid").unlink(missing_ok=True)


def region_diff(a: Image.Image, b: Image.Image, box: tuple[float, float, float, float]) -> float:
    w, h = SIZE
    x0, x1, y0, y1 = (int(w * box[0]), int(w * box[1]), int(h * box[2]), int(h * box[3]))
    pa, pb = a.load(), b.load()
    total = count = 0
    for y in range(y0, y1, 2):
        for x in range(x0, x1, 2):
            u, v = pa[x, y], pb[x, y]
            total += abs(u[0] - v[0]) + abs(u[1] - v[1]) + abs(u[2] - v[2])
            count += 1
    return total / (count * 3) if count else 0.0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--godot", default=os.environ.get("GODOT_BIN"),
                        help="Godot binary (default: $GODOT_BIN)")
    parser.add_argument("--out", default=str(REPO / ".parity"), help="output directory")
    parser.add_argument("--no-build", action="store_true", help="skip dotnet builds")
    args = parser.parse_args()

    if not args.godot or not Path(args.godot).exists():
        print("parity: set GODOT_BIN or pass --godot", file=sys.stderr)
        return 2

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    net_bmp, god_png = out / "net.bmp", out / "godot.png"

    if not args.no_build:
        build()
    started = time.time()
    render_dotnet(net_bmp)
    capture_godot(args.godot, god_png)

    net = Image.open(net_bmp).convert("RGB").resize(SIZE)
    god = Image.open(god_png).convert("RGB").resize(SIZE)

    # Side-by-side + amplified-diff artifact for humans.
    diff = ImageChops.difference(net, god).point(lambda p: min(255, p * 8))
    combo = Image.new("RGB", (SIZE[0], SIZE[1] * 3 + 20), (20, 20, 20))
    combo.paste(net, (0, 0))
    combo.paste(god, (0, SIZE[1] + 10))
    combo.paste(diff, (0, 2 * SIZE[1] + 20))
    combo.save(out / "compare.png")

    failed = []
    print(f"\n{'region':14s} {'diff':>6s} {'limit':>6s}")
    for name, box in REGIONS.items():
        measured = region_diff(net, god, box)
        limit = THRESHOLDS[name]
        status = "ok" if measured <= limit else "FAIL"
        if measured > limit:
            failed.append(name)
        print(f"{name:14s} {measured:6.2f} {limit:6.2f}  {status}")

    print(f"\nartifacts: {out / 'compare.png'}  ({time.time() - started:.0f}s)")
    if failed:
        print(f"parity: REGRESSION in {', '.join(failed)} — see the amplified diff. "
              "If Godot's render looks stale, purge .godot/imported cache entries.",
              file=sys.stderr)
        return 1
    print("parity: within thresholds")
    return 0


if __name__ == "__main__":
    sys.exit(main())

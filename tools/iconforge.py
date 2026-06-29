#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
IconForge - a small Swiss-army knife to generate every flavour of icon from a
source image (PNG / JPEG / BMP / WEBP / GIF).

Three ways to use it
--------------------
1. Interactive menu (default). Drop the script in a folder full of images and:
       python iconforge.py
   Pick platforms with number/letter hotkeys, Enter to run, q to quit.
   Every image in the current folder is processed in batch.

2. CLI subcommands (scriptable):
       python iconforge.py windows logo.png -o build/icons
       python iconforge.py ico logo.png -o app.ico
       python iconforge.py batch *.png --sets windows,web
       python iconforge.py all logo.png

3. MCP server (so an AI agent / opencode can call it):
       python iconforge.py mcp
   Exposes tools: list_presets, generate. JSON-RPC over stdio.

Options (apply to most commands):
    -o, --out DIR      output directory (default: ./icons)
    --bg MODE          background fill: auto | trans | white | black | #RRGGBB
    --rounded PCT      round corners (0-50 percent of half-size)
    --no-pad           do not letterbox to square (crop to square instead)
    --quality S        resampling: lanczos | bicubic | nearest (default lanczos)

Requires Pillow. If missing, the script tries to install it automatically.
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import re
import sys
from typing import Dict, List, Tuple

# --------------------------------------------------------------------------- #
#  Dependency bootstrap: auto-install Pillow if it is not importable.
# --------------------------------------------------------------------------- #
try:
    from PIL import Image, ImageDraw, ImageOps  # type: ignore
except ImportError:  # pragma: no cover
    import subprocess
    print("[iconforge] Pillow not found - installing...")
    subprocess.check_call([sys.executable, "-m", "pip", "install", "Pillow"])
    from PIL import Image, ImageDraw, ImageOps  # type: ignore


IMG_EXTS = (".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif", ".tif", ".tiff")

QUALITY = {
    "lanczos": Image.Resampling.LANCZOS,
    "bicubic": Image.Resampling.BICUBIC,
    "nearest": Image.Resampling.NEAREST,
    "bilinear": Image.Resampling.BILINEAR,
}


# --------------------------------------------------------------------------- #
#  Presets - one entry per "platform / use". Each defines what to emit.
# --------------------------------------------------------------------------- #
PRESETS: Dict[str, dict] = {
    "windows": {
        "label": "Windows app (.ico + PNG suite)",
        "ico": [16, 24, 32, 48, 64, 128, 256],
        "png": [16, 24, 32, 48, 64, 128, 256],
        "bg": "trans",
    },
    "web": {
        "label": "Web (favicon, manifest, Open Graph)",
        "favicon_ico": [16, 32, 48],
        "png": [192, 512],
        "maskable": [512],
        "og": [(1200, 630)],
        "apple_touch": [180],
        "bg": "auto",
    },
    "apple": {
        "label": "Apple (ICNS + PNG)",
        "icns": True,
        "png": [16, 32, 64, 128, 256, 512, 1024],
        "apple_touch": [180],
        "bg": "trans",
    },
    "android": {
        "label": "Android (launcher + Play + maskable)",
        "named_png": {"mdpi": 48, "hdpi": 72, "xhdpi": 96,
                      "xxhdpi": 144, "xxxhdpi": 192},
        "png": [512],
        "maskable": [432],
        "bg": "trans",
    },
    "ios": {
        "label": "iOS (app icons)",
        "png": [20, 29, 40, 58, 60, 76, 80, 87, 120, 152, 167, 180, 1024],
        "bg": "trans",
    },
    "linux": {
        "label": "Linux (hicolor)",
        "png": [16, 22, 24, 32, 48, 64, 96, 128, 256],
        "bg": "trans",
    },
    "social": {
        "label": "Social / Open Graph",
        "png": [(1200, 630), (1080, 1080), (512, 512)],
        "bg": "auto",
    },
    "mono": {
        "label": "Mono tray mask (white silhouette from alpha)",
        "mono": [16, 24, 32, 48],
        "bg": "trans",
    },
}

MENU_ORDER = ["windows", "web", "apple", "android", "ios", "linux", "social", "mono"]
HOTKEYS = {"1": "windows", "2": "web", "3": "apple", "4": "android",
           "5": "ios", "6": "linux", "7": "social", "8": "mono"}


# --------------------------------------------------------------------------- #
#  Image helpers
# --------------------------------------------------------------------------- #
def open_rgba(path: str) -> Image.Image:
    img = Image.open(path)
    img = ImageOps.exif_transpose(img)
    if img.mode not in ("RGBA", "RGB", "P", "LA"):
        img = img.convert("RGBA")
    if img.mode != "RGBA":
        img = img.convert("RGBA")
    return img


def resolve_bg(img: Image.Image, mode: str) -> Tuple[int, int, int, int]:
    if mode in ("trans", "transparent"):
        return (0, 0, 0, 0)
    if mode == "white":
        return (255, 255, 255, 255)
    if mode == "black":
        return (0, 0, 0, 255)
    if mode.startswith("#") or re.fullmatch(r"[0-9a-fA-F]{6}", mode or ""):
        h = mode.lstrip("#")
        r, g, b = int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16)
        return (r, g, b, 255)
    # auto: most frequent opaque colour
    small = img.resize((64, 64))
    colors = small.getcolors(64 * 64) or []
    best = max(((c for c in colors if c[1][3] > 16)), key=lambda x: x[0], default=None)
    if best:
        return best[1]
    return (255, 255, 255, 255)


def to_square(img: Image.Image, bg, pad: bool) -> Image.Image:
    s = max(img.size)
    if pad:
        canvas = Image.new("RGBA", (s, s), bg)
        canvas.alpha_composite(img, ((s - img.width) // 2, (s - img.height) // 2))
        return canvas
    # crop to centre square
    side = min(img.size)
    left = (img.width - side) // 2
    top = (img.height - side) // 2
    return img.crop((left, top, left + side, top + side))


def round_corners(img: Image.Image, pct: float) -> Image.Image:
    if pct <= 0:
        return img
    w, h = img.size
    radius = int(min(w, h) / 2 * (pct / 100.0))
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w, h), radius=radius, fill=255)
    out = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    out.alpha_composite(img)
    out.putalpha(mask)
    return out


def resize(img: Image.Image, size, resample) -> Image.Image:
    if isinstance(size, tuple):
        tw, th = size
        # contain-fit on a transparent/coloured canvas handled by caller
        scale = min(tw / img.width, th / img.height)
        nw, nh = max(1, int(img.width * scale)), max(1, int(img.height * scale))
        small = img.resize((nw, nh), resample)
        canvas = Image.new("RGBA", (tw, th), (0, 0, 0, 0))
        canvas.alpha_composite(small, ((tw - nw) // 2, (th - nh) // 2))
        return canvas
    return img.resize((size, size), resample)


def flatten(img: Image.Image, bg) -> Image.Image:
    """Composite RGBA over bg, return RGB (for formats that dislike alpha)."""
    if img.mode != "RGBA":
        return img.convert("RGB")
    base = Image.new("RGBA", img.size, bg)
    base.alpha_composite(img)
    return base.convert("RGB")


def mono_silhouette(img: Image.Image, sizes, resample, bg) -> List[Image.Image]:
    out = []
    alpha = img.getchannel("A") if "A" in img.getbands() else None
    base_square = to_square(img, bg, True)
    for s in sizes:
        m = resize(base_square, s, resample)
        if alpha is not None:
            a = resize(to_square(Image.new("RGBA", img.size, (255, 255, 255, 255)).putalpha(alpha), bg, True), s, resample)
            white = Image.new("RGBA", (s, s), (255, 255, 255, 0))
            white.putalpha(a.getchannel("A"))
            out.append(white)
        else:
            out.append(m.convert("L").convert("RGBA"))
    return out


# --------------------------------------------------------------------------- #
#  Generation engine
# --------------------------------------------------------------------------- #
def generate(source: str, preset_names: List[str], out_dir: str,
             bg_mode: str = "trans", rounded: float = 0.0,
             pad: bool = True, quality: str = "lanczos") -> List[str]:
    """Process one source image through one or more presets. Returns file list."""
    resample = QUALITY.get(quality, Image.Resampling.LANCZOS)
    img = open_rgba(source)
    stem = os.path.splitext(os.path.basename(source))[0]
    produced: List[str] = []

    for name in preset_names:
        if name not in PRESETS:
            print(f"  ! unknown preset '{name}' - skipped", file=sys.stderr)
            continue
        preset = PRESETS[name]
        pbg = resolve_bg(img, bg_mode if bg_mode != "trans" else preset.get("bg", "trans"))
        # 'auto' should still honour an explicit user override
        if bg_mode != "trans":
            pbg = resolve_bg(img, bg_mode)
        square = to_square(img, pbg, pad)
        if rounded:
            square = round_corners(square, rounded)
        sub = os.path.join(out_dir, stem, name)
        os.makedirs(sub, exist_ok=True)

        def emit(image: Image.Image, rel: str, flatten_rgb: bool = False):
            path = os.path.join(sub, rel)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            save_img = flatten(image, pbg) if flatten_rgb else image
            save_img.save(path)
            produced.append(path)

        if preset.get("ico"):
            sizes = [(s, s) for s in preset["ico"]]
            master = resize(square, max(preset["ico"]), resample)
            master.save(os.path.join(sub, f"{stem}.ico"), format="ICO", sizes=sizes)
            produced.append(os.path.join(sub, f"{stem}.ico"))

        if preset.get("favicon_ico"):
            sizes = [(s, s) for s in preset["favicon_ico"]]
            master = resize(square, max(preset["favicon_ico"]), resample)
            master.save(os.path.join(sub, "favicon.ico"), format="ICO", sizes=sizes)
            produced.append(os.path.join(sub, "favicon.ico"))

        if preset.get("icns"):
            master = resize(square, 1024, resample)
            try:
                master.save(os.path.join(sub, f"{stem}.icns"), format="ICNS")
                produced.append(os.path.join(sub, f"{stem}.icns"))
            except Exception as e:  # ICNS needs >=512
                print(f"  ! icns failed: {e}", file=sys.stderr)

        for s in preset.get("png", []):
            emit(resize(square, s, resample), f"{stem}-{_tag(s)}.png")

        for dpi, s in (preset.get("named_png") or {}).items():
            emit(resize(square, s, resample), f"ic_launcher-{dpi}.png")

        if preset.get("maskable"):
            full = Image.new("RGBA", (1024, 1024), pbg)
            inner = resize(square, int(1024 * 0.80), resample)  # 80% safe zone
            full.alpha_composite(inner, ((1024 - inner.width) // 2,) * 2)
            for s in preset["maskable"]:
                emit(resize(full, s, resample), f"maskable-{_tag(s)}.png")

        if preset.get("og"):
            for size in preset["og"]:
                emit(resize(square, size, resample), f"og-{size[0]}x{size[1]}.png")

        if preset.get("apple_touch"):
            for s in preset["apple_touch"]:
                emit(resize(square, s, resample), f"apple-touch-icon-{s}.png",
                     flatten_rgb=True)

        if preset.get("mono"):
            for im, s in zip(mono_silhouette(img, preset["mono"], resample, pbg),
                             preset["mono"]):
                im.save(os.path.join(sub, f"mono-{s}.png"))
                produced.append(os.path.join(sub, f"mono-{s}.png"))

    return produced


def _tag(size) -> str:
    if isinstance(size, tuple):
        return f"{size[0]}x{size[1]}"
    return str(size)


def discover_sources(args_sources: List[str]) -> List[str]:
    if args_sources:
        out = []
        for s in args_sources:
            if any(ch in s for ch in "*?[]"):
                out.extend(sorted(glob.glob(s)))
            else:
                out.append(s)
        return out
    # default: every image in CWD
    files = [f for f in os.listdir(".") if f.lower().endswith(IMG_EXTS)]
    return sorted(files)


# --------------------------------------------------------------------------- #
#  Interactive menu
# --------------------------------------------------------------------------- #
ANSI = {"reset": "\033[0m", "bold": "\033[1m", "dim": "\033[2m",
        "violet": "\033[38;5;135m", "mint": "\033[38;5;80m",
        "amber": "\033[38;5;221m", "green": "\033[32m"}


def _enable_ansi():
    if os.name == "nt":
        os.system("")  # enables VT processing on Windows consoles


def render_menu(selected: set) -> str:
    lines = []
    lines.append(f"\n{ANSI['bold']}  IconForge{ANSI['reset']} "
                 f"{ANSI['dim']}- pick icon sets to generate{ANSI['reset']}\n")
    for i, key in enumerate(MENU_ORDER, 1):
        p = PRESETS[key]
        mark = f"{ANSI['green']}[x]{ANSI['reset']}" if key in selected else "[ ]"
        hk = HOTKEYS.get(str(i))
        lines.append(f"   {ANSI['bold']}{i}{ANSI['reset']} {mark}  {p['label']}")
    lines.append(f"\n   {ANSI['dim']}a select all  -  space/enter run  -  q quit"
                 f"{ANSI['reset']}")
    return "\n".join(lines)


def menu_main(args):
    _enable_ansi()
    sources = discover_sources(args.sources)
    if not sources:
        print("No images found in this folder. Drop some PNG/JPG/BMP here, "
              "or pass paths as arguments.")
        return 1

    selected: set = set()
    print(f"{ANSI['dim']}Found {len(sources)} image(s) in {os.getcwd()}{ANSI['reset']}")

    try:
        import msvcrt  # Windows: single-key
        use_getch = True
    except Exception:
        use_getch = False

    while True:
        print(render_menu(selected))
        if use_getch:
            sys.stdout.write("> "); sys.stdout.flush()
            ch = msvcrt.getwch().lower()
        else:
            ch = input("> ").strip().lower()
        if ch in ("q", "quit"):
            print("Bye.")
            return 0
        if ch == "a":
            selected = set(MENU_ORDER)
            continue
        if ch in ("", " ", "enter", "\r", "\n"):
            if not selected:
                print("Nothing selected.")
                continue
            break
        key = HOTKEYS.get(ch)
        if key:
            selected ^= {key}
        elif re.fullmatch(r"[1-8]", ch):
            selected ^= {HOTKEYS[ch]}
        else:
            print(f"Unknown choice '{ch}'.")

    print(f"\n{ANSI['mint']}Generating:{ANSI['reset']} "
          + ", ".join(sorted(selected)))
    bg = args.bg
    if bg == "trans":
        bg = "trans"  # keep per-preset default
    total = []
    for src in sources:
        print(f"\n- {src}")
        files = generate(src, sorted(selected), args.out, bg_mode=bg,
                         rounded=args.rounded, pad=args.pad, quality=args.quality)
        for f in files:
            print(f"    {f}")
        total.extend(files)
    print(f"\n{ANSI['green']}Done. {len(total)} file(s) written to "
          f"{os.path.abspath(args.out)}/{ANSI['reset']}")
    return 0


# --------------------------------------------------------------------------- #
#  MCP server (JSON-RPC over stdio) - reusable from any agent / opencode
# --------------------------------------------------------------------------- #
def mcp_main(_args):
    """Minimal MCP stdio server exposing list_presets and generate."""
    def send(obj):
        sys.stdout.write(json.dumps(obj) + "\n")
        sys.stdout.flush()

    tools = [
        {"name": "list_presets",
         "description": "List the available icon-generation presets (platforms).",
         "inputSchema": {"type": "object", "properties": {}}},
        {"name": "generate",
         "description": "Generate icons from a source image for given presets.",
         "inputSchema": {"type": "object",
                         "properties": {
                             "source": {"type": "string"},
                             "presets": {"type": "array", "items": {"type": "string"},
                                         "description": "e.g. windows,web,apple"},
                             "out_dir": {"type": "string"},
                             "bg": {"type": "string", "default": "auto"},
                             "rounded": {"type": "number", "default": 0}},
                         "required": ["source", "presets", "out_dir"]}},
    ]

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError:
            continue
        method = req.get("method")
        rid = req.get("id")
        params = req.get("params") or {}

        if method == "initialize":
            send({"jsonrpc": "2.0", "id": rid,
                  "result": {"protocolVersion": "2024-11-05",
                             "capabilities": {"tools": {}},
                             "serverInfo": {"name": "iconforge", "version": "1.0"}}})
        elif method == "notifications/initialized":
            pass
        elif method == "tools/list":
            send({"jsonrpc": "2.0", "id": rid, "result": {"tools": tools}})
        elif method == "tools/call":
            name = params.get("name")
            argsd = params.get("arguments") or {}
            try:
                if name == "list_presets":
                    out = [{"name": k, "label": PRESETS[k]["label"]} for k in MENU_ORDER]
                    text = json.dumps(out, indent=2)
                elif name == "generate":
                    files = generate(argsd["source"], argsd["presets"],
                                     argsd["out_dir"], bg_mode=argsd.get("bg", "auto"),
                                     rounded=float(argsd.get("rounded", 0)))
                    text = f"Generated {len(files)} files:\n" + "\n".join(files)
                else:
                    raise ValueError(f"unknown tool {name}")
                send({"jsonrpc": "2.0", "id": rid,
                      "result": {"content": [{"type": "text", "text": text}]}})
            except Exception as e:
                send({"jsonrpc": "2.0", "id": rid, "error": {"code": -32603,
                                                              "message": str(e)}})
        else:
            if rid is not None:
                send({"jsonrpc": "2.0", "id": rid,
                      "error": {"code": -32601, "message": "method not found"}})
    return 0


# --------------------------------------------------------------------------- #
#  CLI
# --------------------------------------------------------------------------- #
def build_parser():
    p = argparse.ArgumentParser(prog="iconforge",
                                description="Generate every icon flavour from an image.")
    p.add_argument("command", nargs="?", default="menu",
                   help="menu (default) | windows | web | apple | android | ios | "
                        "linux | social | mono | ico | favicon | icns | all | mcp")
    p.add_argument("sources", nargs="*", help="image files (default: all in CWD)")
    p.add_argument("-o", "--out", default="icons", help="output directory")
    p.add_argument("--bg", default="trans",
                   help="auto | trans | white | black | #RRGGBB")
    p.add_argument("--rounded", type=float, default=0.0,
                   help="corner radius as a percent of half-size (0-50)")
    p.add_argument("--no-pad", dest="pad", action="store_false",
                   help="crop to square instead of letterboxing")
    p.add_argument("--quality", default="lanczos",
                   choices=list(QUALITY.keys()))
    p.add_argument("--sets", default="", help="comma list for 'batch'/'all'")
    p.set_defaults(pad=True)
    return p


def main(argv=None):
    args = build_parser().parse_args(argv)
    cmd = (args.command or "menu").lower()

    if cmd == "mcp":
        return mcp_main(args)
    if cmd == "menu":
        return menu_main(args)

    # resolve which presets to run
    alias = {"windows": ["windows"], "web": ["web"], "apple": ["apple"],
             "android": ["android"], "ios": ["ios"], "linux": ["linux"],
             "social": ["social"], "mono": ["mono"],
             "ico": ["windows"], "favicon": ["web"], "icns": ["apple"]}
    if cmd == "all":
        presets = MENU_ORDER
    elif cmd == "batch":
        presets = [s.strip() for s in args.sets.split(",") if s.strip()]
        presets = [p for p in presets if p in PRESETS] or MENU_ORDER
    elif cmd in alias:
        presets = alias[cmd]
    else:
        print(f"Unknown command '{cmd}'. Run with -h for help.")
        return 2

    sources = discover_sources(args.sources)
    if not sources:
        print("No source images found.")
        return 1

    total = []
    for src in sources:
        print(f"- {src}")
        files = generate(src, presets, args.out, bg_mode=args.bg,
                         rounded=args.rounded, pad=args.pad, quality=args.quality)
        total.extend(files)
    print(f"\nDone. {len(total)} file(s) -> {os.path.abspath(args.out)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

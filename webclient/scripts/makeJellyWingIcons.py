#!/usr/bin/env python3
"""Renders the JellyWing browser and home-screen icons from their SVG sources.

Needs rsvg-convert (librsvg) on the PATH. Run from the webclient folder:

    python3 scripts/makeJellyWingIcons.py
"""
import os
import struct
import subprocess

SOURCE_DIR = os.path.join("src", "assets", "img", "jellywing")
OUTPUT_DIR = os.path.join(SOURCE_DIR, "favicons")

# Home-screen icons are opaque squares; the tab icon is the bare mark on a transparent background.
TOUCH_ICONS = {"touchicon.png": 180, "touchicon72.png": 72, "touchicon114.png": 114,
               "touchicon144.png": 144, "touchicon512.png": 512}
ICO_SIZES = [16, 32, 48]


def render(svg, size, output=None):
    command = ["rsvg-convert", "-w", str(size), "-h", str(size), "--keep-aspect-ratio",
               os.path.join(SOURCE_DIR, svg)]
    if output:
        subprocess.run(command + ["-o", output], check=True)
        return None
    return subprocess.run(command, check=True, capture_output=True).stdout


def write_ico(path, images):
    """Writes an .ico whose entries are PNG images, which every current browser reads."""
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)
    entries = b""
    for size, data in images:
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(path, "wb") as ico:
        ico.write(header + entries + b"".join(data for _, data in images))


def main():
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    for name, size in TOUCH_ICONS.items():
        render("app-icon.svg", size, os.path.join(OUTPUT_DIR, name))
    render("app-icon.svg", 192, os.path.join(OUTPUT_DIR, "favicon.png"))
    write_ico(os.path.join(OUTPUT_DIR, "favicon.ico"), [(size, render("icon.svg", size)) for size in ICO_SIZES])


if __name__ == "__main__":
    main()

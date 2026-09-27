#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

swift "$ROOT/scripts/generate-icon.swift"

ICONSET_DIR="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$ICONSET_DIR"

sips -z 16 16     "$ROOT/packaging/macos/AppIcon-1024.png" --out "$ICONSET_DIR/icon_16x16.png" >/dev/null
sips -z 32 32     "$ROOT/packaging/macos/AppIcon-1024.png" --out "$ICONSET_DIR/icon_16x16@2x.png" >/dev/null
sips -z 32 32     "$ROOT/packaging/macos/AppIcon-1024.png" --out "$ICONSET_DIR/icon_32x32.png" >/dev/null
sips -z 64 64     "$ROOT/packaging/macos/AppIcon-1024.png" --out "$ICONSET_DIR/icon_32x32@2x.png" >/dev/null
sips -z 128 128   "$ROOT/packaging/macos/AppIcon-1024.png" --out "$ICONSET_DIR/icon_128x128.png" >/dev/null
sips -z 256 256   "$ROOT/packaging/macos/AppIcon-1024.png" --out "$ICONSET_DIR/icon_128x128@2x.png" >/dev/null
sips -z 256 256   "$ROOT/packaging/macos/AppIcon-1024.png" --out "$ICONSET_DIR/icon_256x256.png" >/dev/null
sips -z 512 512   "$ROOT/packaging/macos/AppIcon-1024.png" --out "$ICONSET_DIR/icon_256x256@2x.png" >/dev/null
sips -z 512 512   "$ROOT/packaging/macos/AppIcon-1024.png" --out "$ICONSET_DIR/icon_512x512.png" >/dev/null
sips -z 1024 1024 "$ROOT/packaging/macos/AppIcon-1024.png" --out "$ICONSET_DIR/icon_512x512@2x.png" >/dev/null

iconutil -c icns "$ICONSET_DIR" -o "$ROOT/packaging/macos/AppIcon.icns"
rm -rf "$(dirname "$ICONSET_DIR")"

mkdir -p "$ROOT/src/OpcUaBrowser.App/Assets"
sips -z 512 512 "$ROOT/packaging/macos/AppIcon-1024.png" --out "$ROOT/src/OpcUaBrowser.App/Assets/AppIcon.png" >/dev/null

python3 - << 'PYEOF'
import struct
import subprocess
import os

sizes = [16, 32, 48, 64, 128, 256]
png_data = []

tmp_dir = os.path.expanduser("/tmp/ico_gen")
os.makedirs(tmp_dir, exist_ok=True)
for s in sizes:
    p = os.path.join(tmp_dir, f"{s}.png")
    subprocess.run(["sips", "-z", str(s), str(s), "packaging/macos/AppIcon-1024.png", "--out", p], check=True, stdout=subprocess.DEVNULL)
    with open(p, "rb") as f:
        png_data.append((s, f.read()))

header = struct.pack("<HHH", 0, 1, len(sizes))
entries = []
offset = 6 + len(sizes) * 16

for s, data in png_data:
    width_byte = 0 if s == 256 else s
    height_byte = 0 if s == 256 else s
    entry = struct.pack("<BBBBHHII", width_byte, height_byte, 0, 0, 1, 32, len(data), offset)
    entries.append(entry)
    offset += len(data)

with open("src/OpcUaBrowser.App/Assets/AppIcon.ico", "wb") as f:
    f.write(header)
    for e in entries:
        f.write(e)
    for _, data in png_data:
        f.write(data)
PYEOF

echo "Icons generated successfully."

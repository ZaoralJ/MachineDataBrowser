#!/usr/bin/env bash
# Builds "OPC UA Browser.app" (self-contained) and a zip per architecture.
# Usage: packaging/macos/package.sh <version> [arm64|x64 ...]
set -euo pipefail

VERSION="${1:?version required, e.g. 0.1.0}"
shift || true
ARCHS=("${@:-arm64 x64}")
[[ $# -eq 0 ]] && ARCHS=(arm64 x64)

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/artifacts/dist"
APP_NAME="OPC UA Browser"
mkdir -p "$OUT"
: > "$OUT/checksums.txt"

for arch in "${ARCHS[@]}"; do
  rid="osx-$arch"
  publish="$ROOT/artifacts/publish/$rid"
  rm -rf "$publish"
  dotnet publish "$ROOT/src/OpcUaBrowser.App/OpcUaBrowser.App.csproj" \
    -c Release -r "$rid" --self-contained true \
    -p:Version="$VERSION" -p:PublishSingleFile=false -p:UseAppHost=true \
    -o "$publish"

  bundle="$OUT/$rid/$APP_NAME.app"
  rm -rf "$OUT/$rid"
  mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
  cp -R "$publish/." "$bundle/Contents/MacOS/"
  sed "s/__VERSION__/$VERSION/g" "$ROOT/packaging/macos/Info.plist" > "$bundle/Contents/Info.plist"
  chmod +x "$bundle/Contents/MacOS/OpcUaBrowser"
  cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES.md" "$bundle/Contents/Resources/"
  cp "$ROOT/packaging/macos/AppIcon.icns" "$bundle/Contents/Resources/"

  # Ad-hoc signature: required for arm64 binaries to launch; not a Developer ID signature.
  codesign --force --deep --sign - "$bundle"

  zip="$OUT/OpcUaBrowser-$VERSION-$rid.zip"
  rm -f "$zip"
  (cd "$OUT/$rid" && ditto -c -k --keepParent "$APP_NAME.app" "$zip")
  (cd "$OUT" && shasum -a 256 "$(basename "$zip")" | tee -a "$OUT/checksums.txt")
done

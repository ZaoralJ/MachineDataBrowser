#!/usr/bin/env bash
# Builds "Machine Data Browser.app" (self-contained) and a zip per architecture.
# Usage: packaging/macos/package.sh <version> [arm64|x64 ...]
set -euo pipefail

VERSION="${1:?version required, e.g. 0.1.0}"
shift || true
ARCHS=("${@:-arm64 x64}")
[[ $# -eq 0 ]] && ARCHS=(arm64 x64)

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/artifacts/dist"
APP_NAME="Machine Data Browser"
mkdir -p "$OUT"
: > "$OUT/checksums.txt"

for arch in "${ARCHS[@]}"; do
  rid="osx-$arch"
  publish="$ROOT/artifacts/publish/$rid"
  rm -rf "$publish"
  dotnet publish "$ROOT/src/MachineDataBrowser.App/MachineDataBrowser.App.csproj" \
    -c Release -r "$rid" --self-contained true \
    -p:Version="$VERSION" -p:PublishSingleFile=false -p:UseAppHost=true \
    -o "$publish"

  bundle="$OUT/$rid/$APP_NAME.app"
  rm -rf "$OUT/$rid"
  mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
  cp -R "$publish/." "$bundle/Contents/MacOS/"
  sed "s/__VERSION__/$VERSION/g" "$ROOT/packaging/macos/Info.plist" > "$bundle/Contents/Info.plist"
  chmod +x "$bundle/Contents/MacOS/MachineDataBrowser"
  cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES.md" "$bundle/Contents/Resources/"
  cp "$ROOT/packaging/macos/AppIcon.icns" "$bundle/Contents/Resources/"

  # Developer ID + hardened runtime when SIGN_IDENTITY is set, otherwise ad-hoc (arm64 needs a signature to launch).
  "$ROOT/packaging/macos/sign.sh" "$bundle"

  zip="$OUT/MachineDataBrowser-$VERSION-$rid.zip"
  rm -f "$zip"
  (cd "$OUT/$rid" && ditto -c -k --keepParent "$APP_NAME.app" "$zip")

  if [[ "${SIGN_IDENTITY:--}" != "-" && ( -n "${NOTARY_PROFILE:-}" || -n "${NOTARY_KEY_PATH:-}" ) ]]; then
    # Notarize, then staple the ticket into the app so Gatekeeper accepts it offline, and zip the stapled app.
    "$ROOT/packaging/macos/notarize.sh" "$zip"
    xcrun stapler staple "$bundle"
    rm -f "$zip"
    (cd "$OUT/$rid" && ditto -c -k --keepParent "$APP_NAME.app" "$zip")
  fi

  (cd "$OUT" && shasum -a 256 "$(basename "$zip")" | tee -a "$OUT/checksums.txt")
done

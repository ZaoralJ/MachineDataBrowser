#!/usr/bin/env bash
# Builds the mdbrowser CLI as a self-contained single file per runtime and packs each into a tar.gz.
# Usage: packaging/cli/package.sh <version> [osx-arm64 osx-x64 linux-x64 linux-arm64 ...]
set -euo pipefail

VERSION="${1:?version required, e.g. 0.1.0}"
shift || true
RIDS=("$@")
[[ $# -eq 0 ]] && RIDS=(osx-arm64 osx-x64 linux-x64 linux-arm64)

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/artifacts/dist"
mkdir -p "$OUT"
touch "$OUT/checksums.txt"

for rid in "${RIDS[@]}"; do
  publish="$ROOT/artifacts/publish/cli-$rid"
  rm -rf "$publish"
  # libplctag (EtherNet/IP) ships a native library: bundle it into the single file, extracted on first run.
  dotnet publish "$ROOT/src/MachineDataBrowser.Cli/MachineDataBrowser.Cli.csproj" \
    -c Release -r "$rid" --self-contained true \
    -p:Version="$VERSION" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none -o "$publish"
  cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES.md" "$publish/"

  if [[ "$rid" == osx-* ]] && command -v codesign >/dev/null; then
    # Developer ID + hardened runtime when SIGN_IDENTITY is set, otherwise ad-hoc (arm64 needs a signature to run).
    "$ROOT/packaging/macos/sign.sh" "$publish/mdbrowser"
    if [[ "${SIGN_IDENTITY:--}" != "-" && ( -n "${NOTARY_PROFILE:-}" || -n "${NOTARY_KEY_PATH:-}" ) ]]; then
      # A bare binary can't carry a stapled ticket; Gatekeeper looks the notarization up online on first run.
      notary_zip="$ROOT/artifacts/publish/mdbrowser-$rid-notarize.zip"
      rm -f "$notary_zip"
      ditto -c -k "$publish/mdbrowser" "$notary_zip"
      "$ROOT/packaging/macos/notarize.sh" "$notary_zip"
      rm -f "$notary_zip"
    fi
  fi

  archive="$OUT/mdbrowser-$VERSION-$rid.tar.gz"
  tar -czf "$archive" -C "$publish" mdbrowser LICENSE THIRD-PARTY-NOTICES.md
  (cd "$OUT" && shasum -a 256 "$(basename "$archive")" | tee -a "$OUT/checksums.txt")
done

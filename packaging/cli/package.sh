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
    # Ad-hoc signature: required for arm64 binaries to run; not a Developer ID signature.
    codesign --force --sign - "$publish/mdbrowser"
  fi

  archive="$OUT/mdbrowser-$VERSION-$rid.tar.gz"
  tar -czf "$archive" -C "$publish" mdbrowser LICENSE THIRD-PARTY-NOTICES.md
  (cd "$OUT" && shasum -a 256 "$(basename "$archive")" | tee -a "$OUT/checksums.txt")
done

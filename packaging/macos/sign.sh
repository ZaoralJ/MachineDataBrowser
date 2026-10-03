#!/usr/bin/env bash
# Signs an .app bundle or a single binary for distribution.
# Usage: packaging/macos/sign.sh <path.app | binary>
# SIGN_IDENTITY: a "Developer ID Application: …" identity in the keychain -> hardened runtime + secure timestamp,
#                as notarization requires. Unset or "-": ad-hoc signature (local and fork builds; arm64 needs one).
set -euo pipefail

TARGET="${1:?path to .app or binary required}"
IDENTITY="${SIGN_IDENTITY:--}"
# Hardened runtime exceptions .NET needs: the JIT writes and runs machine code, and the bundled native libraries
# (libplctag, SkiaSharp, HarfBuzz) are not signed by our team. (Kept out of the plist: codesign rejects comments there.)
ENTITLEMENTS="$(cd "$(dirname "$0")" && pwd)/entitlements.plist"

sign() {
  if [[ "$IDENTITY" == "-" ]]; then
    codesign --force --sign - "$1" 2>&1 | { grep -v "replacing existing signature" || true; }
  else
    codesign --force --sign "$IDENTITY" --options runtime --timestamp --entitlements "$ENTITLEMENTS" "$1" 2>&1 | { grep -v "replacing existing signature" || true; }
  fi
}

if [[ -d "$TARGET" ]]; then
  # Inside out: every file in Contents/MacOS first, then the bundle. .NET keeps its managed .dll files next to the
  # app host, and Apple treats everything there as code that must be signed (non-Mach-O files get their signature in
  # extended attributes, which ditto keeps when zipping).
  # The main executable is signed with the bundle: signing it alone already checks that everything else is signed.
  main="$TARGET/Contents/MacOS/$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$TARGET/Contents/Info.plist")"
  while IFS= read -r -d '' file; do
    [[ "$file" == "$main" ]] || sign "$file"
  done < <(find "$TARGET/Contents/MacOS" -type f -print0)
fi

sign "$TARGET"
codesign --verify --strict --verbose=1 "$TARGET"

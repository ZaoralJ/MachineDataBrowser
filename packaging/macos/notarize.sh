#!/usr/bin/env bash
# Submits a zip to Apple's notary service and waits for the result.
# Usage: packaging/macos/notarize.sh <file.zip>
# Credentials, one of:
#   NOTARY_PROFILE                                   a keychain profile (xcrun notarytool store-credentials)
#   NOTARY_KEY_PATH, NOTARY_KEY_ID, NOTARY_ISSUER    an App Store Connect API key (CI)
set -euo pipefail

ZIP="${1:?zip to notarize required}"

if [[ -n "${NOTARY_PROFILE:-}" ]]; then
  auth=(--keychain-profile "$NOTARY_PROFILE")
else
  auth=(--key "${NOTARY_KEY_PATH:?}" --key-id "${NOTARY_KEY_ID:?}" --issuer "${NOTARY_ISSUER:?}")
fi

result=$(xcrun notarytool submit "$ZIP" "${auth[@]}" --wait --output-format json)
id=$(echo "$result" | plutil -extract id raw -o - - 2>/dev/null || echo "$result" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')
status=$(echo "$result" | plutil -extract status raw -o - - 2>/dev/null || echo "$result" | sed -n 's/.*"status":"\([^"]*\)".*/\1/p')
echo "Notarization of $(basename "$ZIP"): $status ($id)"

if [[ "$status" != "Accepted" ]]; then
  # Apple's log names every file it rejected and why.
  xcrun notarytool log "$id" "${auth[@]}"
  exit 1
fi

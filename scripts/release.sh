#!/usr/bin/env bash
# Tag and publish a release: scripts/release.sh 0.2.0   (pre-release: 0.2.0-rc.1)
# Runs the tests locally, tags main and pushes the tag; the release workflow builds and publishes.
set -euo pipefail

VERSION="${1:?usage: scripts/release.sh X.Y.Z[-pre]}"
TAG="v$VERSION"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]] || { echo "Version must be X.Y.Z[-pre]"; exit 1; }

cd "$(dirname "$0")/.."
[[ "$(git branch --show-current)" == "main" ]] || { echo "Switch to main first"; exit 1; }
[[ -z "$(git status --porcelain)" ]] || { echo "Working tree is not clean"; exit 1; }
git fetch -q origin main --tags
[[ "$(git rev-parse HEAD)" == "$(git rev-parse origin/main)" ]] || { echo "main is not in sync with origin/main"; exit 1; }
git rev-parse -q --verify "refs/tags/$TAG" >/dev/null && { echo "$TAG already exists"; exit 1; }

artifacts="$(mktemp -d)"
dotnet build -c Release --artifacts-path "$artifacts"
dotnet test -c Release --no-build --artifacts-path "$artifacts"

git tag -a "$TAG" -m "OPC UA Browser $VERSION"
git push origin "$TAG"
echo "Pushed $TAG. Follow the build: gh run watch \$(gh run list --workflow release.yml --limit 1 --json databaseId --jq '.[0].databaseId')"

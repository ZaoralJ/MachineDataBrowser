# OpcUaBrowser

[![ci](https://github.com/ZaoralJ/OpcUaBrowser/actions/workflows/ci.yml/badge.svg)](https://github.com/ZaoralJ/OpcUaBrowser/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Cross-platform OPC UA client to browse and monitor servers (.NET 10, Avalonia, OPC Foundation UA-.NETStandard).

## Install (macOS, Homebrew)

```sh
brew install --cask zaoralj/tap/opcua-browser
```

The app is self-contained (ships its own .NET runtime). It is not notarized; if macOS blocks the first launch:

```sh
xattr -dr com.apple.quarantine "/Applications/OPC UA Browser.app"
```

Update with `brew upgrade --cask opcua-browser`, remove with `brew uninstall --cask --zap opcua-browser`.

## Build from source

```sh
dotnet run --project src/OpcUaBrowser.App
```

## Release

Releases are driven by [release-please](https://github.com/googleapis/release-please). Every merge to `main`
updates a release PR that bumps the version (from the Conventional Commit PR titles) and `CHANGELOG.md`.
Merging that PR tags `vX.Y.Z`, creates the GitHub release and runs the `release` workflow, which tests again,
builds self-contained `osx-arm64`/`osx-x64` app bundles, attaches them and updates the Homebrew cask in
`ZaoralJ/homebrew-tap`.

Pre-releases are tagged by hand and skip the tap:

```sh
scripts/release.sh 0.2.0-rc.1
```

Local package only: `packaging/macos/package.sh 0.2.0 arm64` → `artifacts/dist/`.

## Documentation

- [Architecture](docs/architecture.md) – projects, Core/App design, persistence, packaging
- [Decisions](docs/decisions.md) – why this SDK, UI framework, docking and distribution

## Contributing

Contributions are welcome – see [CONTRIBUTING.md](CONTRIBUTING.md). Licensed under the [MIT License](LICENSE);
third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

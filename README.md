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

1. `git tag v0.1.0 && git push --tags` — the `release` workflow builds `osx-arm64` and `osx-x64` zips
   (`packaging/macos/package.sh`) and attaches a filled-in cask `opcua-browser.rb` to the GitHub release.
2. With the repo secret `TAP_TOKEN` (a token with write access to `ZaoralJ/homebrew-tap`) the workflow pushes the cask to the tap;
   otherwise copy `opcua-browser.rb` from the release into `Casks/` of the tap.

Local package: `packaging/macos/package.sh 0.1.0 arm64` → `artifacts/dist/`.

## Documentation

- [Architecture](docs/architecture.md) – projects, Core/App design, persistence, packaging
- [Decisions](docs/decisions.md) – why this SDK, UI framework, docking and distribution

## Contributing

Contributions are welcome – see [CONTRIBUTING.md](CONTRIBUTING.md). Licensed under the [MIT License](LICENSE);
third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

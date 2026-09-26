# Contributing to OpcUaBrowser

Thanks for helping! Bug reports, ideas and pull requests are welcome.

## Ground rules

- Be kind and constructive – see the [Code of Conduct](CODE_OF_CONDUCT.md).
- Security problems: do **not** open a public issue, follow [SECURITY.md](SECURITY.md).
- By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).

## Workflow

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. **Fork** the repository and create a branch in your fork (`fix/...`, `feat/...`).
   Only maintainers can push branches to this repository; `main` is protected and changes land through
   reviewed pull requests (squash merge).
3. Keep the pull request focused on one change and fill in the PR template.
4. CI must be green: build with **zero warnings** (warnings are errors) and all tests passing.
5. A maintainer reviews and merges. CI for PRs from forks starts after a maintainer approves the run.

## Development setup

Requirements: [.NET SDK 10](https://dotnet.microsoft.com/download) (see `global.json`) and Docker
(integration tests start an [opc-plc](https://github.com/Azure-Samples/iot-edge-opc-plc) container).

```sh
dotnet build                     # warnings are errors
dotnet test                      # Core integration + headless UI tests (needs Docker)
dotnet run --project src/OpcUaBrowser.App
```

A local test server:

```sh
docker run --rm -p 50000:50000 mcr.microsoft.com/iotedge/opc-plc:latest \
  --pn=50000 --autoaccept --unsecuretransport --ph=localhost
```

Connect the app to `opc.tcp://localhost:50000`.

Tip: while the app is running, run tests with `--artifacts-path /tmp/opcua-artifacts` so the test build does not
overwrite the running app's binaries. `OPCUABROWSER_DATA_DIR` points settings/PKI to another folder.

## Code guidelines

- Read [docs/architecture.md](docs/architecture.md) first; OPC UA logic belongs in `OpcUaBrowser.Core`
  (no UI dependencies), UI in `OpcUaBrowser.App`.
- Follow `.editorconfig` and the existing style; nullable reference types are on.
- Persist NodeIds in the `nsu=` form (`ToPortableId`), never namespace indexes.
- Never persist passwords or secrets.
- New behaviour needs a test: Core against opc-plc, UI with `Avalonia.Headless` (`[AvaloniaFact]`).
- Comments explain *why*, not *what*.
- Package versions go in `Directory.Packages.props` only.

## Commits and PR titles

Use [Conventional Commits](https://www.conventionalcommits.org/): `feat(app): ...`, `fix(core): ...`,
`docs: ...`, `build: ...`, `test: ...`. The PR title becomes the squash commit message.

## Releases (maintainers)

Push a tag `vX.Y.Z` on `main`. The release workflow builds the macOS app bundles, publishes a GitHub release and
the Homebrew cask. See the README.

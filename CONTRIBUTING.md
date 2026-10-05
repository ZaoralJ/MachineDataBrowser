# Contributing to MachineDataBrowser

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
(integration tests start an [opc-plc](https://github.com/Azure-Samples/iot-edge-opc-plc) container and build the
simulators in `simulators/`).

```sh
dotnet build                     # warnings are errors
dotnet test                      # Core integration + headless UI tests (needs Docker)
dotnet run --project src/MachineDataBrowser.App
```

Local test servers ([`just`](https://github.com/casey/just) recipes, see [docs/simulators.md](docs/simulators.md)):

```sh
just opcua          # opc-plc                        -> opc.tcp://localhost:50000
just opcua-custom   # custom structures, large tree  -> opc.tcp://localhost:4841/
just cip            # Logix / EtherNet/IP            -> eip://localhost:44818/1,0
just mqtt           # MQTT + Sparkplug B + CloudEvents -> mqtt://localhost:1883
```

Tip: while the app is running, run tests with `--artifacts-path /tmp/opcua-artifacts` so the test build does not
overwrite the running app's binaries. `MACHINEDATABROWSER_DATA_DIR` points settings/PKI to another folder.

Screenshots in README and the user manual (`docs/images`, dark theme) come from `DocsScreenshotTests`. They are
skipped in normal test runs; after a visible UI change run `just docs-screenshots` (needs `pngquant`, e.g.
`brew install pngquant`) and commit the updated PNGs.

The [documentation site](https://zaoralj.github.io/MachineDataBrowser/) is built from `docs/` with MkDocs Material
(`mkdocs.yml`, workflow `pages`) on every push to `main`; pull requests check it with `mkdocs build --strict`.
Preview it locally with `just docs-serve`.

## Code guidelines

- Read [docs/architecture.md](docs/architecture.md) first; protocol logic (OPC UA, EtherNet/IP, MQTT) belongs in `MachineDataBrowser.Core`
  (no UI dependencies), UI in `MachineDataBrowser.App`.
- Follow `.editorconfig` and the existing style; nullable reference types are on.
- Persist NodeIds in the `nsu=` form (`ToPortableId`), never namespace indexes.
- Never persist passwords or secrets.
- New behaviour needs a test: Core against opc-plc or the simulators in `simulators/`, UI with `Avalonia.Headless`
  (`[AvaloniaFact]`).
- Comments explain *why*, not *what*.
- Package versions go in `Directory.Packages.props` only.

## Commits and PR titles

Use [Conventional Commits](https://www.conventionalcommits.org/): `feat(app): ...`, `fix(core): ...`,
`docs: ...`, `build: ...`, `test: ...`. The PR title becomes the squash commit message.

## Releases (maintainers)

Merge the release-please PR to publish a release (see the README). `feat` bumps the minor version, `fix` the
patch; while below 1.0 a breaking change (`feat!:`) bumps the minor. PR titles become the changelog entries.
Pre-releases: `scripts/release.sh X.Y.Z-rc.N` on an up-to-date `main`.

`main` needs one approving review and green `build & test (linux)` / `build & package (macOS)`. The release-please PR
is opened by `github-actions[bot]`, so a maintainer approves it normally (`gh pr review --approve`). A maintainer's
own PR can't be self-approved; it's merged with the admin bypass (`gh pr merge --squash --admin`), and **only after
every check has passed**: never bypass pending or failing checks.

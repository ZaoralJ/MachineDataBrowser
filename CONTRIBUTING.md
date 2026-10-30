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
dotnet test                      # Core, CLI and headless UI tests (needs Docker)
dotnet run --project src/MachineDataBrowser.App
```

Local test servers ([`just`](https://github.com/casey/just) recipes, see [docs/simulators.md](docs/simulators.md)):

```sh
just opcua          # opc-plc                        -> opc.tcp://127.0.0.1:50000
just opcua-custom   # custom structures, large tree  -> opc.tcp://127.0.0.1:4841/
just cip            # Logix / EtherNet/IP            -> eip://127.0.0.1:44818/1,0
just pccc           # SLC 500 / MicroLogix / PLC-5    -> pccc://127.0.0.1:44819?plc=slc500
just modbus         # Modbus TCP                     -> modbus://127.0.0.1:5020/1
just mqtt           # MQTT + Sparkplug B + CloudEvents -> mqtt://127.0.0.1:1883
just s7             # Siemens S7 (tags: simulators/s7/tags.csv) -> s7://127.0.0.1:102/0,1
just mtconnect      # MTConnect agent                -> mtconnect://127.0.0.1:5050
just valkey         # Redis-compatible Valkey        -> redis://127.0.0.1:6379/0
```

Tip: while the app is running, run tests with `--artifacts-path /tmp/opcua-artifacts` so the test build does not
overwrite the running app's binaries. `MACHINEDATABROWSER_DATA_DIR` points settings/PKI to another folder.

Desktop screenshots in README and the user manual (`docs/images`) come from `DocsScreenshotTests`. They are
skipped in normal test runs; after a visible UI change run `just docs-screenshots` (needs `pngquant`, e.g.
`brew install pngquant`) and commit the updated PNGs.
TUI screenshots come from `just docs-tui-screenshot`; CLI examples from `just docs-cli-examples` (both need the
simulators and `uv`).

The [documentation site](https://zaoralj.github.io/MachineDataBrowser/) is built from `docs/` with MkDocs Material
(`mkdocs.yml`, workflow `pages`) on every push to `main`; pull requests check it with `mkdocs build --strict`.
Preview it locally with `just docs-serve`. Build with the same dependencies as CI:

```sh
uv run --with-requirements .github/mkdocs-requirements.txt mkdocs build --strict
```

## Code guidelines

- Read [docs/architecture.md](docs/architecture.md) first. Protocol-neutral contracts belong in `MachineDataBrowser.Core`,
  protocol implementations in `MachineDataBrowser.Driver.*`, composition in `MachineDataBrowser.Drivers`, and desktop UI
  in `MachineDataBrowser.App`. Recording engines and formats live in `MachineDataBrowser.Recording*`.
- Follow `.editorconfig` and the existing style; nullable reference types are on.
- Persist ids through `ToPortableId` (OPC UA uses `nsu=` rather than namespace indexes; other drivers use their own ids).
- Never put passwords or secrets in session/settings files; saved passwords use the opt-in native OS credential store.
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
is opened by `github-actions[bot]`, so GitHub holds its `ci` run ("action required") until a maintainer allows it:
*Approve and run* in the Actions tab, or `gh api -X POST repos/ZaoralJ/MachineDataBrowser/actions/runs/<id>/approve`.
Once it's green, approve the PR (`gh pr review --approve`) and merge it normally. A maintainer's
own PR can't be self-approved; it's merged with the admin bypass (`gh pr merge --squash --admin`), and **only after
every check has passed**: never bypass pending or failing checks.

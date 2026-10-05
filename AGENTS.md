# AGENTS.md

Instructions for coding agents working in this repository. Humans: see [CONTRIBUTING.md](CONTRIBUTING.md).

## What this is

Machine Data Browser: a cross-platform desktop viewer for machine data over **OPC UA**, **EtherNet/IP (Logix)** and
**MQTT** (Sparkplug B, CloudEvents). .NET 10, Avalonia 12, Dock.Avalonia, CommunityToolkit.Mvvm, OPC Foundation
UA-.NETStandard. Read [docs/architecture.md](docs/architecture.md) before changing structure.

| Path | Contents |
|---|---|
| `src/MachineDataBrowser.Core` | protocol clients behind `IDeviceClient` (`Ua/`, `Cip/`, `Mqtt/`), recordings, value formatting/JSON. **No UI references.** |
| `src/MachineDataBrowser.App` | Avalonia app: `ViewModels/` (MVVM, `MainWindowViewModel.*.cs` partials per feature), `Views/`, `Services/` |
| `src/MachineDataBrowser.Cli` | `mdbrowser` command line ([docs/cli.md](docs/cli.md)): System.CommandLine + Spectre.Console, references Core only; `Mcp/` is the MCP server ([docs/mcp.md](docs/mcp.md)) |
| `tests/MachineDataBrowser.Cli.Tests` | the CLI end to end against opc-plc and the MQTT simulator |
| `tests/MachineDataBrowser.Core.Tests` | integration tests against containers (opc-plc, `simulators/`) via Testcontainers, plus unit tests |
| `tests/MachineDataBrowser.App.Tests` | headless Avalonia UI tests (`[AvaloniaFact]`) against the same containers |
| `simulators/` | local test servers (custom OPC UA, Logix, MQTT); `just` recipes start them |
| `docs/` | `user-manual.md`, `architecture.md`, `decisions.md`, `simulators.md`, `images/` |

## Commands

```sh
dotnet build MachineDataBrowser.slnx                       # warnings are errors (incl. CA analyzers)
dotnet test tests/MachineDataBrowser.App.Tests -- --filter-class "*WatchFilterTests"   # MTP filters
dotnet test tests/MachineDataBrowser.Core.Tests -- --filter-method "*Collect_variables*"
dotnet test                                                 # everything; needs Docker, takes several minutes
just docs-screenshots                                       # regenerate docs/images (needs Docker + pngquant)
just docs-tui-screenshot                                    # regenerate docs/images/tui.png (MQTT simulator, uv, pngquant)
just docs-serve                                             # preview the docs site (MkDocs Material, needs uv)
```

Run the narrowest test class that covers your change first, then the affected project. Tests need Docker running.

## Rules

- **Layering:** protocol logic goes in Core behind `IDeviceClient`; the App never talks to a protocol SDK directly.
- **Build clean:** `TreatWarningsAsErrors` with `AnalysisLevel latest-recommended`; fix analyzer findings (e.g. CA1826),
  don't suppress them without a reason.
- **Packages:** versions only in `Directory.Packages.props`.
- **Persistence:** NodeIds are saved in the portable `nsu=` form (`ToPortableId`), never namespace indexes. Never
  persist passwords or secrets. New session/settings fields must be optional so older files still open
  (`SessionDocument.LoadAsync` normalises missing lists).
- **Renamed product:** the app was *OPC UA Browser*. Keep the compatibility paths: `.opcsession` files,
  `OPCUABROWSER_DATA_DIR`, migration of the `OpcUaBrowser` data folder, Homebrew cask rename. See `docs/decisions.md`.
- **Tests:** new behaviour needs a test: Core against opc-plc or `simulators/`, UI with Avalonia.Headless.
- **Docs:** user-visible changes update `docs/user-manual.md` (keep lines ≤ 120 chars). Visible UI changes: rerun
  `just docs-screenshots` and commit the PNGs (`just docs-tui-screenshot` for the TUI). Architecture changes update
  `docs/architecture.md`. `docs/` is also the website (`mkdocs.yml`): new pages go in its `nav`, and
  `mkdocs build --strict` must pass.
- **Comments** explain *why*, not *what*; match the surrounding density.
- **Commits / PR titles:** Conventional Commits (`feat(app): …`, `fix(core): …`, `docs: …`, `test: …`); the PR title
  becomes the squash commit and the changelog entry (release-please). Fill in `.github/PULL_REQUEST_TEMPLATE.md`.
  Branch from `main` (`feat/…`, `fix/…`, `docs/…`); never push to `main`. Merging: see "Releases" in
  CONTRIBUTING.md (approve release-please PRs; admin-merge own PRs only once every check is green).

## Gotchas

- **Keyboard shortcuts** are registered per pane with `Shortcuts.Apply(...)` in each view's code-behind; the same key
  can mean different things in different panes. Check the pane's list for conflicts before adding one (e.g. `G` in
  Watch is "Show recorded values"). Menu shortcuts live in `Views/MainWindow.Menu.cs`.
- **Theme in tests:** `MainWindowViewModel` applies the saved appearance when it is constructed, and a new connection
  tab applies the default one. To render a specific theme, pass a `SettingsStore` with that theme and reset
  `Application.Current.RequestedThemeVariant` in `Dispose`.
- **Headless tests** fail if any exception reaches the dispatcher; `AppErrors`' dispatcher hook can't be exercised
  there, test `AppErrors.Report` / the predicates instead.
- **Dock.Avalonia 12.1** throws "Visual does not belong to a visual tree" during pane drags with
  `UseFloatingDockAdorner`; it stays off (`DockBehavior.cs`) and `AppErrors.IsDockDragGlitch` keeps the leftover case
  out of the error bar.
- **Live values** (opc-plc) are not deterministic and some variables never become Good on CI; wait for the condition
  you actually assert, not for every row to be Good.
- **Number formatting** follows the machine's culture; tests and screenshots that show numbers set en-US explicitly
  (`NumericUpDown` needs its `NumberFormat` set).
- **Running app vs tests:** while the app is running, test with `--artifacts-path /tmp/mdb-artifacts` so the test build
  doesn't overwrite its binaries. `MACHINEDATABROWSER_DATA_DIR` points settings, PKI and logs elsewhere.
- **Error log:** `~/Library/Application Support/MachineDataBrowser/logs/machinedatabrowser.log` (macOS) has full stack
  traces of everything shown in the red error bar; check it first when a user reports an error message.

## Simulators through MCP

The `mdbrowser-*` MCP servers (and `mdbrowser` itself) are often pointed at the local simulators (`just all`); see
[docs/simulators.md](docs/simulators.md). What an agent driving them must know:

| Simulator | Endpoint | Pause tag |
|---|---|---|
| Logix | `eip://localhost:44818/1,0` | `PauseSimulation` |
| Custom types | `opc.tcp://localhost:4841` | `/Objects/Custom/PauseSimulation` |
| MQTT | `mqtt://localhost:1883` | `/Topics/simulator/PauseSimulation` (retained topic) |
| opc-plc | `opc.tcp://localhost:50000` | none: methods `/Objects/OpcPlc/Methods/Stop…`/`StartUpdateFastNodes`/`SlowNodes` |

- **Pause before asserting exact values.** Write `true` to freeze every changing value (the clock stops, so values
  continue from where they were), `false` to continue. Always write `false` again when done: the containers are
  shared with the user and the integration tests.
- **Writes to changing values succeed but don't stick** (Logix behaves like a PLC: the next update replaces the
  value). Pause first to keep a written value; `AccessLevel` "Read, Write" means the data type allows it.
- **Custom types**: only the `DataTypes` scalars and `PauseSimulation` are writable; everything else is read-only.
- **MCP writes** need `mdbrowser mcp --allow-writes` and are confirmed by the user; read-only servers can't pause.
  Without it, use `mdbrowser write <url> <node> true --yes`.
- **Containers start slowly**: the custom server builds 15 000 nodes; `BadConnectionClosed` in the first ~20 s
  after `just all` means wait and retry. `diagnostics` shows whether the connection is healthy.
- **Don't pause in automated tests**: test classes share the containers and run in parallel.

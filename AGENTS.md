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
  `just docs-screenshots` and commit the PNGs. Architecture changes update `docs/architecture.md`.
- **Comments** explain *why*, not *what*; match the surrounding density.
- **Commits / PR titles:** Conventional Commits (`feat(app): …`, `fix(core): …`, `docs: …`, `test: …`); the PR title
  becomes the squash commit and the changelog entry (release-please). Fill in `.github/PULL_REQUEST_TEMPLATE.md`.
  Branch from `main` (`feat/…`, `fix/…`, `docs/…`); never push to `main`.

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

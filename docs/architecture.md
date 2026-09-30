# Architecture

## Projects

| Project | Role |
|---|---|
| `src/OpcUaBrowser.Core` | OPC UA logic, no UI. Session, browse, attributes, monitoring, recordings, value formatting/JSON. |
| `src/OpcUaBrowser.App` | Avalonia 12 desktop app (MVVM with CommunityToolkit.Mvvm, docking with Dock.Avalonia). |
| `tests/OpcUaBrowser.Core.Tests` | Integration tests against an `opc-plc` container and the `simulators/` images (Testcontainers) plus unit tests. |
| `simulators/` | Logix, opc-plc and custom-types test servers for development and tests (`just`, [simulators.md](simulators.md)). |
| `tests/OpcUaBrowser.App.Tests` | Headless Avalonia UI tests (real rendering for screenshots) against the same container. |

Target framework is .NET 10; package versions are pinned centrally in `Directory.Packages.props`;
warnings are errors and the recommended .NET analyzers are enabled.

## Core

`OpcUaClient` wraps one OPC Foundation `Session`:

- **Connect** – endpoint selection (`UseSecurity` picks the most secure endpoint), anonymous or user/password,
  application certificate created on first use. Complex type definitions are loaded after connect
  (`ComplexTypeSystem`) so server-specific structures decode instead of showing as raw bytes.
- **Reconnect** – keep-alive failure starts `SessionReconnectHandler`; subscriptions are transferred and
  `StateChanged` reports `Reconnecting` → `Connected`.
- **Browse** – hierarchical forward references. One extra batched Browse (max 1 reference per child)
  sets `BrowseItem.HasChildren`, so the tree only shows expanders where there is something to expand.
- **Monitoring** – `MonitorManyAsync` creates many items in one `CreateMonitoredItems` round trip and reports
  per-item failures. Items with the same refresh time share one subscription whose publishing interval equals it
  (`Watch@<ms>`); `ChangeRefreshAsync` moves items between them, empty subscriptions are deleted.
- **Portable NodeIds** – `ToPortableId`/`ParsePortableId` use `nsu=<namespace URI>` because namespace indexes
  can change between server restarts. Everything persisted (sessions, exports, JSON) uses this form.
- **Recording** – bounded per-item history (max points, max age), pause/resume/reset, scheduled start and
  auto-stop driven by `TimeProvider` (tested with `FakeTimeProvider`), optional live CSV file, CSV/JSON export.
  `RecordingFileReader` tails a CSV that is still being written.
- **ValueJson** – converts values to JSON keeping OPC UA types (numbers, booleans, arrays, matrices,
  structures as objects; non-finite doubles as strings).

Data, settings and PKI live under `LocalApplicationData/OpcUaBrowser` (`~/Library/Application Support/OpcUaBrowser`
on macOS); `OPCUABROWSER_DATA_DIR` overrides it (used by tests). PKI layout: `pki/own|trusted|issuer|rejected`.

## EtherNet/IP (CIP)

`IDeviceClient` is the protocol boundary; `DeviceClient.Create(url)` picks `Ua.OpcUaClient` (`opc.tcp://`) or
`Cip.CipClient` (`eip://host[:port][/path]`). Protocol-independent operations (`MonitorAsync`, `CollectVariablesAsync`,
`ReadTreeAsync`) are extensions in `DeviceClientExtensions`; `DeviceClient.StopMonitoringAsync` stops handles of any client.

`CipClient` (libplctag, Logix, read-only):

- **Tree** – `Root` ▸ `Controller Tags` / `Programs` ▸ `<program>` (ns=2 folders), then tags as ns=1 string NodeIds
  holding the Logix path (`Program:Main.Motor[2].Speed`); the portable id is the path itself, folders are `@<name>`.
- **Types** – `@tags` listings per scope and `@udt/<id>` templates are parsed by `LogixCodec` and cached. Atomics,
  atomic arrays and STRING-like UDTs (`LEN` + `DATA`) are Variables; structures and arrays of structures are Objects
  with member/element children (max 1000 elements). Hidden BOOL host members (`ZZZZZZZZZZ…`) are skipped.
- **Monitoring** – one poll loop per refresh time reads its tags concurrently and reports only changed bytes/status.
  Communication errors switch the state to `Reconnecting` until a read succeeds (libplctag reconnects itself).
- libplctag errors surface as `ServiceResultException` like the OPC UA client, so App error handling is shared.

## App

- `MainWindowViewModel` (split into partial files: main, `.Session`, `.Recordings`) owns the client, the tree
  (`NodeViewModel`), the watch list (`WatchItemViewModel`), recordings and settings.
- Value notifications arrive on SDK threads; the latest value per item is buffered and flushed to the UI every
  200 ms, so fast servers cannot flood the UI thread.
- Views are Dock tools (`Docking.cs`): Address Space, Attributes, Watch, Recordings. `DockBehavior` sets global
  Dock options (larger drag threshold, owned floating windows, overlay drop indicators). Panes float only via
  *View ▸ Panes ▸ Pop Out*; *Dock back*, ⌘1–⌘4 and *Dock All Floating Panes* return them.
- Persistence (`Services/`): `.opcsession` files (endpoint, options, default refresh, watch list with per-item
  refresh, watch columns and sort – never passwords), `settings.json` (theme, zoom, defaults, recent lists),
  `layout.json` (dock arrangement; collapsed splits are repaired on load).
- The menu is an Avalonia `NativeMenu` – the macOS global menu bar, an in-window menu elsewhere. It is built once
  and mutated, because replacing the menu instance crashes the macOS exporter.

## Packaging

`packaging/macos/package.sh` publishes a self-contained `OPC UA Browser.app` per architecture (runtime included,
ad-hoc signed, not notarized) and zips it. `.github/workflows/release.yml` runs it on `v*` tags, publishes a GitHub
release and a filled-in Homebrew cask (`packaging/homebrew/opcua-browser.rb`) for the tap `ZaoralJ/homebrew-tap`.

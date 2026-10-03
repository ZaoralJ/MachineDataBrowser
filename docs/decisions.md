# Decisions

Short record of the choices behind the current stack. Facts were checked at the time (September 2026).

The app started as an OPC UA browser and is now a machine data viewer for OPC UA, EtherNet/IP (Logix) and MQTT.
## Name: Machine Data Browser (renamed from OPC UA Browser after 0.8)

- Everything carries the new name: repository, projects and namespaces, app bundle (`Machine Data Browser.app`,
  `com.zaoralj.machinedatabrowser`), Homebrew cask (`machine-data-browser`), data folder, session files (`.mdbsession`)
  and the OPC UA client identity (`MachineDataBrowser`). An earlier choice kept the old technical names to avoid
  breaking installs; the rename instead migrates:
  - Homebrew: the tap's `cask_renames.json` maps `opcua-browser` to `machine-data-browser`, so `brew upgrade` replaces
    the old cask and app.
  - Data: on first start the old `OpcUaBrowser` data folder is copied (left in place for older versions);
    `OPCUABROWSER_DATA_DIR` is still honoured.
  - Sessions: `.opcsession` files still open and save back to the same file.
- Not migrated: the OPC UA application certificate. Its subject and application URI carry the name, so a new one is
  created and servers that trusted the old one must trust it once.

## OPC UA SDK: OPC Foundation UA-.NETStandard

- Licensed under the OPC Foundation MIT License (`LICENSE.txt` in the repository; the former GPL-2.0/RCL
  dual license no longer applies).
- Mature client with security policies, reconnect/subscription transfer and runtime decoding of custom
  structures (`Opc.Ua.Client.ComplexTypes`), which a generic browser needs.
- Alternatives considered: `async-opcua` (Rust, MPL-2.0, younger), open62541 (C, MPL-2.0; runtime structure
  decoding is manual), node-opcua (MIT, heavier runtime), Eclipse Milo (Java). No maintained MIT Rust SDK exists.

## EtherNet/IP (Logix): libplctag.NET

- `libplctag` (MPL-2.0) wraps the mature C libplctag and ships native binaries for macOS (x64/arm64), Windows and Linux.
- Scope: Logix only (tag listing via `@tags` / `@udt/<id>` exists only there). Writes cover atomics, atomic arrays
  and STRINGs.
- The OPC UA information model (`NodeId`, `NodeClass`, `StatusCode`) is kept as the shared vocabulary behind
  `IDeviceClient` instead of a new neutral model: the App, recordings and exports work unchanged and the refactor
  stays small. Tags map to `ns=1;s=<tag path>`, folders to `ns=2`.
- The protocol is chosen by the endpoint URL scheme (`eip://`), so session files need no new field.
- CIP has no subscriptions: monitoring polls per refresh time and reports only changes.
- `ab_server` (libplctag's simulator) does not implement tag listing, so the repository has its own Logix simulator
  (`simulators/cip`, pure Python, see [simulators.md](simulators.md)) that the CIP integration tests run against.
- Tags are created with the synchronous `Initialize` on a pool thread: libplctag.NET 1.5 `InitializeAsync` leaks the
  native tag and its callback when creation fails, which crashed the process on a later libplctag event.

## MQTT: MQTTnet, with Sparkplug B

- `MQTTnet` (MIT) supports MQTT 3.1.1 and 5, TLS and WebSocket on every platform the app targets.
- The app subscribes, and publishes only when you write a value: a topic is republished with the same payload kind,
  retain flag and properties; a JSON field republishes the last document with the field changed; a Sparkplug B metric
  is sent as an NCMD/DCMD. It does not send rebirth requests, so metrics published by alias before the app saw the
  BIRTH show as `alias <n>` until the edge node's next birth.
- Discovery by subscription: MQTT has no browse service, so the tree is built from received messages (topic filter
  `#` by default, narrowed by the endpoint path or `?topic=`), with a cap of 20 000 topics. The tree refreshes live
  (`IDynamicAddressSpace`).
- Sparkplug B is decoded by a small hand-written protobuf reader (`SparkplugB.cs`) instead of generated code: only
  the payload/metric fields the viewer shows are needed, and it avoids a protobuf toolchain in the build.
- Payloads are decoded as number, boolean, text, JSON (fields become nodes, addressed by JSON pointer) or binary.
  CloudEvents (structured JSON and binary mode with MQTT 5 user properties) are recognised and labelled.
- The refresh time stays meaningful for MQTT as a maximum update rate: the latest value at most once per interval.
  This keeps recordings of fast topics manageable. 0 delivers every message, e.g. for events where each one
  matters.

## Resilience and threading

- A global handler (`AppErrors`) logs every unhandled exception to `logs/machinedatabrowser.log` and shows it in the error
  bar; library callbacks (OPC UA notifications, reconnect, CIP polling, MQTT messages, recording timers) catch their
  own errors because an exception on a thread-pool thread would end the process.
- Connect, disconnect and shutdown work runs off the UI thread; values reach the UI only through a 200 ms flush.

## UI: Avalonia 12

- Only mature .NET UI framework that runs natively on macOS (incl. Apple Silicon), Windows and Linux.
- .NET MAUI has no official Linux support; a web UI cannot open `opc.tcp` sockets without a gateway;
  Qt's OPC UA module is GPL-only in the open-source tier.
- `Avalonia.Controls.TreeDataGrid` 12.x requires a paid license key, so the tree is a `TreeView` and the tables use
  the MIT `DataGrid`.

## Docking: Dock.Avalonia (MIT)

Rearrangeable panes with persistence. Floating by drag caused accidental pop-outs, so it is disabled and floating
is an explicit command; every pane can always be restored from *View ▸ Panes*.

## Distribution: self-contained app + Homebrew cask

No .NET installation required on the target Mac. Not notarized (needs an Apple Developer ID), so the first launch
may need `xattr -dr com.apple.quarantine "/Applications/Machine Data Browser.app"` or *Open Anyway*.

## Command line: `mdbrowser`

- A separate project (`MachineDataBrowser.Cli`) on top of Core only, so the protocols, ids and value formatting are the
  app's: `endpoints`, `browse`, `read`, `monitor`, and `run` for a session saved in the app (the session file is read
  with its own small model, ignoring unknown fields, so the CLI doesn't depend on the App).
- Named `mdbrowser`, not `mdb`: short names clash with other tools (e.g. `mdbtools`) on `PATH` and in Homebrew.
- Parsing with **System.CommandLine** 2 (Microsoft, stable, generated help, no reflection). Output with
  **Spectre.Console** (tables, a tree, a live watch table) only when stdout is a terminal and the format is text;
  pipes, files, `--format json` and `csv` stay plain, so scripts see stable output. Spectre.Console.Cli (reflection
  based) and ConsoleAppFramework were considered; parsing wasn't where they add value.
- Shares the app's data folder: the same trusted certificates and OPC UA client identity, so servers that trust the
  app trust the CLI.
- Shipped as a self-contained single file per OS/architecture (macOS and Linux, arm64 and x64; libplctag's native
  library is bundled and extracted on first run), not native AOT: the OPC UA SDK relies on reflection. Distributed as
  a Homebrew **formula** (`Formula/mdbrowser.rb`) in the same tap as the app's cask; formulae also install on Linux.

## Deferred

- Windows/Linux packages – the app builds for `win-x64`, but no release artifacts yet.
- Notarization.
- CLI follow-ups: `write`/`call`, `history`/`events`, `check`/`wait` for scripts, a Prometheus or MQTT bridge, an MCP
  server for AI agents.

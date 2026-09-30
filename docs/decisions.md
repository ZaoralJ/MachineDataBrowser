# Decisions

Short record of the choices behind the current stack. Facts were checked at the time (September 2026).

## OPC UA SDK: OPC Foundation UA-.NETStandard

- Licensed under the OPC Foundation MIT License (`LICENSE.txt` in the repository; the former GPL-2.0/RCL
  dual license no longer applies).
- Mature client with security policies, reconnect/subscription transfer and runtime decoding of custom
  structures (`Opc.Ua.Client.ComplexTypes`), which a generic browser needs.
- Alternatives considered: `async-opcua` (Rust, MPL-2.0, younger), open62541 (C, MPL-2.0; runtime structure
  decoding is manual), node-opcua (MIT, heavier runtime), Eclipse Milo (Java). No maintained MIT Rust SDK exists.

## EtherNet/IP (Logix): libplctag.NET, read-only

- `libplctag` (MPL-2.0) wraps the mature C libplctag and ships native binaries for macOS (x64/arm64), Windows and Linux.
- Scope: Logix only (tag listing via `@tags` / `@udt/<id>` exists only there) and read-only.
- The OPC UA information model (`NodeId`, `NodeClass`, `StatusCode`) is kept as the shared vocabulary behind
  `IDeviceClient` instead of a new neutral model: the App, recordings and exports work unchanged and the refactor
  stays small. Tags map to `ns=1;s=<tag path>`, folders to `ns=2`.
- The protocol is chosen by the endpoint URL scheme (`eip://`), so session files need no new field.
- CIP has no subscriptions: monitoring polls per refresh time and reports only changes.
- `ab_server` (libplctag's simulator) does not implement tag listing, so CIP is covered by codec/path unit tests; a
  manual smoke test against `ab_server` verified native loading and reads.

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
may need `xattr -dr com.apple.quarantine "/Applications/OPC UA Browser.app"` or *Open Anyway*.

## Deferred

- CLI (`OpcUaBrowser.Cli` sharing Core) – planned commands `endpoints`, `browse`, `read`, `monitor`, `record`.
- Windows/Linux packages – the app builds for `win-x64`, but no release artifacts yet.
- Server certificate trust prompt (currently an explicit, insecure "auto-trust" option), trend charts,
  write/method call, notarization.

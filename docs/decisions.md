# Decisions

Short record of the choices behind the current stack. Facts were checked at the time (September 2026).

The app started as an OPC UA browser and is now a machine data viewer for OPC UA, EtherNet/IP (Logix) and MQTT.
The app is shown as "Machine Data Browser". The app bundle (`OPC UA Browser.app`), the Homebrew cask
(`opcua-browser`), the data folder and the repository (`OpcUaBrowser`) keep their names, so updates and existing
data keep working.

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
- `ab_server` (libplctag's simulator) does not implement tag listing, so the repository has its own Logix simulator
  (`simulators/cip`, pure Python, see [simulators.md](simulators.md)) that the CIP integration tests run against.
- Tags are created with the synchronous `Initialize` on a pool thread: libplctag.NET 1.5 `InitializeAsync` leaks the
  native tag and its callback when creation fails, which crashed the process on a later libplctag event.

## MQTT: MQTTnet, read-only, with Sparkplug B

- `MQTTnet` (MIT) supports MQTT 3.1.1 and 5, TLS and WebSocket on every platform the app targets.
- Read-only like the other protocols: the app subscribes, never publishes. Consequence for Sparkplug B: it cannot
  send a rebirth request (NCMD), so metrics published by alias before the app saw the BIRTH show as `alias <n>` until
  the edge node's next birth.
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

- A global handler (`AppErrors`) logs every unhandled exception to `logs/opcuabrowser.log` and shows it in the error
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
may need `xattr -dr com.apple.quarantine "/Applications/OPC UA Browser.app"` or *Open Anyway*.

## Deferred

- CLI (`OpcUaBrowser.Cli` sharing Core) – planned commands `endpoints`, `browse`, `read`, `monitor`, `record`.
- Windows/Linux packages – the app builds for `win-x64`, but no release artifacts yet.
- Server certificate trust prompt (currently an explicit, insecure "auto-trust" option), write/method call and
  MQTT publishing (the app is read-only by design), notarization.

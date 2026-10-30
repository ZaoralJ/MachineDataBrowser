---
hide:
  - navigation
  - toc
---

# Machine Data Browser

A cross-platform viewer for machine data. Connect to a device, browse its address space, watch live values, record
them and export them.

![Machine Data Browser connected to an OPC UA server: address space, attributes and live watch values](images/main-window.png)

| Protocol | Endpoint | Highlights |
|---|---|---|
| **OPC UA** | `opc.tcp://127.0.0.1:4841` | secure endpoints, user login, custom structures, history, events & alarms, methods |
| **EtherNet/IP** (Allen-Bradley Logix) | `eip://127.0.0.1:44818/1,0` | controller and program tags, UDTs, arrays of UDTs |
| **Modbus TCP** | `modbus://127.0.0.1:5020/1` | coils, inputs and registers by address, typed values (float32, int32, strings, word order), register maps naming them |
| **PCCC** (SLC 500, MicroLogix, PLC-5) | `pccc://127.0.0.1:44819?plc=slc500` | data files, timers, counters, controls, bits |
| **S7** (Siemens S7-300/400/1200/1500) | `s7://127.0.0.1/0,1` | data blocks, inputs, outputs, memory by address, typed values and arrays, TIA Portal / STEP 7 tag lists naming them |
| **MQTT** | `mqtt://127.0.0.1:1883[/topic/#]`, `mqtts://`, `ws://`, `wss://` | topic tree, JSON fields, Sparkplug B, CloudEvents |
| **MTConnect** | `mtconnect://127.0.0.1:5050[/device]`, `http://`, `https://` | devices and components, samples, events, conditions, agent buffer as history; read-only |
| **Redis / Valkey** | `redis://127.0.0.1:6379[/database]`, `rediss://` | key prefixes, strings and hash fields; update existing values |

## Install (macOS)

```sh
brew install --cask zaoralj/tap/machine-data-browser
```

The app is installed in `~/Applications`, so no administrator password is needed.

The app is self-contained (no .NET needed). It is signed with a Developer ID and notarized by Apple, so it opens
like any other Mac app.

## Read more

- [User manual](user-manual.md): every feature, with screenshots and shortcuts.
- [MCP server](mcp.md): let AI agents (Copilot, Claude, opencode) browse and read machines, with opt-in writes.
- [Command line](cli.md): `mdbrowser` to browse, read and monitor from a terminal or script (macOS, Linux).
- [Test servers](simulators.md): OPC UA, Logix, PCCC, Modbus, S7, MQTT, MTConnect and Valkey simulators to try it without
  hardware.
- [Architecture](architecture.md) and [design decisions](decisions.md): for contributors.
- [Source, releases and issues on GitHub](https://github.com/ZaoralJ/MachineDataBrowser).

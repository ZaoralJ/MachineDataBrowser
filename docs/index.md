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
| **MQTT** | `mqtt://127.0.0.1:1883[/topic/#]`, `mqtts://`, `ws://`, `wss://` | topic tree, JSON fields, Sparkplug B, CloudEvents |

## Install (macOS)

```sh
brew install --cask zaoralj/tap/machine-data-browser
```

The app is self-contained (no .NET needed). It is signed with a Developer ID and notarized by Apple, so it opens
like any other Mac app.

## Read more

- [User manual](user-manual.md): every feature, with screenshots and shortcuts.
- [MCP server](mcp.md): let AI agents (Copilot, Claude, opencode) browse and read machines, read-only.
- [Command line](cli.md): `mdbrowser` to browse, read and monitor from a terminal or script (macOS, Linux).
- [Test servers](simulators.md): OPC UA, Logix and MQTT simulators to try it without hardware.
- [Architecture](architecture.md) and [design decisions](decisions.md): for contributors.
- [Source, releases and issues on GitHub](https://github.com/ZaoralJ/MachineDataBrowser).

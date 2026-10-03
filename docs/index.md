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
| **OPC UA** | `opc.tcp://host:4840` | secure endpoints, user login, custom structures, history, events & alarms, methods |
| **EtherNet/IP** (Allen-Bradley Logix) | `eip://192.168.1.10/1,0` | controller and program tags, UDTs, arrays of UDTs |
| **MQTT** | `mqtt://broker[:port][/topic/#]`, `mqtts://`, `ws://`, `wss://` | topic tree, JSON fields, Sparkplug B, CloudEvents |

## Install (macOS)

```sh
brew install --cask zaoralj/tap/machine-data-browser
```

The app is self-contained (no .NET needed). It is not notarized; if macOS blocks the first launch:

```sh
xattr -dr com.apple.quarantine "/Applications/Machine Data Browser.app"
```

## Read more

- [User manual](user-manual.md): every feature, with screenshots and shortcuts.
- [Command line](cli.md): `mdbrowser` to browse, read and monitor from a terminal or script (macOS, Linux).
- [Test servers](simulators.md): OPC UA, Logix and MQTT simulators to try it without hardware.
- [Architecture](architecture.md) and [design decisions](decisions.md): for contributors.
- [Source, releases and issues on GitHub](https://github.com/ZaoralJ/MachineDataBrowser).

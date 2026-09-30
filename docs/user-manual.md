# User manual

Machine Data Browser is a read-only viewer for machine data. It connects to:

- **OPC UA** servers (`opc.tcp://`),
- Allen-Bradley **Logix** controllers over EtherNet/IP (`eip://`),
- **MQTT** brokers (`mqtt://`, `mqtts://`, `ws://`, `wss://`), including **Sparkplug B** and **CloudEvents**.

It shows the address space and attributes, watches live values, records them and exports them. It never writes to
a device.

```mermaid
flowchart LR
    A[Enter endpoint · Connect] --> B[Browse the Address Space]
    B --> C[Attributes of the selected node]
    B -->|Enter · double-click · drag| D[Watch list]
    D -->|R · Record selected| E[Recording]
    E --> F[Recording viewer<br/>table · chart · export]
    D -->|⌘C · menu| G[Copy / export<br/>table · JSON · CSV · C#]
```

## Window

| Pane | What it shows |
|---|---|
| **Address Space** (⌘1) | tree of the device: OPC UA nodes, Logix tags, MQTT topics and Sparkplug metrics |
| **Attributes** (⌘2) | attributes of the selected node; a variable's value updates live |
| **Watch** (⌘3) | monitored values: status, refresh, last update, since, recorded samples, value |
| **Recordings** (⌘4) | recordings with state, elapsed time, kept samples and details |

- Panes can be rearranged. **View ▸ Panes ▸ Pop Out** floats a pane; **Dock All Floating Panes** (⌥⌘D) and
  **Reset Layout** (⌥⌘0) bring them back.
- **Zoom:** ⌘+ / ⌘− / ⌘0.
- **Theme:** View ▸ Theme, or Settings.
- The **status bar** shows what just happened and the connection state (Connected, Reconnecting…).
- The **red bar** under the header shows errors; nothing makes the app crash.

## Connecting

Type the endpoint and press **Enter** or click **Connect** (⌘↩). The ▾ button (or ↓) lists recent endpoints.

| Protocol | Endpoint | Notes |
|---|---|---|
| OPC UA | `opc.tcp://host:4840` | ⚙ options: secure endpoint, user name/password, auto-trust certificates |
| EtherNet/IP | `eip://192.168.1.10/1,0` | `/backplane,slot`; defaults to `1,0` |
| MQTT | `mqtt://broker:1883` | TLS `mqtts://` (8883), WebSocket `ws://host/mqtt` / `wss://` |
| MQTT, narrowed | `mqtt://broker/plant/#` or `mqtt://broker?topic=plant/%2B/status` | subscribe only to part of the broker |
| MQTT with login | `mqtt://user:password@broker` | or user name/password in the ⚙ options |

**Connection options (⚙):**
- The default refresh time for new watch items.
- For OPC UA and MQTT: user name and password.
- For OPC UA only: "Use secure endpoint".
- "Auto-trust server certificates" accepts OPC UA server certificates and TLS certificates of MQTT brokers without
  checking them. Use it on lab networks only.

**Disconnect:** ⇧⌘D. If the connection drops, the app shows *Connection lost. Reconnecting…* and restores the
connection by itself. Watch items and recordings continue after that.

## Address Space

- Expand nodes with a click or the arrow keys. **E** expands everything below the selection (up to 5 levels);
  **⇧E** collapses all.
- The selected node's attributes appear in the Attributes pane.
- **Monitor** a variable: Enter, double-click, drag it to Watch, or right-click ▸ Monitor. **1–7** monitor with a
  refresh of 100 ms … 10 s; **T** asks for a custom refresh time. **F** monitors every variable in a folder,
  including subfolders, up to the limit set in Settings.
- **Copy:**
  - ⌥⌘N copies the NodeId.
  - ⇧⌘C copies the current values of the node and everything below it as JSON.
  - ⇧⌘K / ⌥⌘K copy C# classes or records that mirror the structure.
  - To cherry-pick properties, select several nodes first.

### Search

⌘F (View ▸ Find in Address Space…) or **/** in the tree puts the cursor in the search box above the tree.

- Type part of a name or id and press **Enter**. Case doesn't matter; `*` and `?` are wildcards (`Temp*`, `Press?`).
- **Where it searches:** below the selected folder, or the whole tree if nothing with children is selected. Select a
  folder first to search a big server faster.
- **Results:** each result shows its name and path. ↓ moves into the list; **Enter** or double-click opens the tree at
  that node and selects it.
- **Esc** clears the search.
- **Limits:** the search browses the device breadth-first, up to 12 levels and 50 000 nodes, and returns at most
  1 000 results. The status line says when a limit was reached.
- **MQTT:** only topics received so far can be found.

### What the tree looks like per protocol

- **OPC UA:** the server's address space. Custom structures are decoded using the server's type definitions.
- **EtherNet/IP:** *Controller Tags* and *Programs*. UDTs and arrays of UDTs expand into members and elements
  (arrays show up to 1000 elements).
- **MQTT, *Topics*:** topic levels as folders. A topic that received a message is a variable.
  - A topic can have a value and also subtopics, e.g. `plant/hall1`.
  - JSON payloads expand into their fields, so you can watch or chart a single field such as
    `machines/m1/status ▸ temperature ▸ bearing`.
  - Attributes show the retained flag, QoS, size, content type and MQTT 5 user properties. A
    [CloudEvent](https://cloudevents.io) is labelled as structured or binary, with its event type.
  - Topics appear as messages arrive. Retained topics appear at once; the tree refreshes itself.
- **MQTT, *Sparkplug B*:** group ▸ edge node ▸ device ▸ metrics. A metric name with `/`, such as `Motor/Speed`,
  becomes a folder.
  - When a node or device dies (NDEATH/DDEATH), its metrics turn **Bad (no communication)** until it is reborn.
  - Metrics named `alias 101` were published before the app saw the device's BIRTH message. They get their real
    names at the next birth; the app is read-only, so it cannot request one.

## Watch

Each row shows Name, Status, Refresh, Last update, Since, Recorded and Value. NodeId and Source time are hidden
columns; the ⫼ button shows or hides columns and reorders them by dragging the headers. Column layout and sort
order are saved with the session.

- **Status and age:**
  - *Since* turns amber when a value is **stale**, i.e. there has been no update for 5× its refresh time (at least
    5 s).
  - The status column shows **Bad** or **Uncertain** in colour.
- **Select and remove problem rows:**
  - The clock button (or **S**) selects stale rows; the ! button (or **B**) selects bad rows.
  - Double-clicking the button, or pressing the key twice, removes them.
  - ⇧S selects rows that are stale or bad.
  - ⇧Delete removes all rows. If recordings are still running, the app asks whether to stop them, close them or keep
    them running.
- **Refresh time:** 1–7 (100 ms … 10 s), T for a custom time, or right-click ▸ Refresh time.
  - OPC UA uses it as the sampling interval, Logix as the poll interval.
  - For MQTT it is a maximum update rate: at most the latest value once per interval. **0** means every message;
    use it for events, CloudEvents or anything where each message matters.
- **Recorded values:**
  - A red dot marks a row that is being recorded; *Recorded* shows how many samples are kept.
  - Space, G, double-click or the chart button in the Name cell opens that item's recorded values. Double-click on a
    row that isn't recorded shows it in the address space; so does Enter.
- **Copy:**
  - ⌘C copies the selected rows (or all rows) as a table that pastes into Excel or Numbers.
  - ⌥⌘C copies the value; ⌥⌘J copies the values as JSON; ⇧⌘C copies the rows as JSON.
  - File ▸ Export Watch List as CSV (⌘E).

## Recordings

A recording captures the values of its items with timestamps. It keeps running when you remove the items from Watch.

- **Start:** R records the selected Watch rows (or all of them); ⇧R records everything monitored. The form sets:
  - name and refresh time,
  - **Max points per item** (the oldest samples are dropped),
  - **Keep last (min)** (samples older than this are dropped),
  - a delayed start and an automatic stop,
  - **Also write to file**: a CSV with *every* sample, not limited by the two settings above.
- **Control (Recordings pane):**
  - S start/resume, P pause, X stop.
  - ⇧⌫ reset the history; Delete close (discards the history).
  - C / J export CSV / JSON; O opens a CSV file and follows it while it is still being written.
- **Settings of a running recording:** right-click ▸ Settings… (E). Name, refresh time, limits, auto-stop and file
  change immediately.
- **Add items to a recording:** in Watch, right-click ▸ *Add to recording ▸ name*.

### Recording viewer

Opened with Enter or double-click in Recordings, or from Watch for a single item.

- The table shows the kept samples (Received, Name, Status, Value, and optionally NodeId and Source time). The item
  box filters to one item.
- **Chart:** every numeric value is charted above the table; G shows or hides it.
  - With one item selected, the chart shows that item.
  - With *All items*, each numeric item gets its own coloured line and a legend.
  - When the items' ranges differ a lot, each line is scaled to its own range. The axis then shows percent, and
    the legend shows each item's real minimum and maximum.
  - Numbers, booleans (1/0) and numeric text are charted; other values are left out.
  - Selecting a row marks that sample on the chart; clicking the chart selects the nearest row.
- **F** turns *Follow latest* on or off. C chooses columns. ⌘C copies the selected rows as a table.
- Times are local (`HH:mm:ss.fff`). Hover a time to see the full date and UTC offset.
- The status bar shows kept vs received samples and the per-item limit.

## Several instances

File ▸ **New Instance** (⇧⌘N) starts another copy of the app. Each copy has its own connection, watch list and
recordings, so you can look at several devices side by side. On macOS the Dock only brings the running app to the
front, so use this menu item, or `open -n "/Applications/OPC UA Browser.app"` from a terminal.

All copies share settings, certificates, the layout and the log. When two copies change settings, the last save
wins.

## Sessions and settings

- **Sessions (`.opcsession`):**
  - A session holds the endpoint, the options (never passwords), the default refresh, the watch list and its
    columns.
  - New ⌘N, Open ⌘O, Save ⌘S, Save As ⇧⌘S, File ▸ Open Recent.
  - Settings can reopen the last session at start.
- **Settings (⌘,):** theme, default refresh time, and the item limit for "monitor all variables in folder".
- **Data folder:** Help ▸ Show Settings Folder (⌥⇧⌘,). On macOS it is
  `~/Library/Application Support/OpcUaBrowser`. It holds:
  - `settings.json` and `layout.json`,
  - the `pki/` certificate stores (Connection ▸ Show Certificate Folder),
  - `logs/opcuabrowser.log` with details of every error shown.

## Quitting

- If the session has unsaved changes, the app asks once: Save, Don't Save or Cancel. Everything keeps running while
  the question is open.
- **Holding ⌘Q** quits without asking.
- On quit, recordings are stopped, live CSV files are completed and the connection is closed properly.

## Keyboard

Every menu command has a shortcut, and each pane has its own single-key shortcuts. They're shown in the menus, the
right-click menus and the tooltips. **Help ▸ Keyboard Shortcuts** (⌘/) lists them all.

- A pane's shortcuts work when you click anywhere in it, or simply point at it.
- On Windows and Linux, ⌘ is Ctrl and ⌥ is Alt.
- In dialogs, **Enter** confirms and **Esc** cancels. Enter or Esc also closes the recording viewer and information
  windows.

## Troubleshooting

| Symptom | What to check |
|---|---|
| OPC UA: `BadCertificateUntrusted` | Trust the server certificate in the certificate folder, or use "auto-trust" on a lab network |
| OPC UA structure shows as bytes | The server does not publish its type definitions; the raw value is still shown |
| EtherNet/IP: no tags | Only Logix (ControlLogix/CompactLogix) lists tags; check the path (`/1,0` = backplane 1, slot 0) |
| MQTT: empty tree | Nothing has been published yet on the filter (only retained messages appear at once); check the topic filter in the URL |
| MQTT: `alias 101` instead of names | The Sparkplug device's BIRTH was published before connecting; names appear with the next birth |
| MQTT: values stop, metrics Bad | The Sparkplug node or device sent a DEATH, or the broker connection is reconnecting (status bar) |
| Something failed | The error bar shows it; details are in `logs/opcuabrowser.log` in the data folder |

For local test servers (OPC UA, Logix, MQTT with Sparkplug B and CloudEvents) see [simulators.md](simulators.md).

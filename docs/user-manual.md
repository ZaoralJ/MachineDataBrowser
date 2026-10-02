# User manual

Machine Data Browser is a viewer for machine data. It connects to:

- **OPC UA** servers (`opc.tcp://`),
- Allen-Bradley **Logix** controllers over EtherNet/IP (`eip://`),
- **MQTT** brokers (`mqtt://`, `mqtts://`, `ws://`, `wss://`), including **Sparkplug B** and **CloudEvents**.

It shows the address space and attributes, watches live values, records them and exports them. It can also write
values to a device, but only when you ask it to (see [Writing values](#writing-values)).

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
  - *Follow System*, *Light* or *Dark* (⌥⌘7/8/9) picks the appearance.
  - The colour theme sets the palette for both appearances: **Indigo** (default), **Graphite** (pure grey, blue),
    **Ocean** (sea-glass / deep navy, teal), **Forest** (sage / deep green), **Amber** (sand / dark brown),
    **Nord**, **Solarized** and **Dracula**. Settings previews it while you choose; Cancel returns to the saved one.
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
- With auto-trust off, an OPC UA server whose certificate is not trusted yet shows it (subject, issuer, validity,
  thumbprint and the reason) and asks: **Trust Once** connects this time only, **Always Trust** adds it to
  `pki/trusted` so later connections don't ask, **Cancel** doesn't connect.

**Diagnostics:** Connection ▸ **Diagnostics…** (⇧⌘I) shows the connection live, refreshed every second. Use it when
values seem to stop:
- *Session* (OPC UA): endpoint, security, user, keep-alive, reconnects, outstanding requests, and the server's state,
  clock (with its offset from this computer), start time and product.
- *This app*: watched items, value updates per second, and how many rows are stale, bad or uncertain.
- *Subscriptions* (OPC UA): the publishing interval the server granted (it may differ from the refresh time), items,
  notifications and when the last one arrived.

**Disconnect:** ⇧⌘D. If the connection drops, the app shows *Connection lost. Reconnecting…* and restores the
connection by itself. Watch items and recordings continue after that.

## Address Space

- Expand nodes with a click or the arrow keys (→ expands or steps into the first child, ← collapses or goes to the
  parent). **E** expands everything below the selection (up to 5 levels); **⇧E** collapses all.
- Type the start of a name to jump to the next visible node with that name.
- If a folder can't be browsed, it shows a red *Couldn't load* row: hover it for the reason, click it to retry.
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
    names at the next birth. The app does not request a rebirth.

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
- **Filter:** the box at the top right (or **/**) shows only rows whose name, NodeId, value or status contains the
  text; Esc clears it. **Problems only** (P) shows Bad, Uncertain and stale rows. Rows join and leave the filtered list
  as their values change. ⌘C and the stale/bad selection act on what is shown; the count says how many are hidden.
- **Refresh time:** 1–7 (100 ms … 10 s), T for a custom time, or right-click ▸ Refresh time.
  - OPC UA uses it as the sampling interval, Logix as the poll interval.
  - For MQTT it is a maximum update rate: at most the latest value once per interval. **0** means every message;
    use it for events, CloudEvents or anything where each message matters.
- **Display format:** right-click ▸ **Display format…** for the selected rows (every protocol).
  - *Format*: as sent, a fixed number of decimals, or for integers hex (`0x00FF`, two's complement for negatives),
    binary in groups of four, or *Bits*, the list of set bits (for status and alarm words).
  - *Scale*: value × gain + offset, e.g. a raw analog input 0…27648 to 0…100 %.
  - *Unit*: shown after numbers. OPC UA variables with an EngineeringUnits property show theirs automatically; type
    another, or untick to hide it.
  - Display only: filters, snapshots, recordings, exports and *Write value* use the device's value; the tooltip shows
    it. Saved with the session.
- **Monitoring settings** (OPC UA): right-click ▸ **Monitoring settings…** for the selected rows.
  - *Own sampling interval*: sample faster or slower than the refresh time, at which values are still published.
  - *Queue size*: how many samples the server keeps between publishes; above 1, every sample arrives, not only the
    last. *Discard oldest* decides which ones are dropped when the queue overflows.
  - *Deadband*: report only changes larger than an absolute amount, or a percent of the variable's EURange (the
    server rejects percent without one; the row keeps its previous settings).
  - Rows with changed settings show ⚙ next to the refresh time; the tooltip lists them. They are saved with the
    session and kept when the refresh time changes. Recordings use their own monitored items and are not affected.
- **Recorded values:**
  - A red dot marks a row that is being recorded; *Recorded* shows how many samples are kept.
  - Space, G, double-click or the chart button in the Name cell opens that item's recorded values. Double-click on a
    row that isn't recorded shows it in the address space; so does Enter.
- **Copy:**
  - ⌘C copies the selected rows (or all rows) as a table that pastes into Excel or Numbers.
  - ⌥⌘C copies the value; ⌥⌘J copies the values as JSON; ⇧⌘C copies the rows as JSON.
  - File ▸ Export Watch List as CSV (⌘E).

### Snapshots

A snapshot saves the current values of the whole watch list, to compare later, e.g. before and after a change on the
machine.

- **Watch ▸ Take Snapshot** (⌥⌘T) saves it, named with the date and time.
- **Watch ▸ Compare with Snapshot…** (⌥⌘Y) shows *Before* (a snapshot) next to *After*: the live watch values,
  updated every second, or another snapshot.
  - Items are matched by NodeId. *Change* shows the difference for numbers, *changed* for other values, and
    *only before* / *only after* for items in one side only. A status change counts as a change.
  - **Changed only** hides equal values. ⌘C copies the rows as a table. **Delete snapshot** removes the *Before* one.
- Snapshots are JSON files in the `snapshots` folder of the settings folder (Help ▸ Show Settings Folder).

## Writing values

The app writes to a device only when you ask it to.

- **Where:** right-click a variable in Attributes (on the *Value* row) or one or more rows in Watch ▸ **Write value…**.
- **Format:** type the value as text; it is converted to the variable's data type. Arrays are comma-separated,
  optionally in brackets: `[1, 2, 3]`.
- **Per protocol:**
  - **OPC UA:** writes the Value attribute. The server decides whether the variable is writable (AccessLevel).
  - **EtherNet/IP:** atomics, atomic arrays and STRINGs. Tags the controller program owns may be rejected
    (`BadNotWritable`).
  - **MQTT:** a topic is republished with the same payload kind, retain flag and properties; a JSON field republishes
    the last document with the field changed; a Sparkplug B metric is sent as a command (NCMD/DCMD).
- Several Watch rows get the same value; failures are listed per item.

## Calling methods (OPC UA)

Double-click a method in the address space, or right-click ▸ **Call method…**. The form lists each input argument
with its name, data type and description; type the values as text (like *Write value*: arrays comma-separated,
`[1, 2, 3]`) and press **Call** (Enter). The outputs appear below the arguments; the form stays open to call again.

- The method is called on the object it sits under in the tree.
- A value that doesn't fit its type, a missing argument or an error from the server shows in red at the bottom.
- In the custom test server, *Custom ▸ Methods* has `Add`, `Greet` and `Stats` (an array argument and three outputs).

## History (OPC UA)

Many OPC UA servers store past values. Right-click one or more variables in the address space or rows in Watch ▸
**Show history ▸ Last 15 minutes … Last 7 days**. The values open in the recording viewer: a table and a trend
chart, oldest first, timed by their source timestamp.

- At most 20 000 values per item (the oldest); the status bar says when an item had more.
- A variable without stored history shows a message instead. Whether a server stores history is visible in
  Attributes: *Historizing* and *AccessLevel* (HistoryRead).
- In the custom test server, *Custom ▸ History* has Temperature, Pressure and Running with two hours of history.

## Events & Alarms (OPC UA)

Connection ▸ **Events & Alarms…** (⌥⌘A) opens a live window for the whole server. To narrow it to one area or
source, right-click it in the address space ▸ **Show events & alarms**. Each window is its own subscription; closing it
(or disconnecting) ends it.

- **Alarms tab:** alarms the server keeps (active or not yet acknowledged), one row each, most severe first, updated
  live. Alarms that are already active when the window opens appear at once.
  - **Acknowledge:** click an alarm row that says *Unacked*, optionally type a comment, then click **Acknowledge** (or
    press A). The row changes to *Acked*, or disappears once the alarm is also inactive.
  - The server matches the acknowledgement by the alarm's event id. Some servers send alarms without one (opc-plc,
    the test server, does); they refuse it, and the status bar says why.
- **Events tab:** every event and alarm change, newest first: time, severity, source, type, message and alarm state.
  - *Minimum severity* hides less severe events (OPC UA severity is 1–1000: high ≥ 700, medium ≥ 400).
  - **Pause** (Space) keeps the list still; **Clear** (⌘K) empties it. ⌘C copies the selected rows as a table.
  - The window keeps the latest 5 000 events.
- In opc-plc (the test server), the alarms sit under *Objects ▸ Server* (e.g. *Green ▸ East ▸ Blue ▸ WestTank*).

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
| OPC UA: `BadCertificateUntrusted` | Answer the trust prompt, or copy the certificate into `pki/trusted/certs` in the certificate folder |
| OPC UA structure shows as bytes | The server does not publish its type definitions; the raw value is still shown |
| EtherNet/IP: no tags | Only Logix (ControlLogix/CompactLogix) lists tags; check the path (`/1,0` = backplane 1, slot 0) |
| MQTT: empty tree | Nothing has been published yet on the filter (only retained messages appear at once); check the topic filter in the URL |
| MQTT: `alias 101` instead of names | The Sparkplug device's BIRTH was published before connecting; names appear with the next birth |
| MQTT: values stop, metrics Bad | The Sparkplug node or device sent a DEATH, or the broker connection is reconnecting (status bar) |
| Something failed | The error bar shows it; details are in `logs/opcuabrowser.log` in the data folder |

For local test servers (OPC UA, Logix, MQTT with Sparkplug B and CloudEvents) see [simulators.md](simulators.md).

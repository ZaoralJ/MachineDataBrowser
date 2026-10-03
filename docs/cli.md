# Command line (`mdbrowser`)

`mdbrowser` does the browsing and monitoring of the app from a terminal or a script: list endpoints, browse the
address space, read values, stream live values, and run the watch list of a session saved in the app. It speaks the
same protocols as the app: **OPC UA**, **EtherNet/IP (Logix)** and **MQTT** (Sparkplug B, CloudEvents).

## Install

```sh
brew install zaoralj/tap/mdbrowser        # macOS and Linux
```

Or download `mdbrowser-<version>-<os>-<arch>.tar.gz` from the
[releases](https://github.com/ZaoralJ/MachineDataBrowser/releases) and put `mdbrowser` on your `PATH`. It is a single
self-contained file; no .NET installation is needed.

## Commands

| Command | Does |
|---|---|
| `mdbrowser endpoints <url>` | the endpoints of an OPC UA server: security mode, policy, logins, certificate thumbprint |
| `mdbrowser browse <url> [node] [--depth N]` | the address space below a node (default: the root) |
| `mdbrowser read <url> <node>... [-R]` | the current value and data type of one or more variables |
| `mdbrowser monitor <url> <node>... [-R]` | live values until Ctrl+C, `--duration` or `--count` |
| `mdbrowser run <session>` | the watch list of a session file saved by the app (`.mdbsession`), each item at its refresh time |
| `mdbrowser mcp --endpoint <url>...` | an MCP server for AI agents, read-only; see [MCP server](mcp.md) |

`mdbrowser <command> --help` lists every option.

```sh
mdbrowser endpoints opc.tcp://plc:4840
mdbrowser browse opc.tcp://plc:4840 /Objects/Line1 --depth 2
mdbrowser read eip://10.0.0.5/1,0 "/Controller Tags/Motor.Speed"
mdbrowser monitor mqtt://broker/plant/# /Topics/plant/line1/status/speed --duration 10m
mdbrowser monitor opc.tcp://plc:4840 /Objects/Line1 --recursive --depth 3
mdbrowser run line1.mdbsession --format csv > shift.csv
```

### Nodes

A node is either a **path** of display names from the root, starting with `/`, or an **id**:

- `/Objects/OpcPlc/Telemetry/Basic/StepUp`, `/Controller Tags/Motor.Speed`, `/Topics/plant/line1/status`,
  `"/Sparkplug B/Plant1/Edge1/Press1/Temperature"` (quote paths with spaces);
- `ns=3;s=StepUp` or the portable `nsu=http://…;s=StepUp` (OPC UA), a tag name (Logix), an MQTT id as the app shows it.

`browse` prints full paths, so its output can be passed to `read` and `monitor` as it is.

### Whole folders (`--recursive`)

With `-R` / `--recursive`, `read` and `monitor` expand each folder, object or Logix structure to every variable below
it, like *Monitor folder* in the app:

| Option | |
|---|---|
| `--depth N` | levels below each node (default 10) |
| `--max-items N` | at most this many variables in total (default 500); a note on stderr says when the limit was hit |

- Items are named by their path below the node you passed (`Basic/StepUp`, `Station/Name`), so equal names in
  different folders stay apart.
- A variable you pass directly is read or monitored itself. OPC UA properties of variables (EURange, EngineeringUnits)
  are left out.
- MQTT: a topic with a JSON payload expands to its fields (`status/speed`, `status/temperature/bearing`).
- Nothing found within `--depth` is an error (exit 1).

### Connection options

| Option | |
|---|---|
| `-u, --user` / `-p, --password` | login (OPC UA, MQTT); the password can also come from `MDBROWSER_PASSWORD` |
| `--secure` | OPC UA: the most secure endpoint the server offers |
| `--trust-all` | accept untrusted server certificates; for lab networks only |

`mdbrowser` shares the app's data folder: certificates trusted in the app are trusted here, and the OPC UA client
identity is the same, so a server that trusts the app trusts the command line too. `run` takes the endpoint, login
name and options from the session; passwords are never saved, so pass `--password` or set `MDBROWSER_PASSWORD`.

### Output

| `--format` | In a terminal | Redirected (pipe, file) |
|---|---|---|
| `text` (default) | tables, a tree for `browse`, a live table for `monitor`/`run` that updates in place, coloured status | aligned plain text, one line per update |
| `json` | `read`/`browse`/`endpoints`: a JSON array; `monitor`/`run`: one JSON object per line, values keep their type | same |
| `csv` | a header line, then rows | same |

```sh
mdbrowser read opc.tcp://plc:4840 /Objects/Line1/Speed -f json | jq '.[0].value'
mdbrowser monitor opc.tcp://plc:4840 /Objects/Line1/Speed -f json | jq -c 'select(.status != "Good")'
```

### Stopping and exit codes

`monitor` and `run` stop on Ctrl+C (or SIGTERM), after `--duration` (`30s`, `5m`, `8h`, `1d`, `500ms` or `hh:mm:ss`) or
after `--count` updates, and close the connection properly.

| Exit code | |
|---|---|
| `0` | success, or a stream that ended normally |
| `1` | an error (one line on stderr, `mdbrowser: …`), or a `read` where a value could not be read |
| `130` | cancelled before the command finished (e.g. Ctrl+C during `read`) |

Items that can't be monitored are reported on stderr and the rest continue.

# MCP server for AI agents

`mdbrowser mcp` runs an [MCP](https://modelcontextprotocol.io) server, so AI agents (GitHub Copilot, Claude, opencode,
Cursor, …) can explore and read machines: find a signal, read values, check how they behave over a few seconds. They
can also read [recordings in SQLite files](user-manual.md#recording-to-sqlite): what was recorded, statistics, and
samples over any time range. It is part of the [command line](cli.md) and speaks OPC UA, EtherNet/IP (Logix) and MQTT.

**Read-only by default:** without `--allow-writes` no tool can write values or call methods, and none ever
acknowledges alarms. With it, every change is confirmed by you first (see [Changes](#changes-allow-writes)).

## Setup

Install the command line (`brew install zaoralj/tap/mdbrowser`), then add the server to your agent. Every machine the
agent may use is listed with `--endpoint` (repeat it) or `--session` (a session saved in the app, with its options);
recordings with `--recording file.db` (repeat it) or `--recordings-dir folder` (the SQLite files in it). Machines,
recordings or both: the agent only gets the tools for what is configured.

| Option | Lets the agent |
|---|---|
| `--endpoint url`, `--session file` | use these machines, with their login and trust options |
| `--allow "opc.tcp://10.0.5.*"` (repeatable) | connect to endpoints it names that match the pattern (`*`, `?`), **without** credentials or auto-trust |
| `--allow-any` | connect to any endpoint it names, without credentials (lab use) |
| `--recording file.db`, `--recordings-dir folder` | read these recordings |
| `--allow-recording folder` | start background recordings into that folder (and read them) |
| `--allow-writes` | write values and call methods on `--endpoint` / `--session` machines, each change confirmed by you |

=== "VS Code / GitHub Copilot"

    `.vscode/mcp.json`:

    ```json
    {
      "servers": {
        "machine-data": {
          "type": "stdio",
          "command": "mdbrowser",
          "args": ["mcp", "--endpoint", "opc.tcp://plc-01:4840", "--endpoint", "eip://10.0.0.5/1,0"]
        }
      }
    }
    ```

=== "Claude Code"

    ```sh
    claude mcp add machine-data -- mdbrowser mcp --endpoint opc.tcp://plc-01:4840
    ```

=== "opencode"

    `opencode.json`:

    ```json
    {
      "mcp": {
        "machine-data": {
          "type": "local",
          "command": ["mdbrowser", "mcp", "--session", "/Users/me/line1.mdbsession"]
        }
      }
    }
    ```

=== "Claude Desktop / others"

    ```json
    {
      "mcpServers": {
        "machine-data": {
          "command": "mdbrowser",
          "args": ["mcp", "--endpoint", "mqtt://broker:1883"],
          "env": { "MDBROWSER_PASSWORD": "…" }
        }
      }
    }
    ```

Logins: `--user` with the password in `MDBROWSER_PASSWORD` (set it in the agent's `env`, not in the arguments).
`--secure` and `--trust-all` work as on the command line; certificates trusted in the app are trusted here.

## Tools

| Tool | Gives the agent |
|---|---|
| `list_endpoints` | the configured machines, their protocol and connection state |
| `browse` | the address space below a node, a few levels at a time: path, class (Object/Variable/Method), id |
| `search` | nodes whose name or id matches (`Temp*`, `Motor?` or plain text), with their paths |
| `read` | current values with data type; `recursive` expands a folder or structure to every variable below it |
| `sample` | watches values for 1–300 s and summarises each: samples, changes, first/last value, min/max/mean, statuses |
| `attributes` | all attributes of a node (data type, description, access level, …) and its engineering unit |
| `history` | what an OPC UA server stored over a time range, raw or per `bucketSeconds` (count, min, max, average, not Good) |
| `alarms` | the server's current alarms, most severe first: source, name, severity, active, acknowledged, message |
| `events` | events and alarm changes collected for 1–300 s, newest first, optionally only above a severity |
| `diagnostics` | connection health: state; for OPC UA security, keep-alive, reconnects, server state and clock offset, subscriptions |
| `generate_code` | C# records or classes mirroring a structure (like *Copy as C#* in the app), or a JSON snapshot of its values |
| `wait_for` | waits up to 10 min until a value meets a condition (`> 80`, `== Run`, `contains Error`, `changes`) and says when |
| `start_recording`, `stop_recording`, `active_recordings` | with `--allow-recording`: records items in the background into a SQLite file (≤ 24 h, ≤ 5 at a time, ≤ 500 items), to analyse with the recording tools |

Nodes are the same paths and ids as on the command line (`/Objects/Line1/Speed`, `ns=3;s=Speed`,
`Program:Main.Speed`), and paths from `browse` and `search` can be passed on as they are. Values keep their type
(numbers, booleans, arrays, structures as objects).

`sample` exists because an agent can't watch a live stream; a summary answers questions like *is it moving*, *how much
does it fluctuate*, *did it go bad*.

### Recordings

```sh
mdbrowser mcp --recordings-dir ~/Recordings                              # recordings only
mdbrowser mcp --endpoint opc.tcp://plc-01:4840 --recording ~/plant.db    # machines and recordings
```

| Tool | Gives the agent |
|---|---|
| `list_recordings` | the recording files it may read and the recordings in each: name, endpoint, start and stop, items, samples |
| `recording_items` | per item: samples, first and last time, min/max/average, last value and status |
| `recording_samples` | samples of chosen items over a time range (ISO 8601, UTC), raw (up to 1000) or, with `bucketSeconds`, per time bucket: count, min, max, average and samples not Good |

Buckets are how an agent looks at days of data: a week per hour is 168 rows per item. Files are opened read-only and
can still be recording.

### Resources and prompts

Besides tools, the server offers **resources** an agent (or you, in clients that show them) can read directly, and
**prompts**: ready-made workflows to pick in the agent.

| Resource | |
|---|---|
| `mdbrowser://endpoints` | the machines, their state, allowed patterns and the session resources |
| `mdbrowser://sessions/{name}` | a session file given with `--session`: endpoint, options and watch list (never a password) |
| `mdbrowser://recordings` | the recording files and the recordings in each |

| Prompt | |
|---|---|
| `diagnose_machine` (endpoint, area) | connection, alarms, suspicious values and how they move: likely causes first, nothing changed |
| `check_alarms` (endpoint) | the current alarms explained, most severe first, with what to check on site |
| `summarize_recording` (file, item) | ranges, trends and anomalies of a recording, zooming in on unusual periods |

### Changes (`--allow-writes`)

| Tool | |
|---|---|
| `write` | writes a value (text converted to the variable's type) and returns it before and after |
| `call_method` | calls an OPC UA method on its object with the given inputs and returns the outputs |

- **You confirm every change.** The server asks through MCP *elicitation*: your agent shows a dialog like *"Write 42.5 to
  Setpoint (ns=3;s=Setpoint) on opc.tcp://plc-01:4840? It is 40 now."* and nothing happens unless you confirm.
  Clients that can't show such a dialog are refused; `--skip-write-confirmation` lets them through, relying on the
  client's own tool approval only.
- **Only configured machines:** endpoints named under `--allow` patterns are never changed, and sessions marked
  *Read-Only* in the app are refused.
- The tools are marked *destructive*, so clients show them as such.

## Limits and safety

- **Only listed machines.** A tool call naming any other endpoint is refused; with one machine configured the agent
  doesn't need to name it.
- **Read-only.** All tools are marked read-only for the agent, and nothing in them writes.
- **Bounded results:** `browse` returns at most 500 items, `read` 200 values, `sample` 100 items for at most 300 s,
  `history` 1000 raw values in total (20 000 per item for buckets), `events` 500; results say when they were truncated.
- **Events and alarms are only read:** nothing is acknowledged.
- **Named endpoints** (`--allow`, `--allow-any`) connect without any login or automatic certificate trust; logins and
  `--trust-all` apply only to `--endpoint` / `--session`.
- **Background recordings** write only into the `--allow-recording` folder (the name becomes a safe file name there),
  stop by themselves after their duration, and are stopped when the server exits.
- **Connections** are opened on first use and kept for later calls, so a server sees one session, not one per question.
- **No credentials pass through tools:** logins come from the server's own options and environment.
- **Only listed recording files,** opened read-only. A file outside `--recording` / `--recordings-dir` is refused, and
  there is no free SQL: SQLite could otherwise attach and read other database files on the disk.

## Example questions

- *"What machines can you see, and what's under Line 1?"*
- *"Find the motor temperatures on plc-01 and tell me which is the hottest."*
- *"Watch the line speed for 30 seconds. Is it stable?"*
- *"Read all recipe values on the Logix controller and summarise them."*
- *"Which signals under Boilers are not Good right now?"*
- *"Which alarms are active on plc-01, and what does the most severe one mean?"*
- *"What did the boiler temperature do overnight? Hourly averages, please."*
- *"Values look frozen. Is the connection to the PLC healthy?"*
- *"Tell me when line 1 changes to Run (wait up to 10 minutes)."*
- *"Record the press for an hour, then tell me how often the pressure went above 4 bar."*
- *"In last night's recording, when was the speed below 1000, and for how long?"*
- *"Compare the average temperature per hour between Monday and Tuesday."*

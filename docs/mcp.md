# MCP server for AI agents

`mdbrowser mcp` runs an [MCP](https://modelcontextprotocol.io) server, so AI agents (GitHub Copilot, Claude, opencode,
Cursor, …) can explore and read machines: find a signal, read values, check how they behave over a few seconds. It is
part of the [command line](cli.md) and speaks OPC UA, EtherNet/IP (Logix) and MQTT.

**Read-only:** the tools can't write values, call methods or acknowledge alarms.

## Setup

Install the command line (`brew install zaoralj/tap/mdbrowser`), then add the server to your agent. Every machine the
agent may use is listed with `--endpoint` (repeat it) or `--session` (a session saved in the app, with its options).

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

Nodes are the same paths and ids as on the command line (`/Objects/Line1/Speed`, `ns=3;s=Speed`,
`Program:Main.Speed`), and paths from `browse` and `search` can be passed on as they are. Values keep their type
(numbers, booleans, arrays, structures as objects).

`sample` exists because an agent can't watch a live stream; a summary answers questions like *is it moving*, *how much
does it fluctuate*, *did it go bad*.

## Limits and safety

- **Only listed machines.** A tool call naming any other endpoint is refused; with one machine configured the agent
  doesn't need to name it.
- **Read-only.** All tools are marked read-only for the agent, and nothing in them writes.
- **Bounded results:** `browse` returns at most 500 items, `read` 200 values, `sample` 100 items for at most 300 s;
  results say when they were truncated.
- **Connections** are opened on first use and kept for later calls, so a server sees one session, not one per question.
- **No credentials pass through tools:** logins come from the server's own options and environment.

## Example questions

- *"What machines can you see, and what's under Line 1?"*
- *"Find the motor temperatures on plc-01 and tell me which is the hottest."*
- *"Watch the line speed for 30 seconds. Is it stable?"*
- *"Read all recipe values on the Logix controller and summarise them."*
- *"Which signals under Boilers are not Good right now?"*

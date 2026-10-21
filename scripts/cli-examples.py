# /// script
# requires-python = ">=3.11"
# ///
"""Writes docs/cli-examples.md: every `mdbrowser` command run against the bundled simulators, with its real output.

Needs all simulators (`just all`) and builds the CLI first (see `just docs-cli-examples`).
Usage: uv run scripts/cli-examples.py <mdbrowser> <output.md>
Writes go to simulator values that are restored right after, so the shared containers are left as they were.
"""

import os
import shlex
import subprocess
import sys
import tempfile
from pathlib import Path

OPCUA = "opc.tcp://127.0.0.1:50000"
CUSTOM = "opc.tcp://127.0.0.1:4841"
LOGIX = "eip://127.0.0.1:44818/1,0"
MQTT = "mqtt://127.0.0.1:1883"
PRESS = "/Topics/plant/hall1/press1"

# (heading, text, command, lines to keep). The command is shown as typed; {M} is the mdbrowser binary.
EXAMPLES: list[tuple[str, str, str, int]] = [
    ("Endpoints of an OPC UA server", "Security modes, policies and logins, most secure first.",
     f"mdbrowser endpoints {OPCUA}", 12),
    ("Browse OPC UA", "One level below a node; paths can be passed to `read` and `monitor` as they are.",
     f"mdbrowser browse {CUSTOM} /Objects/Custom", 20),
    ("Browse deeper", "`--depth` walks further down.",
     f"mdbrowser browse {CUSTOM} /Objects/Custom/Fast --depth 2", 12),
    ("Browse Logix (EtherNet/IP)", "Controller tags and programs of a ControlLogix.",
     f'mdbrowser browse {LOGIX} "/Controller Tags"', 12),
    ("Browse MQTT", "Topics seen on the broker; JSON payloads open into their fields.",
     f"mdbrowser browse {MQTT} /Topics/plant --depth 3", 14),
    ("Read values", "Value and data type of one or more variables.",
     f"mdbrowser read {CUSTOM} /Objects/Custom/DataTypes/Int32 /Objects/Custom/DataTypes/Double "
     "/Objects/Custom/DataTypes/String", 10),
    ("Read a Logix array", "",
     f'mdbrowser read {LOGIX} "/Controller Tags/Arrays/Lreals"', 10),
    ("Read a folder as JSON", "`-R` expands a folder to every variable below it; `-f json` keeps the types.",
     f"mdbrowser read {CUSTOM} /Objects/Custom/DataTypes -R -f json", 26),
    ("Read an MQTT topic with a JSON payload", "The whole payload of the topic, as received.",
     f"mdbrowser read {MQTT} /Topics/machines/m1/status", 4),
    ("Read properties from the JSON on a topic",
     "Each property of a JSON payload is a node below its topic, nested objects too; it is read with its own type.",
     f"mdbrowser read {MQTT} /Topics/machines/m1/status/speed /Topics/machines/m1/status/state "
     "/Topics/machines/m1/status/temperature/motor", 6),
    ("Read a JSON property by its id", "`topic#/path` is the same property as the id the app and `browse` show.",
     f'mdbrowser read {MQTT} "t:machines/m1/status#/temperature/bearing"', 4),
    ("Monitor live values", "Every 500 ms for 2 seconds (or until Ctrl+C without `--duration`).",
     f"mdbrowser monitor {CUSTOM} /Objects/Custom/Fast/Every10ms/Counter /Objects/Custom/Fast/Every10ms/Sine "
     "-r 500 --duration 2s", 12),
    ("Monitor MQTT and record", "A whole topic tree, recorded to SQLite as well.",
     f"mdbrowser monitor {MQTT} {PRESS} -R --duration 2s --record press1.db", 12),
    ("Monitor Sparkplug B metrics",
     "Sparkplug B metrics by their id, `m:group|edge node|device|metric`. Births name the metrics; until the next birth "
     "(this simulator re-sends them every 15 s, a real edge node on start and on a rebirth request) values arrive "
     "under their alias, so ids by name are the reliable way from the command line.",
     f'mdbrowser monitor {MQTT} "m:Plant1|Edge1|Press1|Temperature" "m:Plant1|Edge1|Press1|Pressure" '
     "-r 1000 --duration 20s", 12),
    ("Write a value", "Shows the value before and after; without `--yes` it asks first.",
     f"mdbrowser write {CUSTOM} /Objects/Custom/DataTypes/Int16 -321 --yes", 6),
    ("Create a session for the app", "",
     f"mdbrowser session create press1.mdbsession {MQTT} {PRESS}/temperature {PRESS}/pressure", 4),
    ("Add to a session", "",
     f"mdbrowser session add press1.mdbsession {PRESS}/state /Topics/plant/hall1/press2/temperature", 4),
    ("Remove from a session", "Offline: the file is changed without connecting.",
     "mdbrowser session remove press1.mdbsession /Topics/plant/hall1/press2/temperature", 4),
    ("Run a session", "Its watch list, each item at its refresh time.",
     "mdbrowser run press1.mdbsession --duration 2s", 10),
    ("Run a session as CSV", "",
     "mdbrowser run press1.mdbsession --duration 3s -f csv", 8),
]

# Restores what the examples write, so the shared simulators stay as they were.
CLEANUP = [f"mdbrowser write {CUSTOM} /Objects/Custom/DataTypes/Int16 -1234 --yes"]


def run(mdbrowser: str, command: str, cwd: str, env: dict[str, str]) -> str:
    argv = shlex.split(command)
    argv[0] = mdbrowser
    result = subprocess.run(argv, cwd=cwd, env=env, capture_output=True, text=True, timeout=120)
    output = (result.stdout + result.stderr).rstrip()
    if result.returncode != 0:
        sys.exit(f"`{command}` failed ({result.returncode}):\n{output}")
    return output


def main() -> None:
    mdbrowser, output = os.path.abspath(sys.argv[1]), sys.argv[2]
    cwd = tempfile.mkdtemp(prefix="mdb-cli-examples-")
    env = dict(os.environ, NO_COLOR="1", COLUMNS="120", TERM="dumb",
               MACHINEDATABROWSER_DATA_DIR=tempfile.mkdtemp(prefix="mdb-docs-"))
    lines = [
        "# Command line examples",
        "",
        "<!-- Generated by `just docs-cli-examples` from the bundled simulators; do not edit. -->",
        "",
        "Every command of [`mdbrowser`](cli.md) run against the [test servers](simulators.md) (`just all`), with",
        "the output it printed. Long output is shortened (`…`). Live values and timestamps differ on every run.",
        "",
    ]
    try:
        for heading, text, command, keep in EXAMPLES:
            out = run(mdbrowser, command, cwd, env).splitlines()
            if len(out) > keep:
                out = out[:keep] + [f"… ({len(out) - keep} more lines)"]
            lines += [f"## {heading}", ""]
            if text:
                lines += [text, ""]
            lines += ["```sh", command, "```", "", "``` { .text .no-copy }", *out, "```", ""]
    finally:
        for command in CLEANUP:
            run(mdbrowser, command, cwd, env)

    lines += ["The full-screen browser (`mdbrowser tui`) is shown in [Command line](cli.md#full-screen-browser-tui);",
              "the MCP server in [MCP server for AI agents](mcp.md).", ""]
    Path(output).write_text("\n".join(lines), encoding="utf-8")


if __name__ == "__main__":
    main()

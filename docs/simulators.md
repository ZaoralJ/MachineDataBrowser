# Test servers (simulators)

Eight containers under `simulators/` cover local development, manual/user testing and the integration tests.
Start them with [`just`](https://github.com/casey/just) from the repository root:

```sh
just cip            # Logix / EtherNet/IP   -> eip://127.0.0.1:44818/1,0
just pccc           # SLC 500 / MicroLogix / PLC-5 -> pccc://127.0.0.1:44819?plc=slc500
just opcua          # opc-plc               -> opc.tcp://127.0.0.1:50000
just opcua-custom   # custom types (asyncua) -> opc.tcp://127.0.0.1:4841/
just mqtt           # MQTT broker + traffic  -> mqtt://127.0.0.1:1883 (ws://127.0.0.1:9001)
just modbus         # Modbus TCP device      -> modbus://127.0.0.1:5020/1
just s7             # Siemens S7 (snap7)     -> s7://127.0.0.1:102/0,1, tag list simulators/s7/tags.csv
just mtconnect      # MTConnect agent        -> mtconnect://127.0.0.1:5050
just all            # all eight in the background (replaces running ones); `just stop` removes them
just logs cip       # follow a background container's log
just cip-debug      # log every CIP request (service, path, sizes)
just pccc-debug     # log every PCCC request
just test-sim       # Core tests against the simulators
```

Recipe parameters are positional, e.g. `just cip 44900 20` (port 44900, 20 ms base tick).

## Example sessions

Each simulator has a ready-to-open session with representative live values in its watch list. Start the simulators with
`just all`, then open a session from **File ▸ Open** in the app or run it in the terminal:

```sh
mdbrowser tui simulators/cip/simulator.mdbsession
```

| Simulator | Example session |
|---|---|
| Logix / EtherNet/IP | [`simulators/cip/simulator.mdbsession`](https://github.com/ZaoralJ/MachineDataBrowser/blob/main/simulators/cip/simulator.mdbsession) |
| PCCC | [`simulators/pccc/simulator.mdbsession`](https://github.com/ZaoralJ/MachineDataBrowser/blob/main/simulators/pccc/simulator.mdbsession) |
| opc-plc | [`simulators/opcua/simulator.mdbsession`](https://github.com/ZaoralJ/MachineDataBrowser/blob/main/simulators/opcua/simulator.mdbsession) |
| Custom OPC UA types | [`simulators/opcua-custom/simulator.mdbsession`](https://github.com/ZaoralJ/MachineDataBrowser/blob/main/simulators/opcua-custom/simulator.mdbsession) |
| MQTT | [`simulators/mqtt/simulator.mdbsession`](https://github.com/ZaoralJ/MachineDataBrowser/blob/main/simulators/mqtt/simulator.mdbsession) |
| Modbus TCP | [`simulators/modbus/simulator.mdbsession`](https://github.com/ZaoralJ/MachineDataBrowser/blob/main/simulators/modbus/simulator.mdbsession) |
| Siemens S7 | [`simulators/s7/simulator.mdbsession`](https://github.com/ZaoralJ/MachineDataBrowser/blob/main/simulators/s7/simulator.mdbsession) |
| MTConnect | [`simulators/mtconnect/simulator.mdbsession`](https://github.com/ZaoralJ/MachineDataBrowser/blob/main/simulators/mtconnect/simulator.mdbsession) |

The Modbus and S7 sessions also load their adjacent register map/tag list by a relative path.

## Pausing the simulation

Write `true` to the pause tag to freeze every changing value, `false` to continue. The simulation clock stops
while paused, so values continue from where they were rather than jumping. Each simulator logs the change.

| Simulator | Pause tag |
|---|---|
| Logix | `PauseSimulation` (controller tag, BOOL) |
| PCCC | `B3:0/0` (bit 0 of `B3:0`) |
| Custom types | `/Objects/Custom/PauseSimulation` |
| MQTT | topic `simulator/PauseSimulation` (retained, payload `true`/`false`); the app writes it like any topic |
| Modbus | coil `co:0` (`/Tags/Simulator/PauseSimulation` with the simulator's map) |
| S7 | `M0.0` (symbol `PauseSimulation` in `tags.csv`) |
| MTConnect | `curl -X PUT 'http://127.0.0.1:5050/simulator/pause?value=true'` (MTConnect is read-only); data item `agent_pause` (*Agent ▸ PauseSimulation*) shows the state |
| opc-plc | no single tag; call `Objects/OpcPlc/Methods/StopUpdateFastNodes` / `StopUpdateSlowNodes` (and `Start…`) |

```sh
mdbrowser write eip://127.0.0.1:44818/1,0 PauseSimulation true --yes
mdbrowser write opc.tcp://127.0.0.1:4841 /Objects/Custom/PauseSimulation false --yes
mdbrowser write modbus://127.0.0.1:5020/1 co:0 true --yes
mdbrowser write "pccc://127.0.0.1:44819?plc=slc500" B3:0/0 true --yes
mdbrowser write s7://127.0.0.1/0,1 M0.0 true --yes
```

opc-plc is Microsoft's image: its methods pause only the Fast and Slow telemetry nodes. Basic, Anomaly, alarms and
boilers keep running.

## Logix simulator (`simulators/cip`)

A pure-Python (no packages) EtherNet/IP server that answers like a ControlLogix controller. Every tag is stored
as raw bytes in Logix memory layout, so tag reads, member and element reads, whole-struct and array reads,
fragmented reads and writes are served from the same data.

It implements `@tags` (controller and program scope, paged, any attribute list), `@udt/<id>` (template attributes
and definition), Read/Write Tag (plus fragmented variants), Read-Modify-Write, Multiple Service Packet, symbol
instance addressing, Forward Open (small and large), Unconnected Send and the Identity object. libplctag
(the browser) and pycomm3 both work against it; `ab_server` could not be used because it has no tag listing.

### Content

| Area | Tags |
|------|------|
| Atomics | `TestBool` … `TestLreal`, unsigned `TestUsint`/`TestUint`/`TestUdint`/`TestUlint`, bit strings `TestByte`/`TestWord`/`TestDword`/`TestLword` |
| Strings | `TestString` (STRING, 82), `TestString20` (STRING20), `TestString40` (STRING_40), `TestStringArray`, `TestString20Array` |
| Arrays | 1-D arrays of each type, `Matrix` DINT[4,5], `Cube` REAL[2,3,4], `Grid` SINGLE_T[3,3], `BigDintArray` DINT[5000], `BigRealArray` REAL[1500], `BigStructArray` SINGLE_T[1200] (more than the browser's 1000-element cap) |
| BOOL | packed BOOL members (`Flags.B0`…`B11` around a DINT, `BoolsOnly`), `TestBoolArray` BOOL[64] and BOOL[n] UDT members (stored as DWORD words; the template reports the logical BOOL count and the client unpacks bits) |
| UDTs | `Motor1`, `Motors` MOTOR_T[4], `Stations` STATION_T[6] (nested ROBOT_T, CONVEYOR_T, MOTOR_T[3], BOOL[32]), `Line`, `Plant` (5 levels, arrays of UDTs at several levels), `Types`/`Arrays` (every type), `Recipes` RECIPE_T[3] (1 kB each, fragmented reads) |
| Changing values | `HighSpeed.Axes[0..3]`, `HighSpeed.Cycle`, `HighSpeed.Vision`, `Stations[*].Robot`, `Program:Motion.*` at 10 / 20 / 50 ms; `Fast` 100 ms, `Medium` 1 s, `Slow` 10 s, `Heartbeat` 1 s and slow process values |
| Programs | `MainProgram`, `Motion`, `DATALOG`, `Packaging`, `Safety`, `Empty`, `Line01`…`Line20` |
| Hidden by the browser | `Local:1:I`, `Local:2:O` (module types with colons), `__HiddenCounter`, `SystemClock` (system flag), `Task:MainTask`, `Map:Local`, `Routine:MainRoutine` in each program |
| Paging | `Bulk_0001`…`Bulk_3000` push the controller `@tags` listing over many pages |

### Writing

Every tag accepts Write Tag. Values that are not animated keep whatever a client writes: the `Test*` atomics,
strings and arrays, `Types.*`, `Arrays.*`, `Setpoints`, `Recipe_Active`, `Recipes`, `Motor1`/`Motor2`, static
members such as `Stations[*].Name`, program tags such as `Program:Packaging.Mode`. Animated values (the
*Changing values* row) are owned by the simulated program: as on a real controller, a write succeeds and the next
update replaces it. While `PauseSimulation` is set, the written value stays until the simulation resumes; with
`CIP_SIM_FAST_TICK_MS=0` nothing is animated at all.

### Settings

| Variable | Default | Effect |
|----------|---------|--------|
| `CIP_SIM_PORT` | `44818` | TCP port |
| `CIP_SIM_FAST_TICK_MS` | `10` | base tick of the animations; `0` freezes all values |
| `CIP_SIM_BULK_TAGS` | `3000` | number of `Bulk_NNNN` tags |
| `CIP_SIM_PROGRAMS` | `20` | number of `Program:LineNN` scopes |
| `CIP_SIM_LOG` | `INFO` | `DEBUG` logs every request |
| `CIP_SIM_SEED` | random | seed for the random values |

Known simplifications: writes of whole structures are accepted as raw bytes, BOOL arrays are addressed by DWORD
index (`Bits[1]` is the second 32-bit word, as pycomm3 expects) and their wire reads/writes use `ceil(n/32)` DWORDs
(the browser uses the template's logical element count to unpack and update the BOOL bits while preserving padding),
there are no aliases, AOIs, produced/consumed tags or External Access settings (every tag is writable).

## PCCC simulator (`simulators/pccc`)

A pure-Python (no packages) EtherNet/IP server that answers PCCC like an SLC 500, MicroLogix or PLC-5. One data
table serves all three: SLC 500 / MicroLogix requests (typed logical read `0xA2`, write `0xAA`, masked write
`0xAB`) and PLC-5 requests (word range read `0x01`, write `0x00`, read-modify-write `0x26`) are told apart by
their function code, so `plc=slc500`, `plc=micrologix` and `plc=plc5` all connect to the same container. It runs on
port 44818 in the container and is mapped to **44819** on the host, next to the Logix simulator.

It implements RegisterSession, ListIdentity, SendRRData with Execute PCCC (`0x4B`) on the PCCC object (directly or
inside Unconnected Send) and the Identity object. Addresses use the standard encoding (one byte, or `0xFF` and a
16-bit number), sub-elements (`T4:0.ACC`) and PLC-5 level flags. Errors are PCCC extended statuses: "address
doesn't point to something usable" for a missing file or wrong file type, "file is wrong size" past its end.
libplctag's own `ab_server` was not used: it can't address sub-elements and serves one PLC type per process.

### Content

| File | Elements | Contents |
|------|---------:|----------|
| `O0`, `I1` | 8 | `I1:0` a walking bit (1 s), `O0:0` mirrors it |
| `S2` | 66 | `S:1` mode bits, `S:4` free-running clock (10 ms), `S:57` |
| `B3` | 32 | `B3:0/0` pauses the simulation; `B3:1` counts seconds in binary; `B3:2` = `0x5555`, `B3:3` all ones |
| `T4` | 10 | `T4:0` times to its preset (1 s) and restarts; `T4:1` done; presets `100 + 50 × n` |
| `C5` | 10 | `C5:0` counts up once a second to twice its preset; `C5:2.ACC` = 7; presets 50 |
| `R6` | 4 | `R6:0.POS` walks 0…`LEN` (10) once a second |
| `N7` | 100 | `N7:0` heartbeat (s), `N7:1` sine ±1000 (10 s), `N7:2` ramp 0…1000, `N7:3` random walk; `N7:10…` = `10 × n` |
| `F8` | 50 | `F8:0` temperature (sine, 30 s), `F8:1` ramp 0…100, `F8:2` noisy pressure; `F8:10…` = `1.5 × n` |
| `N9` | 256 | `N9:n` = `n`: a file of the SLC maximum length |
| `ST10` | 10 | strings `Hello PCCC`, `SLC 500`, `MicroLogix`, `PLC-5`, `Recipe A`, `Recipe B`, rest empty |
| `L11` | 20 | `L11:0` simulated milliseconds; `L11:n` = `100000 × n` |

`N9`, `ST10` and `L11` are not standard files, so name them in the endpoint to see them in the tree:
`pccc://127.0.0.1:44819?plc=slc500&files=N9,ST10,L11`.

### Writing

Every element accepts writes. Animated values (`N7:0…3`, `F8:0…2`, `L11:0`, `T4:0`, `C5:0`, `R6:0`, `I1:0`, `O0:0`,
`B3:1`, `S:4`) are replaced at the next tick unless the simulation is paused; everything else keeps what is written.

### Settings

| Variable | Default | Effect |
|----------|---------|--------|
| `PCCC_SIM_PORT` | `44818` | TCP port inside the container (`just pccc PORT` maps the host port) |
| `PCCC_SIM_TICK_MS` | `100` | animation tick; `0` freezes all values |
| `PCCC_SIM_LOG` | `INFO` | `DEBUG` logs every request |
| `PCCC_SIM_SEED` | — | random seed for the noisy values |

## opc-plc (`simulators/opcua`)

Microsoft's [opc-plc](https://github.com/Azure-Samples/iot-edge-opc-plc), pinned, with a development profile:

- `Objects/OpcPlc/Telemetry/Fast`: `OPCUA_SIM_FAST_NODES` (20) doubles changing every `OPCUA_SIM_FAST_MS` (10 ms).
- `Objects/OpcPlc/Telemetry/Basic` and `Anomaly`: the simulation cycle runs every `OPCUA_SIM_CYCLE_MS` (50 ms).
- `Objects/OpcPlc/Telemetry/Slow`: `OPCUA_SIM_SLOW_NODES` (10) counters, every second.
- `Objects/OpcPlc/Plant`: a writable folder tree from `nodesfile.json` with every built-in type and arrays. These
  values change only when a client writes them.
- Boilers (complex type, DI companion spec), alarms and conditions, simple events, stacklight, pumps, `ReferenceTest`.
- Alarms (`--alm`) live under *Objects ▸ Server* (e.g. *Green ▸ East ▸ Blue ▸ WestTank ▸ Gold*); simple events
  (`--ses`) are raised on the Server object. Its alarm notifications carry an all-zero EventId, so they cannot be
  acknowledged.
- Security None and anonymous allowed, certificates auto-accepted; users `admin`/`admin` and `user1`/`password`.
- Extra opc-plc arguments go after the image name: `docker run --rm -p 50000:50000 machinedatabrowser-opcua-simulator:dev --chaos`.

## Custom types server (`simulators/opcua-custom`)

An [asyncua](https://github.com/FreeOpcUa/opcua-asyncio) server for what opc-plc lacks. Everything is under
`Objects/Custom`, namespace `urn:machinedatabrowser:simulator:custom`:

| Folder | Content |
|--------|---------|
| `Machines` | `Machine1..3`, instances of the custom `MachineType` object type; `Status` is a `MachineStatus` structure (enum, nested `Vector3`/`ToolInfo`, arrays of Int32/String/`Vector3`) updated every 50 ms; `Speed` every 10 ms |
| `Structures` | `Position` (`Vector3`, 10 ms), `StatusHistory` (`MachineStatus[5]`), `QualityFull`/`QualityPartial` (optional fields set and unset), `SetpointNumeric`/`SetpointText` (union), `State` (enum), `Alarms` (OptionSet) |
| `DataTypes` | every built-in scalar type (writable), `Arrays` with empty, 10 000-element, `Matrix3x4` (ValueRank 2, changes every 20 ms) and `Cube2x3x4` |
| `Fast` | `Every10ms`, `Every20ms`, `Every50ms`: counter, sine, toggle, noise, timestamp |
| `EdgeCases` | special characters, a 3000-character NodeId, opaque and GUID NodeIds, null value, Bad and Uncertain status, a 100 kB string, a not-readable node, a folder 30 levels deep, a method |
| `Large` | `OPCUA_CUSTOM_LARGE` = `areas,lines,tags` (default `10,10,50`: 5000 variables) |
| `Flat` | `OPCUA_CUSTOM_FLAT` (default 10 000) variables in one folder, to exercise browse continuation |
| `Methods` | `Add(A, B) → Sum`, `Greet(Name, Times) → Greeting` (BadOutOfRange outside 0–10), `Stats(Values[]) → Min, Max, Mean` (BadInvalidArgument when empty) |
| `History` | `Temperature` (°C), `Pressure` (bar), `Running`: historized (HistoryRead), two hours prefilled every 10 s, then a new value every second; Temperature and Pressure have EngineeringUnits |

`OPCUA_CUSTOM_FAST_MS` (10) sets the base tick (`0` freezes values), `OPCUA_CUSTOM_PORT` (4841) the port and
`OPCUA_CUSTOM_HOST` (`localhost`) the host in the advertised endpoint URL. Startup takes 20-30 s with the
default address space. Security is None only.

## MQTT simulator (`simulators/mqtt`)

Mosquitto (anonymous; MQTT on 1883, MQTT over WebSocket on 9001) plus a Python publisher (`publisher.py`, MQTT 5):

| Topics | Content |
|--------|---------|
| `plant/hall1/press1/…`, `plant/hall1/press2/…` | plain values: numbers (100 ms – 1 s), booleans, text; `plant/hall1` also has its own retained payload |
| `fast/10ms/counter`, `fast/20ms/sine`, `fast/50ms/noise` | values changing every 10 / 20 / 50 ms |
| `machines/m1/status`, `machines/m2/status` | JSON every 200 ms: nested object, array, boolean, `null` |
| `config/line1`, `plant/info/…`, `deep/l1/…/l8/value`, `text/with spaces/and-üñíçødé`, `bulk/sensor/NNNN` | retained messages (visible right after connecting), a deep tree, special characters, many topics |
| `raw/blob`, `raw/int32`, `raw/float64` | raw binary payloads (non UTF-8 bytes, little-endian int32 / float64) |
| `events/press1/structured` | CloudEvents 1.0, structured mode: JSON envelope (`specversion`, `id`, `source`, `type`, `time`, `data`), content type `application/cloudevents+json` |
| `events/press1/binary` | CloudEvents 1.0, binary mode: JSON data as payload, attributes as MQTT 5 user properties |
| `spBv1.0/Plant1/…` | Sparkplug B: edge nodes `Edge1` (devices `Press1`, `Press2`) and `Edge2`; BIRTH with names and aliases, DATA with aliases only (Temperature and Motor/Speed every 50 ms, the rest every second); `Press2` sends DDEATH and is reborn 5 s later every `MQTT_SIM_DEATH_PERIOD_S`; `Edge1` has an NDEATH last will |

| Variable | Default | Effect |
|----------|---------|--------|
| `MQTT_SIM_FAST_MS` | `10` | base tick of the publisher |
| `MQTT_SIM_BULK` | `500` | number of `bulk/sensor/NNNN` topics |
| `MQTT_SIM_DEATH_PERIOD_S` | `30` | how often `Press2` dies (0 = never) |
| `MQTT_SIM_REBIRTH_S` | `15` | births are re-published periodically, so a browser connecting later learns metric names |

### Writing

"Write value…" publishes to the broker, so any topic can be written; values the publisher republishes are
overwritten at its next tick. Values that keep what is written:

- **Topics**: the retained ones published once, e.g. `plant/info/version`, `bulk/sensor/NNNN`, `deep/l1/…/l8/value`.
  The payload keeps its kind (number, boolean, text, JSON, hex for binary) and its retain flag.
- **JSON fields**: `config/line1` (`cycleMs`, `limits/max`, …), `acme/billund/moulding/lineN/shift` (`crew`,
  `supervisor`) and the `_meta` documents. The last document is republished with the one field changed.
- **Sparkplug B**: a write sends a DCMD (device) or NCMD (edge node). `Press1`/`Press2` apply writes to their static
  metrics `Config/Recipe` and `Types/Int8`, `Types/Int16`, `Types/UInt8`, `Types/UInt32` and report them in DDATA;
  animated metrics ignore commands. NCMD `Node Control/Rebirth` = true on `Edge1` re-sends its births.

## Modbus simulator (`simulators/modbus`)

A pure-Python (no packages) Modbus TCP server (`modbus_server.py`) on port 502 in the container (5020 on the host,
since ports below 1024 need root). It answers every unit id and supports functions 1–6, 15 and 16. Addresses beyond
the ranges below answer with exception 2 (illegal data address).

| Area | Addresses | Content |
|---|---|---|
| Coils | 0–99 | `0` PauseSimulation; `1`–`15` static (odd on); `16` toggles every 2 s; `17`–`99` free to write |
| Discrete inputs | 0–99 | `0` toggles every second; `1`–`15` static (multiples of 3 on) |
| Input registers | 0–99 | `0` counter; `2` float32 50 ± 10; `10` = 4242; `20` float32 −12.25 |
| Holding registers | 0–999 | `0` counter; `1` int16 sine; `2` float32 20 ± 5; static: `10` int16 −1234, `11` 54321, `20` float32 3.5, `22` float32 3.5 word-swapped, `30` uint32 123456789, `32` int32 −100000, `40` float64 e, `44` int64 −9007199254740993, `50` string "MDB-SIM" (8 registers); `100`–`999` free to write |

**Register map:** `simulators/modbus/simulator-map.csv` names all of these (Simulator, Inputs, Static, Scratch
folders, with units and descriptions). Import it in the app (⚙ ▸ Register map ▸ Import…, or Connection ▸ Import
Register Map…), pass it to the CLI with `--map`, or press `k` in the TUI. `simulators/modbus/simulator.mdbsession`
uses it (by relative path) with a watch list of the changing values:

```sh
just modbus
mdbrowser tui simulators/modbus/simulator.mdbsession
mdbrowser read modbus://127.0.0.1:5020/1 /Tags/Static -R --map simulators/modbus/simulator-map.csv
```

| Variable | Default | Effect |
|----------|---------|--------|
| `MODBUS_SIM_PORT` | `502` | port inside the container |
| `MODBUS_SIM_TICK_MS` | `100` | how often the changing values update |
| `MODBUS_SIM_LOG` | `INFO` | `DEBUG` logs every request |

## S7 simulator (`simulators/s7`)

A Siemens S7 server from [python-snap7](https://github.com/gijzelaerr/python-snap7) (pure Python) on ISO-on-TCP port
102, rack 0, slot 1. It answers like an S7 CPU with non-optimized data blocks: reads, writes (bit writes change only
that bit, which the stock snap7 server gets wrong), PDU negotiation and errors for unknown DBs or addresses out of
range. Values change every 50 ms (`S7_SIM_TICK_MS`).

**Tag map:** [`simulators/s7/tags.csv`](https://github.com/ZaoralJ/MachineDataBrowser/blob/main/simulators/s7/tags.csv)
is the symbol table for every address below, in the TIA Portal CSV layout (Name, Path, Data Type, Logical Address,
Comment). Import it in the app (Connection ▸ Import Register Map / Tag List…) or pass it with `--map`.
[`simulators/s7/simulator.mdbsession`](https://github.com/ZaoralJ/MachineDataBrowser/blob/main/simulators/s7/simulator.mdbsession)
is a session with the tag map and a watch list: File ▸ Open in the app, or `mdbrowser tui simulators/s7/simulator.mdbsession`.
Both are in the solution under *simulators/s7*.

| Area | Content |
|------|---------|
| DB1 *Machine* (64 bytes) | `Running`, `Fault` (10 s), `Blink` (every tick), `Speed` REAL, `Counter` DINT, `Temperature` REAL, `Pressure` LREAL, `Mode` INT, `Status` WORD, `Runtime` TIME; writable and static: `Name` STRING[20], `Setpoint` REAL, `Recipe` INT |
| DB2 *Arrays* (72 bytes) | `Reals` REAL[10] (sines), `Ints` INT[10] (counters), `Bools` BOOL[16] (walking bit), `Bytes` BYTE[8] |
| DB3 *DataTypes* (128 bytes) | one static value of every supported type, BOOL … LWORD, STRING, WSTRING, DATE, TIME, TIME_OF_DAY |
| Inputs (16 bytes) | `I0.0` toggles every second, `IB1` counter, `IW2` analog INT 0…27648 |
| Outputs (16 bytes) | `Q0.0` mirrors `Machine.Running`, `QB1` mirrors `IB1` |
| Memory (64 bytes) | `M0.0` PauseSimulation, `MW2` tick counter, `MD4` uptime REAL; the rest is free for write tests |

```sh
mdbrowser browse s7://127.0.0.1/0,1 --map simulators/s7/tags.csv /Tags/Machine
mdbrowser read s7://127.0.0.1/0,1 DB3.DBW4:INT DB3.DBB28:STRING[10]
```

## MTConnect agent simulator (`simulators/mtconnect`)

A pure-Python (no packages) MTConnect agent: it answers `/probe`, `/current` and `/sample` (MTConnect 2.2 XML,
including `from`, `count` and the `path` filter forms clients use: `//DataItem[@id="a" or @id="b"]`, `@type`,
`@name`, `@category`, and `//Components/Axes`), per device too (`/VMC/current`), with a ring buffer of observations
and `MTConnectError` answers (`OUT_OF_RANGE`, `NO_DEVICE`, `INVALID_XPATH`). The real
[cppagent](https://github.com/mtconnect/cppagent) works the same for the browser; this one has no adapter to set up.

| Device | Content |
|--------|---------|
| `Agent` | `AVAILABILITY`, `DEVICE_ADDED` and `PauseSimulation` (`agent_pause`), like an MTConnect 2.x agent device |
| `VMC` | 3-axis mill: *Axes ▸ base* with `X`/`Y`/`Z` (position actual and commanded every 100 ms, load, temperature, travel condition) and spindle `S` (speed, override, load, mode, a 10-sample `DISPLACEMENT` time series, condition); *Controller ▸ path* (execution, program, line, block, part count, tool, feedrate, 3D `PATH_POSITION`, `WORK_OFFSET` data set, conditions); *Systems* (coolant level and a low-level warning, power, powered time); door; `spare` sensor that is always `UNAVAILABLE` |
| `Lathe` | 2-axis turning centre: `X`/`Z` positions, `C` spindle speed and chuck state, path with execution, program, part count and a recurring tailstock *Warning* |

The container listens on 5000, the MTConnect default; `just` publishes it on 5050 because macOS uses 5000 for the
AirPlay Receiver. A part takes 60 s (the mill cuts for 80 % of it). Every two minutes the spindle reports a *Fault* (`SP-OT`) for 5 s.

| Variable | Default | Effect |
|----------|---------|--------|
| `MTCONNECT_SIM_PORT` | `5000` | HTTP port |
| `MTCONNECT_SIM_TICK_MS` | `100` | fast tick; slow values change every 10 ticks |
| `MTCONNECT_SIM_BUFFER` | `131072` | observations kept for `/sample` and history |
| `MTCONNECT_SIM_LOG` | `INFO` | `DEBUG` logs every request |

```sh
curl -s http://127.0.0.1:5050/probe                                        # the device model
curl -sg 'http://127.0.0.1:5050/current?path=//DataItem[@id="vmc_x_pos"]'  # one data item
mdbrowser read mtconnect://127.0.0.1:5050 /VMC/base/X/Xpos /VMC/base/S/Scond
```

## Integration tests

`tests/MachineDataBrowser.Core.Tests` builds `simulators/cip`, `simulators/pccc`, `simulators/opcua-custom` and
`simulators/mqtt` with Testcontainers (`LogixSimulatorFixture`, `PcccSimulatorFixture`, `CustomTypesServerFixture`,
`MqttSimulatorFixture`); the custom server runs with a small address space and the MQTT publisher with faster deaths
and rebirths there. `CipClientTests`,
`CipConnectionLossTests`, `PcccClientTests`, `CustomTypesTests`, `MqttClientTests` and the App's `MqttAppTests` run
in CI with the rest of `dotnet test`. `simulators/modbus` runs the same way (`ModbusSimulatorFixture`) for
`ModbusClientTests`, `ModbusTagMapClientTests`, the CLI's `ModbusCliTests` and the App's `ModbusAppTests`; one test
checks that the simulator's register map and session still match it. `simulators/s7` likewise (`S7SimulatorFixture`)
for the `S7*Tests` in Core, the CLI's `S7CliTests` and the App's `S7AppTests`. `simulators/mtconnect`
(`MtConnectSimulatorFixture`) backs `MtConnectClientTests`, the CLI's `MtConnectCliTests` and the App's
`MtConnectAppTests`.

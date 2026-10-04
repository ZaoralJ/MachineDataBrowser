# Test servers (simulators)

Four containers under `simulators/` cover local development, manual/user testing and the integration tests.
Start them with [`just`](https://github.com/casey/just) from the repository root:

```sh
just cip            # Logix / EtherNet/IP   -> eip://localhost:44818/1,0
just opcua          # opc-plc               -> opc.tcp://localhost:50000
just opcua-custom   # custom types (asyncua) -> opc.tcp://localhost:4841/
just mqtt           # MQTT broker + traffic  -> mqtt://localhost:1883 (ws://localhost:9001)
just all            # all four in the background (replaces running ones); `just stop` removes them
just logs cip       # follow a background container's log
just cip-debug      # log every CIP request (service, path, sizes)
just test-sim       # Core tests against the simulators
```

Recipe parameters are positional, e.g. `just cip 44900 20` (port 44900, 20 ms base tick).

## Pausing the simulation

Write `true` to the pause tag to freeze every changing value, `false` to continue. The simulation clock stops
while paused, so values continue from where they were rather than jumping. Each simulator logs the change.

| Simulator | Pause tag |
|---|---|
| Logix | `PauseSimulation` (controller tag, BOOL) |
| Custom types | `/Objects/Custom/PauseSimulation` |
| MQTT | topic `simulator/PauseSimulation` (retained, payload `true`/`false`); the app writes it like any topic |
| opc-plc | no single tag; call `Objects/OpcPlc/Methods/StopUpdateFastNodes` / `StopUpdateSlowNodes` (and `Start…`) |

```sh
mdbrowser write eip://localhost:44818/1,0 PauseSimulation true --yes
mdbrowser write opc.tcp://localhost:4841 /Objects/Custom/PauseSimulation false --yes
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
| BOOL | packed BOOL members (`Flags.B0`…`B11` around a DINT, `BoolsOnly`), `TestBoolArray` BOOL[64] and BOOL[n] UDT members (stored and described as DWORDs) |
| UDTs | `Motor1`, `Motors` MOTOR_T[4], `Stations` STATION_T[6] (nested ROBOT_T, CONVEYOR_T, MOTOR_T[3], BOOL[32]), `Line`, `Plant` (5 levels, arrays of UDTs at several levels), `Types`/`Arrays` (every type), `Recipes` RECIPE_T[3] (1 kB each, fragmented reads) |
| Changing values | `HighSpeed.Axes[0..3]`, `HighSpeed.Cycle`, `HighSpeed.Vision`, `Stations[*].Robot`, `Program:Motion.*` at 10 / 20 / 50 ms; `Fast` 100 ms, `Medium` 1 s, `Slow` 10 s, `Heartbeat` 1 s and slow process values |
| Programs | `MainProgram`, `Motion`, `DATALOG`, `Packaging`, `Safety`, `Empty`, `Line01`…`Line20` |
| Hidden by the browser | `Local:1:I`, `Local:2:O` (module types with colons), `__HiddenCounter`, `SystemClock` (system flag), `Task:MainTask`, `Map:Local`, `Routine:MainRoutine` in each program |
| Paging | `Bulk_0001`…`Bulk_3000` push the controller `@tags` listing over many pages |

### Writing

Every value that is not animated accepts Write Tag, so it keeps whatever a client writes: the `Test*` atomics,
strings and arrays, `Types.*`, `Arrays.*`, `Setpoints`, `Recipe_Active`, `Recipes`, `Motor1`/`Motor2`, static
members such as `Stations[*].Name`, program tags such as `Program:Packaging.Mode`. Animated values (the
*Changing values* row) are owned by the simulated program: writes that touch their bytes fail with CIP status
`0x0F` (privilege violation), which the browser shows as `BadNotWritable`. While `PauseSimulation` is set they
accept writes too, and keep the written value until the simulation resumes. With `CIP_SIM_FAST_TICK_MS=0` nothing
is animated and every tag is writable.

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
index (`Bits[1]` is the second 32-bit word, as pycomm3 expects) and reads of `n` BOOLs return `ceil(n/32)` DWORDs,
there are no aliases, AOIs, produced/consumed tags or External Access settings (only animated values are read-only).

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

## Integration tests

`tests/MachineDataBrowser.Core.Tests` builds `simulators/cip`, `simulators/opcua-custom` and `simulators/mqtt` with
Testcontainers (`LogixSimulatorFixture`, `CustomTypesServerFixture`, `MqttSimulatorFixture`); the custom server runs
with a small address space and the MQTT publisher with faster deaths and rebirths there. `CipClientTests`,
`CipConnectionLossTests`, `CustomTypesTests`, `MqttClientTests` and the App's `MqttAppTests` run in CI with the rest
of `dotnet test`.

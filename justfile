# Local test servers for MachineDataBrowser. `just` lists the recipes; containers run in the foreground (Ctrl-C stops).
# Details: docs/simulators.md

cip_image := "machinedatabrowser-cip-simulator:dev"
pccc_image := "machinedatabrowser-pccc-simulator:dev"
opcua_image := "machinedatabrowser-opcua-simulator:dev"
custom_image := "machinedatabrowser-opcua-custom:dev"
mqtt_image := "machinedatabrowser-mqtt-simulator:dev"
modbus_image := "machinedatabrowser-modbus-simulator:dev"
s7_image := "machinedatabrowser-s7-simulator:dev"

default:
    @just --list

# Logix (ControlLogix) EtherNet/IP simulator -> eip://127.0.0.1:PORT/1,0 (UDT arrays, program scopes, 10/20/50 ms tags)
cip port="44818" fast_ms="10" bulk="3000" programs="20":
    docker build -t {{cip_image}} simulators/cip
    -docker rm -f machinedatabrowser-cip >/dev/null 2>&1
    docker run --rm -it --name machinedatabrowser-cip -p {{port}}:44818 \
        -e CIP_SIM_FAST_TICK_MS={{fast_ms}} -e CIP_SIM_BULK_TAGS={{bulk}} -e CIP_SIM_PROGRAMS={{programs}} \
        {{cip_image}}

# Same, with every CIP request logged (service, path, sizes)
cip-debug port="44818":
    docker build -t {{cip_image}} simulators/cip
    -docker rm -f machinedatabrowser-cip >/dev/null 2>&1
    docker run --rm -it --name machinedatabrowser-cip -p {{port}}:44818 -e CIP_SIM_LOG=DEBUG {{cip_image}}

# Run the Logix simulator without Docker (Python 3.11+, no packages needed)
cip-local port="44818":
    cd simulators/cip && CIP_SIM_PORT={{port}} python3 logix_server.py

# SLC 500 / MicroLogix / PLC-5 (PCCC) simulator -> pccc://127.0.0.1:PORT?plc=slc500|micrologix|plc5 (pause: B3:0/0)
pccc port="44819" tick_ms="100":
    docker build -t {{pccc_image}} simulators/pccc
    -docker rm -f machinedatabrowser-pccc >/dev/null 2>&1
    docker run --rm -it --name machinedatabrowser-pccc -p {{port}}:44818 -e PCCC_SIM_TICK_MS={{tick_ms}} {{pccc_image}}

# Same, with every PCCC request logged
pccc-debug port="44819":
    docker build -t {{pccc_image}} simulators/pccc
    -docker rm -f machinedatabrowser-pccc >/dev/null 2>&1
    docker run --rm -it --name machinedatabrowser-pccc -p {{port}}:44818 -e PCCC_SIM_LOG=DEBUG {{pccc_image}}

# Run the PCCC simulator without Docker (Python 3.11+, no packages needed)
pccc-local port="44819":
    cd simulators/pccc && PCCC_SIM_PORT={{port}} python3 pccc_server.py

# OPC UA opc-plc simulator -> opc.tcp://127.0.0.1:PORT (Plant tree, boilers, alarms, fast nodes every FAST_MS)
opcua port="50000" fast_ms="10" fast_nodes="20" slow_nodes="10":
    docker build -t {{opcua_image}} simulators/opcua
    -docker rm -f machinedatabrowser-opcua >/dev/null 2>&1
    docker run --rm -it --name machinedatabrowser-opcua -p {{port}}:{{port}} -p 8080:8080 \
        -e OPCUA_SIM_PORT={{port}} -e OPCUA_SIM_FAST_MS={{fast_ms}} \
        -e OPCUA_SIM_FAST_NODES={{fast_nodes}} -e OPCUA_SIM_SLOW_NODES={{slow_nodes}} \
        {{opcua_image}}

# OPC UA server with custom structures/enums/unions and a large address space -> opc.tcp://127.0.0.1:PORT/
opcua-custom port="4841" fast_ms="10" large="10,10,50" flat="10000":
    docker build -t {{custom_image}} simulators/opcua-custom
    -docker rm -f machinedatabrowser-opcua-custom >/dev/null 2>&1
    docker run --rm -it --name machinedatabrowser-opcua-custom -p {{port}}:{{port}} \
        -e OPCUA_CUSTOM_PORT={{port}} -e OPCUA_CUSTOM_FAST_MS={{fast_ms}} \
        -e OPCUA_CUSTOM_LARGE={{large}} -e OPCUA_CUSTOM_FLAT={{flat}} \
        {{custom_image}}

# MQTT broker (Mosquitto) with plain, JSON, retained, binary and Sparkplug B traffic -> mqtt://127.0.0.1:PORT (ws on 9001)
mqtt port="1883" fast_ms="10" bulk="500" death_s="30":
    docker build -t {{mqtt_image}} simulators/mqtt
    -docker rm -f machinedatabrowser-mqtt >/dev/null 2>&1
    docker run --rm -it --name machinedatabrowser-mqtt -p {{port}}:1883 -p 9001:9001 \
        -e MQTT_SIM_FAST_MS={{fast_ms}} -e MQTT_SIM_BULK={{bulk}} -e MQTT_SIM_DEATH_PERIOD_S={{death_s}} \
        {{mqtt_image}}

# Modbus TCP simulator -> modbus://127.0.0.1:PORT/1 (typed, static and changing registers; coil 0 pauses)
modbus port="5020" tick_ms="100":
    docker build -t {{modbus_image}} simulators/modbus
    -docker rm -f machinedatabrowser-modbus >/dev/null 2>&1
    docker run --rm -it --name machinedatabrowser-modbus -p {{port}}:502 -e MODBUS_SIM_TICK_MS={{tick_ms}} {{modbus_image}}

# Run the Modbus simulator without Docker (Python 3.11+, no packages needed)
modbus-local port="5020":
    cd simulators/modbus && MODBUS_SIM_PORT={{port}} python3 modbus_server.py

# Siemens S7 simulator (python-snap7) -> s7://127.0.0.1:PORT/0,1 with tag map simulators/s7/tags.csv
s7 port="102" tick_ms="50":
    docker build -t {{s7_image}} simulators/s7
    -docker rm -f machinedatabrowser-s7 >/dev/null 2>&1
    @echo "Tag map: simulators/s7/tags.csv (or open simulators/s7/simulator.mdbsession)"
    docker run --rm -it --name machinedatabrowser-s7 -p {{port}}:102 -e S7_SIM_TICK_MS={{tick_ms}} {{s7_image}}

# Start all simulators in the background
all: stop
    docker build -t {{cip_image}} simulators/cip
    docker build -t {{pccc_image}} simulators/pccc
    docker build -t {{opcua_image}} simulators/opcua
    docker build -t {{custom_image}} simulators/opcua-custom
    docker build -t {{mqtt_image}} simulators/mqtt
    docker build -t {{modbus_image}} simulators/modbus
    docker build -t {{s7_image}} simulators/s7
    docker run -d --rm --name machinedatabrowser-cip -p 44818:44818 {{cip_image}}
    docker run -d --rm --name machinedatabrowser-pccc -p 44819:44818 {{pccc_image}}
    docker run -d --rm --name machinedatabrowser-opcua -p 50000:50000 -p 8080:8080 {{opcua_image}}
    docker run -d --rm --name machinedatabrowser-opcua-custom -p 4841:4841 {{custom_image}}
    docker run -d --rm --name machinedatabrowser-mqtt -p 1883:1883 -p 9001:9001 {{mqtt_image}}
    docker run -d --rm --name machinedatabrowser-modbus -p 5020:502 {{modbus_image}}
    docker run -d --rm --name machinedatabrowser-s7 -p 102:102 {{s7_image}}
    @echo "eip://127.0.0.1:44818/1,0  pccc://127.0.0.1:44819?plc=slc500  opc.tcp://127.0.0.1:50000  opc.tcp://127.0.0.1:4841/  mqtt://127.0.0.1:1883  modbus://127.0.0.1:5020/1  s7://127.0.0.1:102/0,1"

# Stop all simulator containers
stop:
    -docker rm -f machinedatabrowser-cip machinedatabrowser-pccc machinedatabrowser-opcua machinedatabrowser-opcua-custom machinedatabrowser-mqtt machinedatabrowser-modbus machinedatabrowser-s7

# Follow a simulator's log (cip | pccc | opcua | opcua-custom | mqtt | modbus | s7)
logs name="cip":
    docker logs -f machinedatabrowser-{{name}}

# Core tests against the simulators (needs Docker)
test-sim:
    dotnet test tests/MachineDataBrowser.Core.Tests -- --filter-class "*CipClientTests" --filter-class "*CustomTypesTests" --filter-class "*MqttClientTests" --filter-class "*CipConnectionLossTests" --filter-class "*ModbusClientTests" --filter-class "*PcccClientTests" --filter-class "*S7*"

# Regenerate the dark-theme screenshots in docs/images (needs Docker; compresses them with pngquant)
docs-screenshots:
    MDB_DOCS_SCREENSHOTS=1 dotnet test tests/MachineDataBrowser.App.Tests -- --filter-class "*DocsScreenshotTests"
    PATH="/opt/homebrew/bin:/usr/local/bin:$PATH" pngquant --force --skip-if-larger --quality=80-95 --strip --ext .png docs/images/*.png docs/images/themes/app-*.png

# Regenerate docs/images/tui*.png: the real mdbrowser tui in a pseudo-terminal (needs `just all`, uv, pngquant)
docs-tui-screenshot:
    dotnet build src/MachineDataBrowser.Cli -o artifacts/docs-cli
    uv run scripts/tui-screenshot.py artifacts/docs-cli/mdbrowser docs/images
    PATH="/opt/homebrew/bin:/usr/local/bin:$PATH" pngquant --force --skip-if-larger --quality=80-95 --strip --ext .png docs/images/tui*.png docs/images/themes/tui-*.png

# Regenerate docs/cli-examples.md: every mdbrowser command with its output (needs `just all`, uv)
docs-cli-examples:
    dotnet build src/MachineDataBrowser.Cli -o artifacts/docs-cli
    uv run scripts/cli-examples.py artifacts/docs-cli/mdbrowser docs/cli-examples.md

# Preview the documentation site at http://127.0.0.1:8000 (needs uv)
docs-serve:
    uv run --with-requirements .github/mkdocs-requirements.txt mkdocs serve

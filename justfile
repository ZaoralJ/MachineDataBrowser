# Local test servers for MachineDataBrowser. Most recipes run in the foreground; Valkey and selected Compose recipes
# run detached. `just all` runs every simulator in the background.
# Details: docs/simulators.md

default:
    @just --list

# Logix (ControlLogix) EtherNet/IP simulator -> eip://127.0.0.1:PORT/1,0 (UDT arrays, program scopes, 10/20/50 ms tags)
cip port="44818" fast_ms="10" bulk="3000" programs="20":
    -docker compose stop cip
    -docker compose rm -f cip
    -docker rm -f machinedatabrowser-cip
    CIP_HOST_PORT={{port}} CIP_SIM_FAST_TICK_MS={{fast_ms}} CIP_SIM_BULK_TAGS={{bulk}} CIP_SIM_PROGRAMS={{programs}} \
        docker compose run --build --rm --service-ports --name machinedatabrowser-cip cip

# Same, with every CIP request logged (service, path, sizes)
cip-debug port="44818":
    -docker compose stop cip
    -docker compose rm -f cip
    -docker rm -f machinedatabrowser-cip
    CIP_HOST_PORT={{port}} CIP_SIM_LOG=DEBUG docker compose run --build --rm --service-ports \
        --name machinedatabrowser-cip cip

# Run the Logix simulator without Docker (Python 3.11+, no packages needed)
cip-local port="44818":
    cd simulators/cip && CIP_SIM_PORT={{port}} python3 logix_server.py

# SLC 500 / MicroLogix / PLC-5 (PCCC) simulator -> pccc://127.0.0.1:PORT?plc=slc500|micrologix|plc5 (pause: B3:0/0)
pccc port="44819" tick_ms="100":
    -docker compose stop pccc
    -docker compose rm -f pccc
    -docker rm -f machinedatabrowser-pccc
    PCCC_HOST_PORT={{port}} PCCC_SIM_TICK_MS={{tick_ms}} docker compose run --build --rm --service-ports \
        --name machinedatabrowser-pccc pccc

# Same, with every PCCC request logged
pccc-debug port="44819":
    -docker compose stop pccc
    -docker compose rm -f pccc
    -docker rm -f machinedatabrowser-pccc
    PCCC_HOST_PORT={{port}} PCCC_SIM_LOG=DEBUG docker compose run --build --rm --service-ports \
        --name machinedatabrowser-pccc pccc

# Run the PCCC simulator without Docker (Python 3.11+, no packages needed)
pccc-local port="44819":
    cd simulators/pccc && PCCC_SIM_PORT={{port}} python3 pccc_server.py

# OPC UA opc-plc simulator -> opc.tcp://127.0.0.1:PORT (optional trailing username/password override user1/password)
opcua port="50000" fast_ms="10" fast_nodes="20" slow_nodes="10" username="" password="":
    @if { [ -n "{{username}}" ] && [ -z "{{password}}" ]; } || { [ -z "{{username}}" ] && [ -n "{{password}}" ]; }; then echo "username and password must both be provided" >&2; exit 2; fi
    -docker compose stop opcua
    -docker compose rm -f opcua
    -docker rm -f machinedatabrowser-opcua
    OPCUA_PORT={{port}} OPCUA_SIM_FAST_MS={{fast_ms}} OPCUA_SIM_FAST_NODES={{fast_nodes}} \
        OPCUA_SIM_SLOW_NODES={{slow_nodes}} \
        OPCUA_SIM_USERNAME="{{if username == "" { "user1" } else { username }}}" \
        OPCUA_SIM_PASSWORD="{{if password == "" { "password" } else { password }}}" \
        OPCUA_SIM_DISABLE_ANONYMOUS={{if username == "" { "false" } else { "true" }}} \
        docker compose run --build --rm --service-ports \
        --name machinedatabrowser-opcua opcua

# OPC UA with custom credentials and the default port/simulation settings
opcua-auth username password port="50000":
    -docker compose stop opcua
    -docker compose rm -f opcua
    -docker rm -f machinedatabrowser-opcua
    OPCUA_PORT={{port}} OPCUA_SIM_FAST_MS=10 OPCUA_SIM_FAST_NODES=20 OPCUA_SIM_SLOW_NODES=10 \
        OPCUA_SIM_USERNAME="{{username}}" OPCUA_SIM_PASSWORD="{{password}}" OPCUA_SIM_DISABLE_ANONYMOUS=true \
        docker compose up --build --detach --wait opcua

# OPC UA custom types server -> opc.tcp://127.0.0.1:PORT/ (starts in the machine-data-browser Compose project)
opcua-custom port="4841" fast_ms="10" large="10,10,50" flat="10000":
    -docker compose stop opcua-custom
    -docker compose rm -f opcua-custom
    -docker rm -f machinedatabrowser-opcua-custom
    OPCUA_CUSTOM_HOST_PORT={{port}} OPCUA_CUSTOM_PORT={{port}} OPCUA_CUSTOM_FAST_MS={{fast_ms}} \
        OPCUA_CUSTOM_LARGE={{large}} OPCUA_CUSTOM_FLAT={{flat}} \
        docker compose up --build --detach --wait opcua-custom

# OPC UA custom types server with credentials in the machine-data-browser Compose project
opcua-custom-auth username password port="4841":
    -docker compose stop opcua-custom
    -docker compose rm -f opcua-custom
    -docker rm -f machinedatabrowser-opcua-custom
    OPCUA_CUSTOM_HOST_PORT={{port}} OPCUA_CUSTOM_PORT={{port}} OPCUA_CUSTOM_FAST_MS=10 \
        OPCUA_CUSTOM_LARGE=10,10,50 OPCUA_CUSTOM_FLAT=10000 \
        OPCUA_CUSTOM_USERNAME="{{username}}" OPCUA_CUSTOM_PASSWORD="{{password}}" \
        docker compose up --build --detach --wait opcua-custom

# MQTT broker (Mosquitto) with plain, JSON, retained, binary and Sparkplug B traffic -> mqtt://127.0.0.1:PORT (ws on 9001)
mqtt port="1883" fast_ms="10" bulk="500" death_s="30" username="" password="":
    -docker compose stop mqtt
    -docker compose rm -f mqtt
    -docker rm -f machinedatabrowser-mqtt
    MQTT_HOST_PORT={{port}} MQTT_SIM_FAST_MS={{fast_ms}} MQTT_SIM_BULK={{bulk}} \
        MQTT_SIM_DEATH_PERIOD_S={{death_s}} MQTT_SIM_USERNAME={{username}} MQTT_SIM_PASSWORD={{password}} \
        docker compose run --build --rm --service-ports \
        --name machinedatabrowser-mqtt mqtt

# Modbus TCP simulator -> modbus://127.0.0.1:PORT/1 (typed, static and changing registers; coil 0 pauses)
modbus port="5020" tick_ms="100":
    -docker compose stop modbus
    -docker compose rm -f modbus
    -docker rm -f machinedatabrowser-modbus
    MODBUS_HOST_PORT={{port}} MODBUS_SIM_TICK_MS={{tick_ms}} docker compose run --build --rm --service-ports \
        --name machinedatabrowser-modbus modbus

# Run the Modbus simulator without Docker (Python 3.11+, no packages needed)
modbus-local port="5020":
    cd simulators/modbus && MODBUS_SIM_PORT={{port}} python3 modbus_server.py
# MTConnect agent -> mtconnect://127.0.0.1:PORT (detached in Compose; pause: just mtconnect-pause)
mtconnect port="5050" tick_ms="100" buffer="131072":
    -docker compose stop mtconnect
    -docker compose rm -f mtconnect
    -docker rm -f machinedatabrowser-mtconnect
    MTCONNECT_HOST_PORT={{port}} MTCONNECT_SIM_TICK_MS={{tick_ms}} MTCONNECT_SIM_BUFFER={{buffer}} \
        docker compose up --build --detach --wait mtconnect

# MTConnect agent with Basic authentication in the machine-data-browser Compose project
mtconnect-auth username password port="5050":
    -docker compose stop mtconnect
    -docker compose rm -f mtconnect
    -docker rm -f machinedatabrowser-mtconnect
    MTCONNECT_HOST_PORT={{port}} MTCONNECT_SIM_TICK_MS=100 MTCONNECT_SIM_BUFFER=131072 \
        MTCONNECT_SIM_USERNAME="{{username}}" MTCONNECT_SIM_PASSWORD="{{password}}" \
        docker compose up --build --detach --wait mtconnect

# Run the MTConnect agent simulator without Docker (Python 3.11+, no packages needed)
mtconnect-local port="5050":
    cd simulators/mtconnect && MTCONNECT_SIM_PORT={{port}} python3 agent.py

# Valkey server with machine-like strings and hashes -> redis://127.0.0.1:PORT/0
valkey port="6379":
    VALKEY_HOST_PORT={{port}} docker compose up --detach --wait valkey
    just valkey-seed
    @echo "redis://127.0.0.1:{{port}}/0 (Redis-compatible Valkey)"

# Valkey with a custom ACL user/password in the machine-data-browser Compose project
valkey-auth username password port="6379":
    -docker compose stop valkey
    -docker compose rm -f valkey
    VALKEY_HOST_PORT={{port}} VALKEY_SIM_USERNAME="{{username}}" VALKEY_SIM_PASSWORD="{{password}}" \
        docker compose up --detach --wait valkey
    just valkey-seed
    @echo "redis://{{username}}@127.0.0.1:{{port}}/0 (Redis-compatible Valkey; password stored in the OS credential store when used by mdbrowser)"

# Reseed the running Valkey instance with deterministic machine-like values
valkey-seed:
    docker compose exec -T valkey sh -s < simulators/valkey/seed.sh

# Pause (true) or continue (false) the MTConnect simulator; MTConnect is read-only, so it has no pause tag
mtconnect-pause value="true" port="5050" username="" password="":
    @if { [ -n "{{username}}" ] && [ -z "{{password}}" ]; } || { [ -z "{{username}}" ] && [ -n "{{password}}" ]; }; then echo "username and password must both be provided" >&2; exit 2; fi
    @if [ -n "{{username}}" ]; then curl -fsS --user "{{username}}:{{password}}" -X PUT 'http://127.0.0.1:{{port}}/simulator/pause?value={{value}}'; else curl -fsS -X PUT 'http://127.0.0.1:{{port}}/simulator/pause?value={{value}}'; fi

# Siemens S7 simulator (python-snap7) -> s7://127.0.0.1:PORT/0,1 with tag map simulators/s7/tags.csv
s7 port="102" tick_ms="50":
    -docker compose stop s7
    -docker compose rm -f s7
    -docker rm -f machinedatabrowser-s7
    @echo "Tag map: simulators/s7/tags.csv (or open simulators/s7/simulator-s7.mdbsession)"
    S7_HOST_PORT={{port}} S7_SIM_TICK_MS={{tick_ms}} docker compose run --build --rm --service-ports \
        --name machinedatabrowser-s7 s7

# Start all simulators in the background
all: stop
    docker compose up --build --detach --wait
    just valkey-seed
    @echo "eip://127.0.0.1:44818/1,0  pccc://127.0.0.1:44819?plc=slc500  opc.tcp://127.0.0.1:50000  opc.tcp://127.0.0.1:4841/  mqtt://127.0.0.1:1883  modbus://127.0.0.1:5020/1  s7://127.0.0.1:102/0,1  mtconnect://127.0.0.1:5050  redis://127.0.0.1:6379/0"

# Stop all simulator containers
stop:
    -docker compose down --remove-orphans
    -docker rm -f machinedatabrowser-cip machinedatabrowser-pccc machinedatabrowser-opcua machinedatabrowser-opcua-custom machinedatabrowser-mqtt machinedatabrowser-modbus machinedatabrowser-s7 machinedatabrowser-mtconnect

# Follow a simulator's log (cip | pccc | opcua | opcua-custom | mqtt | modbus | s7 | mtconnect | valkey)
logs name="cip":
    docker compose logs --follow {{name}}

# Core tests against the simulators (needs Docker)
test-sim:
    dotnet test tests/MachineDataBrowser.Core.Tests -- --filter-class "*CipClientTests" --filter-class "*CustomTypesTests" --filter-class "*MqttClientTests" --filter-class "*CipConnectionLossTests" --filter-class "*ModbusClientTests" --filter-class "*PcccClientTests" --filter-class "*S7*" --filter-class "*MtConnectClientTests" --filter-class "*RedisClientTests"

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

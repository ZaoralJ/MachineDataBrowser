# Local test servers for OpcUaBrowser. `just` lists the recipes; containers run in the foreground (Ctrl-C stops).
# Details: docs/simulators.md

cip_image := "opcuabrowser-cip-simulator:dev"
opcua_image := "opcuabrowser-opcua-simulator:dev"
custom_image := "opcuabrowser-opcua-custom:dev"

default:
    @just --list

# Logix (ControlLogix) EtherNet/IP simulator -> eip://localhost:PORT/1,0 (UDT arrays, program scopes, 10/20/50 ms tags)
cip port="44818" fast_ms="10" bulk="3000" programs="20":
    docker build -t {{cip_image}} simulators/cip
    docker run --rm -it --name opcuabrowser-cip -p {{port}}:44818 \
        -e CIP_SIM_FAST_TICK_MS={{fast_ms}} -e CIP_SIM_BULK_TAGS={{bulk}} -e CIP_SIM_PROGRAMS={{programs}} \
        {{cip_image}}

# Same, with every CIP request logged (service, path, sizes)
cip-debug port="44818":
    docker build -t {{cip_image}} simulators/cip
    docker run --rm -it --name opcuabrowser-cip -p {{port}}:44818 -e CIP_SIM_LOG=DEBUG {{cip_image}}

# Run the Logix simulator without Docker (Python 3.11+, no packages needed)
cip-local port="44818":
    cd simulators/cip && CIP_SIM_PORT={{port}} python3 logix_server.py

# OPC UA opc-plc simulator -> opc.tcp://localhost:PORT (Plant tree, boilers, alarms, fast nodes every FAST_MS)
opcua port="50000" fast_ms="10" fast_nodes="20" slow_nodes="10":
    docker build -t {{opcua_image}} simulators/opcua
    docker run --rm -it --name opcuabrowser-opcua -p {{port}}:{{port}} -p 8080:8080 \
        -e OPCUA_SIM_PORT={{port}} -e OPCUA_SIM_FAST_MS={{fast_ms}} \
        -e OPCUA_SIM_FAST_NODES={{fast_nodes}} -e OPCUA_SIM_SLOW_NODES={{slow_nodes}} \
        {{opcua_image}}

# OPC UA server with custom structures/enums/unions and a large address space -> opc.tcp://localhost:PORT/
opcua-custom port="4841" fast_ms="10" large="10,10,50" flat="10000":
    docker build -t {{custom_image}} simulators/opcua-custom
    docker run --rm -it --name opcuabrowser-opcua-custom -p {{port}}:{{port}} \
        -e OPCUA_CUSTOM_PORT={{port}} -e OPCUA_CUSTOM_FAST_MS={{fast_ms}} \
        -e OPCUA_CUSTOM_LARGE={{large}} -e OPCUA_CUSTOM_FLAT={{flat}} \
        {{custom_image}}

# Start all three simulators in the background
all:
    docker build -t {{cip_image}} simulators/cip
    docker build -t {{opcua_image}} simulators/opcua
    docker build -t {{custom_image}} simulators/opcua-custom
    docker run -d --rm --name opcuabrowser-cip -p 44818:44818 {{cip_image}}
    docker run -d --rm --name opcuabrowser-opcua -p 50000:50000 -p 8080:8080 {{opcua_image}}
    docker run -d --rm --name opcuabrowser-opcua-custom -p 4841:4841 {{custom_image}}
    @echo "eip://localhost:44818/1,0  opc.tcp://localhost:50000  opc.tcp://localhost:4841/"

# Stop all simulator containers
stop:
    -docker rm -f opcuabrowser-cip opcuabrowser-opcua opcuabrowser-opcua-custom

# Follow a simulator's log (cip | opcua | opcua-custom)
logs name="cip":
    docker logs -f opcuabrowser-{{name}}

# Core tests against the simulators (needs Docker)
test-sim:
    dotnet test tests/OpcUaBrowser.Core.Tests -- --filter-class "*CipClientTests" --filter-class "*CustomTypesTests"

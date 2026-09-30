# OPC UA simulator (Microsoft opc-plc) for local development and user testing. See docs/simulators.md.
#   Objects/OpcPlc/Plant       writable folder tree from nodesfile.json
#   Objects/OpcPlc/Telemetry   fast nodes every OPCUA_SIM_FAST_MS (10 ms), simulation every OPCUA_SIM_CYCLE_MS (50 ms),
#                              slow nodes every second
#   Boilers, alarms, simple events, stacklight, pumps, ReferenceTest (all data types)
# Endpoint opc.tcp://localhost:50000 - anonymous, SecurityPolicy None allowed, client certs auto-accepted.
# Users: admin / admin (SecurityAdmin), user1 / password.
# Pinned: `latest` differs between machines and older builds reject some of the flags below.
FROM mcr.microsoft.com/iotedge/opc-plc:2.15.7

COPY nodesfile.json /app/config/nodesfile.json

ENV OPCUA_SIM_PORT=50000 \
    OPCUA_SIM_HOST=localhost \
    OPCUA_SIM_FAST_MS=10 \
    OPCUA_SIM_CYCLE_MS=50 \
    OPCUA_SIM_FAST_NODES=20 \
    OPCUA_SIM_SLOW_NODES=10 \
    OPCUA_SIM_GUID_NODES=5

EXPOSE 50000 8080

ENTRYPOINT ["/bin/sh", "-c", "exec ./opcplc \
  --pn=\"$OPCUA_SIM_PORT\" --ph=\"$OPCUA_SIM_HOST\" \
  --autoaccept --ut --to \
  --au=admin --ac=admin --du=user1 --dc=password \
  --nodesfile=/app/config/nodesfile.json \
  --ct=\"$OPCUA_SIM_CYCLE_MS\" \
  --fn=\"$OPCUA_SIM_FAST_NODES\" --ft=double --vfr=\"$OPCUA_SIM_FAST_MS\" \
  --sn=\"$OPCUA_SIM_SLOW_NODES\" --sr=1 --st=uint \
  --gn=\"$OPCUA_SIM_GUID_NODES\" \
  --alm --ses --sl --pu \
  \"$@\"", "opcplc"]

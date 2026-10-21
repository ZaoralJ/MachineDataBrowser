# Modbus TCP simulator for local development, user testing and integration tests.
# Pure Python, no dependencies. See docs/simulators.md.
FROM python:3.13-slim

WORKDIR /app
COPY modbus_server.py ./

ENV MODBUS_SIM_PORT=502 \
    MODBUS_SIM_TICK_MS=100 \
    MODBUS_SIM_LOG=INFO \
    PYTHONUNBUFFERED=1

EXPOSE 502
ENTRYPOINT ["python", "modbus_server.py"]

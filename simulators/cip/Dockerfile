# Logix (ControlLogix) EtherNet/IP simulator for local development, user testing and integration tests.
# Pure Python, no dependencies. See docs/simulators.md.
FROM python:3.13-slim

WORKDIR /app
COPY logix_model.py controller.py logix_server.py ./

ENV CIP_SIM_PORT=44818 \
    CIP_SIM_FAST_TICK_MS=10 \
    CIP_SIM_BULK_TAGS=3000 \
    CIP_SIM_PROGRAMS=20 \
    CIP_SIM_LOG=INFO \
    PYTHONUNBUFFERED=1

EXPOSE 44818
ENTRYPOINT ["python", "logix_server.py"]

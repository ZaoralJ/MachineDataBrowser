# Allen-Bradley PCCC (SLC 500 / MicroLogix / PLC-5) EtherNet/IP simulator for local development, user testing and
# integration tests. Pure Python, no dependencies. See docs/simulators.md.
FROM python:3.13-slim

WORKDIR /app
COPY pccc_server.py ./

ENV PCCC_SIM_PORT=44818 \
    PCCC_SIM_TICK_MS=100 \
    PCCC_SIM_LOG=INFO \
    PYTHONUNBUFFERED=1

EXPOSE 44818
ENTRYPOINT ["python", "pccc_server.py"]

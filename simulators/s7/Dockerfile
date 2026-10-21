# Siemens S7 simulator (python-snap7 server) for local development, user testing and integration tests.
# See docs/simulators.md.
FROM python:3.13-slim

RUN pip install --no-cache-dir python-snap7==3.3.0

WORKDIR /app
COPY s7_server.py tags.csv ./

ENV S7_SIM_PORT=102 \
    S7_SIM_TICK_MS=50 \
    PYTHONUNBUFFERED=1

EXPOSE 102
ENTRYPOINT ["python", "s7_server.py"]

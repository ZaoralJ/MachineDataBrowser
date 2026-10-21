# MTConnect agent simulator (3-axis mill and a lathe) for local development, user testing and integration tests.
# Pure Python, no dependencies. See docs/simulators.md.
FROM python:3.13-slim

WORKDIR /app
COPY agent.py ./

ENV MTCONNECT_SIM_PORT=5000 \
    MTCONNECT_SIM_TICK_MS=100 \
    MTCONNECT_SIM_BUFFER=131072 \
    MTCONNECT_SIM_LOG=INFO \
    PYTHONUNBUFFERED=1

EXPOSE 5000
ENTRYPOINT ["python", "agent.py"]

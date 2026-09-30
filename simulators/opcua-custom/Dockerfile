# OPC UA server with custom structured types and a large address space (asyncua). See docs/simulators.md.
FROM python:3.13-slim

RUN pip install --no-cache-dir asyncua==2.0.1

WORKDIR /app
COPY server.py .

ENV OPCUA_CUSTOM_PORT=4841 \
    OPCUA_CUSTOM_HOST=localhost \
    OPCUA_CUSTOM_FAST_MS=10 \
    OPCUA_CUSTOM_LARGE=10,10,50 \
    OPCUA_CUSTOM_FLAT=10000 \
    PYTHONUNBUFFERED=1

EXPOSE 4841
ENTRYPOINT ["python", "server.py"]

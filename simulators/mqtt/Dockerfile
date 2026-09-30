# MQTT broker (Mosquitto) plus a publisher of plain, JSON, retained, binary and Sparkplug B traffic.
# See docs/simulators.md.
FROM python:3.13-slim

RUN apt-get update \
 && apt-get install -y --no-install-recommends mosquitto \
 && rm -rf /var/lib/apt/lists/* \
 && pip install --no-cache-dir paho-mqtt==2.1.0

WORKDIR /app
COPY mosquitto.conf publisher.py entrypoint.sh ./
RUN chmod +x entrypoint.sh

ENV MQTT_SIM_FAST_MS=10 \
    MQTT_SIM_BULK=500 \
    MQTT_SIM_DEATH_PERIOD_S=30 \
    MQTT_SIM_REBIRTH_S=15 \
    MQTT_SIM_UNS_LINES=4 \
    PYTHONUNBUFFERED=1

EXPOSE 1883 9001
ENTRYPOINT ["/app/entrypoint.sh"]

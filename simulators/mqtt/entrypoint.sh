#!/bin/sh
set -e

if [ -n "${MQTT_SIM_USERNAME:-}" ] || [ -n "${MQTT_SIM_PASSWORD:-}" ]; then
    if [ -z "${MQTT_SIM_USERNAME:-}" ] || [ -z "${MQTT_SIM_PASSWORD:-}" ]; then
        echo "MQTT_SIM_USERNAME and MQTT_SIM_PASSWORD must both be set" >&2
        exit 1
    fi

    umask 077
    mosquitto_passwd -b -c /tmp/mosquitto.passwd "$MQTT_SIM_USERNAME" "$MQTT_SIM_PASSWORD"
    chown mosquitto:mosquitto /tmp/mosquitto.passwd
    cat > /tmp/mosquitto.conf <<'EOF'
listener 1883 0.0.0.0
listener 9001 0.0.0.0
protocol websockets
allow_anonymous false
password_file /tmp/mosquitto.passwd
persistence false
log_dest stdout
log_type error
log_type warning
EOF
else
    cp /app/mosquitto.conf /tmp/mosquitto.conf
fi

mosquitto -c /tmp/mosquitto.conf &
exec python /app/publisher.py

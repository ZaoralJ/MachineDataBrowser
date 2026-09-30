#!/bin/sh
set -e
mosquitto -c /app/mosquitto.conf &
exec python /app/publisher.py

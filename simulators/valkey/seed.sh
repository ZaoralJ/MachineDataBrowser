#!/bin/sh
set -eu

if [ -n "${VALKEY_SIM_USERNAME:-}" ]; then
    export REDISCLI_AUTH="${VALKEY_SIM_PASSWORD:?}"
fi

cli() {
    if [ -n "${VALKEY_SIM_USERNAME:-}" ]; then
        valkey-cli --user "$VALKEY_SIM_USERNAME" "$@"
    else
        valkey-cli "$@"
    fi
}

cli SET plant:line1:temperature 22.8
cli SET plant:line1:pressure 5.4
cli SET plant:line1:cycle_count 1842
cli SET plant:line1:state RUNNING
cli SET plant:line1:drive:main:speed 1450
cli HSET plant:line1:drive:main:status running true mode auto current 3.2
cli HSET plant:line1:quality last_result PASS rejected_count 2

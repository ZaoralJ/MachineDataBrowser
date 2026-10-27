#!/bin/sh
set -eu

valkey-cli SET plant:line1:temperature 22.8
valkey-cli SET plant:line1:pressure 5.4
valkey-cli SET plant:line1:cycle_count 1842
valkey-cli SET plant:line1:state RUNNING
valkey-cli SET plant:line1:drive:main:speed 1450
valkey-cli HSET plant:line1:drive:main:status running true mode auto current 3.2
valkey-cli HSET plant:line1:quality last_result PASS rejected_count 2

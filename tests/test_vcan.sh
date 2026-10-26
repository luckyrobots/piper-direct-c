#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
# This test uses only its newly created virtual CAN interface.
if ip link show dev piperdiag0 >/dev/null 2>&1; then
  echo 'piperdiag0 already exists; leaving it untouched.' >&2
  exit 1
fi
sudo -n ip link add dev piperdiag0 type vcan
trap 'sudo -n ip link delete dev piperdiag0' EXIT
sudo -n ip link set piperdiag0 up
mkdir -p artifacts
./build/piper-can --interface piperdiag0 --query-firmware --seconds 6 > artifacts/vcan-reconnect.log &
piper_test_pid=$!
sleep 1
sudo -n ip link set piperdiag0 down
sleep 2
sudo -n ip link set piperdiag0 up
if wait "$piper_test_pid"; then piper_test_exit=0; else piper_test_exit=$?; fi
test "$piper_test_exit" -eq 2
test "$(rg -c 'firmware query 4AF#' artifacts/vcan-reconnect.log)" -eq 1
test "$(rg -c 'opened raw CAN socket' artifacts/vcan-reconnect.log)" -ge 2
rg -q 'external RX=0 local/echo=1' artifacts/vcan-reconnect.log
rg -q '0 adapter\(s\) with one fresh complete PiPer feedback set\.' artifacts/vcan-reconnect.log
echo 'Virtual CAN passed: raw sockets, echo excluded, reconnection, no query replay.'

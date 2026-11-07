#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p build/linux-x64
cc -shared -fPIC -std=c17 -O2 -g -Wall -Wextra -Wpedantic -Werror -Isrc -Ivendor \
  src/managed_bridge.c src/controller.c src/canbus.c src/protocol.c vendor/cJSON.c \
  -o build/linux-x64/libpiper-core.so -lm

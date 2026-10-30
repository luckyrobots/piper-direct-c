CC ?= cc
CPPFLAGS += -Isrc -Ivendor -DCJSON_NESTING_LIMIT=32
LDLIBS += -lm
CFLAGS ?= -O2 -g
CFLAGS += -std=c17 -Wall -Wextra -Wpedantic -Werror

.PHONY: all test test-vcan test-record clean
all: build/piper-can build/piper-control build/piper-record build/piper-trace

build:
	mkdir -p build

build/piper-can: src/main.c src/protocol.c src/protocol.h | build
	$(CC) $(CPPFLAGS) $(CFLAGS) src/main.c src/protocol.c -o $@ $(LDLIBS)

CORE = src/controller.c src/canbus.c src/protocol.c vendor/cJSON.c
HEADERS = src/controller.h src/obstruction.h src/canbus.h src/protocol.h vendor/cJSON.h
build/piper-trace: src/trace.c src/canbus.c src/protocol.c $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) src/trace.c src/canbus.c src/protocol.c -o $@ $(LDLIBS)

build/piper-record: src/record.c src/canbus.c src/protocol.c vendor/cJSON.c $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) src/record.c src/canbus.c src/protocol.c vendor/cJSON.c -o $@ $(LDLIBS)

build/piper-control: src/server.c src/camera.c src/camera.h $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) src/server.c src/camera.c $(CORE) -o $@ $(LDLIBS)

build/test-protocol: tests/test_protocol.c src/protocol.c src/protocol.h | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_protocol.c src/protocol.c -o $@ $(LDLIBS)

build/test-control: tests/test_control.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_control.c $(CORE) -o $@ $(LDLIBS)

build/test-range-drift: tests/test_range_drift.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_range_drift.c $(CORE) -o $@ $(LDLIBS)

build/test-configuration-cache: tests/test_configuration_cache.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_configuration_cache.c $(CORE) -o $@ $(LDLIBS)

build/test-camera: tests/test_camera.c src/camera.c src/camera.h src/canbus.c src/protocol.c vendor/cJSON.c $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_camera.c src/camera.c src/canbus.c src/protocol.c vendor/cJSON.c -o $@ $(LDLIBS)

build/test-startup: tests/test_startup.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_startup.c $(CORE) -o $@ $(LDLIBS)

build/test-cycle: tests/test_cycle.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_cycle.c $(CORE) -o $@ $(LDLIBS)

build/test-gripper: tests/test_gripper.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_gripper.c $(CORE) -o $@ $(LDLIBS)

build/test-obstruction: tests/test_obstruction.c src/obstruction.h | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_obstruction.c -o $@ $(LDLIBS)

build/test-obstruction-controller: tests/test_obstruction_controller.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_obstruction_controller.c $(CORE) -o $@ $(LDLIBS)

build/test-follow: tests/test_follow.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_follow.c $(CORE) -o $@ $(LDLIBS)

test: all build/test-protocol build/test-control build/test-camera build/test-startup build/test-cycle build/test-gripper build/test-obstruction build/test-obstruction-controller build/test-range-drift build/test-configuration-cache build/test-follow
	./build/test-follow
	./build/test-configuration-cache
	./build/test-range-drift
	./build/test-obstruction
	./build/test-obstruction-controller
	./build/test-protocol
	./build/test-control
	./build/test-camera
	./build/test-startup
	./build/test-cycle
	./build/test-gripper
	./build/piper-can --help >/dev/null
	! ./build/piper-can --query-firmware >/dev/null 2>&1
	! ./build/piper-can --seconds nonsense >/dev/null 2>&1
	! ./build/piper-can --interface lo --query-firmware --seconds 1 >/dev/null 2>&1
	node --check web/app.js
	node --check web/bench.mjs
	node --check web/pika.mjs
	node --check web/pika_remote.mjs
	node tests/test_pika_remote.mjs
	node tests/test_pika_windows_block.mjs
	node tests/test_pika.mjs
	node tests/test_pika_ui.mjs
	node tests/test_control_prompt.mjs
	node tests/test_camera_profiles.mjs
	node tests/test_api.mjs

clean:
	rm -f build/piper-can build/piper-control build/test-protocol build/test-control build/test-camera build/test-control-sanitized

test-vcan: build/piper-can
	bash tests/test_vcan.sh

test-record: build/piper-record
	python3 tests/test_record.py

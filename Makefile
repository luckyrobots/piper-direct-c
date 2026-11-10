CC ?= cc
CPPFLAGS += -Isrc -Ivendor -DCJSON_NESTING_LIMIT=32
LDLIBS += -lm
CFLAGS ?= -O2 -g
CFLAGS += -std=c17 -Wall -Wextra -Wpedantic -Werror

.PHONY: all test test-vcan test-record clean
all: build/piper-can build/piper-control build/piper-record build/piper-trace

build:
	mkdir -p build

CORE = src/controller.c src/canbus.c src/protocol.c vendor/cJSON.c
HEADERS = tests/fixture.h tests/check.h src/controller.h src/controller_internal.h src/controller_lane_fields.inc src/obstruction.h src/canbus.h src/protocol.h vendor/cJSON.h

build/piper-can: src/main.c src/protocol.c src/protocol.h | build
	$(CC) $(CPPFLAGS) $(CFLAGS) src/main.c src/protocol.c -o $@ $(LDLIBS)

build/piper-trace: src/trace.c src/canbus.c src/protocol.c $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) src/trace.c src/canbus.c src/protocol.c -o $@ $(LDLIBS)

build/piper-record: src/record.c src/canbus.c src/protocol.c vendor/cJSON.c $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) src/record.c src/canbus.c src/protocol.c vendor/cJSON.c -o $@ $(LDLIBS)

build/piper-control: src/server.c src/camera.c src/camera.h $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) src/server.c src/camera.c $(CORE) -o $@ $(LDLIBS)

build/test-protocol: tests/test_protocol.c src/protocol.c src/protocol.h tests/check.h | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_protocol.c src/protocol.c -o $@ $(LDLIBS)

build/test-obstruction: tests/test_obstruction.c src/obstruction.h tests/check.h | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_obstruction.c -o $@ $(LDLIBS)

build/test-can-lost: tests/test_can_lost.c src/canbus.c src/protocol.c $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_can_lost.c src/canbus.c src/protocol.c -o $@ $(LDLIBS)

build/test-camera: tests/test_camera.c src/camera.c src/camera.h src/canbus.c src/protocol.c vendor/cJSON.c $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_camera.c src/camera.c src/canbus.c src/protocol.c vendor/cJSON.c -o $@ $(LDLIBS)

# Offline controller tests: build/test-NAME links tests/test_NAME.c (dashes
# become underscores) with CORE. WRAP_NAME lists the boundaries the test
# replaces through -Wl,--wrap.
CONTROLLER_TESTS = speed-units control range-drift range-transition arrival-tolerance configuration-cache \
	idle-reconnect multi-lanes pose-entry-order startup reset-retry enable-sequence disable-sequence cycle \
	gripper obstruction-controller follow follow-noop trajectory-follow trajectory-viability teaching \
	teach-batches stationary recover-at-pose stop-generation hold-target record-load
FAKE_CLOCK = piper_now
FAKE_BUS = piper_now can_bus_receive can_bus_send
WRAP_arrival-tolerance = can_bus_receive can_bus_send
WRAP_idle-reconnect = $(FAKE_BUS) can_bus_reconnect can_bus_reconnect_device
WRAP_multi-lanes = $(FAKE_BUS)
WRAP_pose-entry-order = $(FAKE_BUS)
WRAP_startup = $(FAKE_CLOCK)
WRAP_reset-retry = $(FAKE_BUS)
WRAP_enable-sequence = $(FAKE_BUS)
WRAP_disable-sequence = $(FAKE_BUS)
WRAP_follow-noop = $(FAKE_BUS)
WRAP_trajectory-follow = $(FAKE_CLOCK)
WRAP_trajectory-viability = $(FAKE_BUS)
WRAP_teach-batches = $(FAKE_BUS)
WRAP_stationary = $(FAKE_CLOCK)
WRAP_recover-at-pose = $(FAKE_BUS)

comma := ,
.SECONDEXPANSION:
$(CONTROLLER_TESTS:%=build/test-%): build/test-%: tests/test_$$(subst -,_,$$*).c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) $< $(CORE) $(patsubst %,-Wl$(comma)--wrap=%,$(WRAP_$*)) -o $@ $(LDLIBS)

TESTS = $(CONTROLLER_TESTS) can-lost obstruction protocol camera
test: all $(TESTS:%=build/test-%)
	@rm -f build/test-skips.log
	for t in $(TESTS); do ./build/test-$$t >build/test-$$t.log 2>&1; rc=$$?; cat build/test-$$t.log; \
		grep "^SKIP" build/test-$$t.log >>build/test-skips.log; \
		[ $$rc -eq 0 ] || { echo "FAILED: build/test-$$t"; exit 1; }; done
	@# A skipped check still exits 0, so machines without its tool pass; say so loudly.
	@if [ -s build/test-skips.log ]; then echo "==== SKIPPED native checks ===="; cat build/test-skips.log; echo "==============================="; fi
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
	rm -rf build

test-vcan: build/piper-can
	bash tests/test_vcan.sh

test-record: build/piper-record
	python3 tests/test_record.py

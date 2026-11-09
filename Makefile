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
HEADERS = src/controller.h src/controller_internal.h src/controller_lane_fields.inc src/obstruction.h src/canbus.h src/protocol.h vendor/cJSON.h
build/piper-trace: src/trace.c src/canbus.c src/protocol.c $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) src/trace.c src/canbus.c src/protocol.c -o $@ $(LDLIBS)

build/piper-record: src/record.c src/canbus.c src/protocol.c vendor/cJSON.c $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) src/record.c src/canbus.c src/protocol.c vendor/cJSON.c -o $@ $(LDLIBS)

build/piper-control: src/server.c src/camera.c src/camera.h $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) src/server.c src/camera.c $(CORE) -o $@ $(LDLIBS)

build/test-protocol: tests/test_protocol.c src/protocol.c src/protocol.h | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_protocol.c src/protocol.c -o $@ $(LDLIBS)

build/test-speed-units: tests/test_speed_units.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_speed_units.c $(CORE) -o $@ $(LDLIBS)

build/test-control: tests/test_control.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_control.c $(CORE) -o $@ $(LDLIBS)

build/test-range-drift: tests/test_range_drift.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_range_drift.c $(CORE) -o $@ $(LDLIBS)

build/test-range-transition: tests/test_range_transition.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_range_transition.c $(CORE) -o $@ $(LDLIBS)

build/test-arrival-tolerance: tests/test_arrival_tolerance.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_arrival_tolerance.c $(CORE) -Wl,--wrap=can_bus_receive -Wl,--wrap=can_bus_send -o $@ $(LDLIBS)

build/test-configuration-cache: tests/test_configuration_cache.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_configuration_cache.c $(CORE) -o $@ $(LDLIBS)

build/test-idle-reconnect: tests/test_idle_reconnect.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_idle_reconnect.c $(CORE) -Wl,--wrap=piper_now -Wl,--wrap=can_bus_receive -Wl,--wrap=can_bus_reconnect -Wl,--wrap=can_bus_reconnect_device -Wl,--wrap=can_bus_send -o $@ $(LDLIBS)

test: build/test-idle-reconnect

build/test-multi-lanes: tests/test_multi_lanes.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_multi_lanes.c $(CORE) -Wl,--wrap=piper_now -Wl,--wrap=can_bus_receive -Wl,--wrap=can_bus_send -o $@ $(LDLIBS)

test: build/test-multi-lanes

build/test-pose-entry-order: tests/test_pose_entry_order.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_pose_entry_order.c $(CORE) -Wl,--wrap=piper_now -Wl,--wrap=can_bus_receive -Wl,--wrap=can_bus_send -o $@ $(LDLIBS)

test: build/test-pose-entry-order

build/test-camera: tests/test_camera.c src/camera.c src/camera.h src/canbus.c src/protocol.c vendor/cJSON.c $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_camera.c src/camera.c src/canbus.c src/protocol.c vendor/cJSON.c -o $@ $(LDLIBS)

build/test-startup: tests/test_startup.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_startup.c $(CORE) -Wl,--wrap=piper_now -o $@ $(LDLIBS)

build/test-reset-retry: tests/test_reset_retry.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_reset_retry.c $(CORE) -Wl,--wrap=piper_now -Wl,--wrap=can_bus_receive -Wl,--wrap=can_bus_send -o $@ $(LDLIBS)

build/test-enable-sequence: tests/test_enable_sequence.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_enable_sequence.c $(CORE) -Wl,--wrap=piper_now -Wl,--wrap=can_bus_receive -Wl,--wrap=can_bus_send -o $@ $(LDLIBS)

build/test-disable-sequence: tests/test_disable_sequence.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_disable_sequence.c $(CORE) -Wl,--wrap=piper_now -Wl,--wrap=can_bus_receive -Wl,--wrap=can_bus_send -o $@ $(LDLIBS)

test: build/test-disable-sequence

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

build/test-follow-noop: tests/test_follow_noop.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_follow_noop.c $(CORE) -Wl,--wrap=piper_now -Wl,--wrap=can_bus_receive -Wl,--wrap=can_bus_send -o $@ $(LDLIBS)

build/test-trajectory-follow: tests/test_trajectory_follow.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_trajectory_follow.c $(CORE) -Wl,--wrap=piper_now -o $@ $(LDLIBS)

build/test-trajectory-viability: tests/test_trajectory_viability.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_trajectory_viability.c src/canbus.c src/protocol.c vendor/cJSON.c -Wl,--wrap=piper_now -Wl,--wrap=can_bus_receive -Wl,--wrap=can_bus_send -o $@ $(LDLIBS)

build/test-streamed-position: tests/test_streamed_position.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) -DPIPER_ENABLE_EXPERIMENTAL_STREAMED_POSITION=1 tests/test_streamed_position.c $(CORE) -Wl,--wrap=piper_now -Wl,--wrap=can_bus_receive -Wl,--wrap=can_bus_send -o $@ $(LDLIBS)

build/test-streamed-bus: tests/test_streamed_bus.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) -DPIPER_ENABLE_EXPERIMENTAL_STREAMED_POSITION=1 tests/test_streamed_bus.c $(CORE) -Wl,--wrap=piper_now -o $@ $(LDLIBS)

build/test-streamed-disabled: tests/test_streamed_disabled.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_streamed_disabled.c $(CORE) -Wl,--wrap=piper_now -o $@ $(LDLIBS)

build/test-teaching: tests/test_teaching.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_teaching.c $(CORE) -o $@ $(LDLIBS)

build/test-teach-batches: tests/test_teach_batches.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_teach_batches.c $(CORE) -Wl,--wrap=piper_now -Wl,--wrap=can_bus_receive -Wl,--wrap=can_bus_send -o $@ $(LDLIBS)

build/test-stationary: tests/test_stationary.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_stationary.c $(CORE) -Wl,--wrap=piper_now -o $@ $(LDLIBS)

build/test-recover-at-pose: tests/test_recover_at_pose.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_recover_at_pose.c $(CORE) -Wl,--wrap=piper_now -Wl,--wrap=can_bus_receive -Wl,--wrap=can_bus_send -o $@ $(LDLIBS)

build/test-stop-generation: tests/test_stop_generation.c $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) tests/test_stop_generation.c $(CORE) -o $@ $(LDLIBS)

test: all build/test-protocol build/test-speed-units build/test-control build/test-camera build/test-startup build/test-reset-retry build/test-enable-sequence build/test-cycle build/test-gripper build/test-obstruction build/test-obstruction-controller build/test-range-drift build/test-range-transition build/test-arrival-tolerance build/test-configuration-cache build/test-follow build/test-follow-noop build/test-trajectory-follow build/test-trajectory-viability build/test-streamed-position build/test-streamed-bus build/test-streamed-disabled build/test-teaching build/test-teach-batches build/test-stop-generation build/test-stationary build/test-recover-at-pose
	./build/test-stationary
	./build/test-recover-at-pose
	./build/test-speed-units
	./build/test-trajectory-follow
	./build/test-trajectory-viability
	./build/test-streamed-position
	./build/test-streamed-bus
	./build/test-streamed-disabled
	./build/test-stop-generation
	./build/test-teaching
	./build/test-teach-batches
	./build/test-follow
	./build/test-follow-noop
	./build/test-configuration-cache
	./build/test-idle-reconnect
	./build/test-multi-lanes
	./build/test-pose-entry-order
	./build/test-range-drift
	./build/test-range-transition
	./build/test-arrival-tolerance
	./build/test-obstruction
	./build/test-obstruction-controller
	./build/test-protocol
	./build/test-control
	./build/test-camera
	./build/test-startup
	./build/test-reset-retry
	./build/test-enable-sequence
	./build/test-disable-sequence
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

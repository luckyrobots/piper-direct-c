CC ?= cc
CPPFLAGS += -Isrc -Ivendor -DCJSON_NESTING_LIMIT=32
LDLIBS += -lm
CFLAGS ?= -O2 -g
CFLAGS += -std=c17 -Wall -Wextra -Wpedantic -Werror

ifeq ($(OS),Windows_NT)
LDLIBS += -lsetupapi -lcfgmgr32
endif

.PHONY: all test test-web test-windows test-vcan test-record clean
all: build/piper-can build/piper-control build/piper-record build/piper-trace

build:
	mkdir -p build

CORE = src/controller.c src/canbus.c src/protocol.c vendor/cJSON.c
HEADERS = src/controller.h src/controller_internal.h src/controller_lane_fields.inc src/obstruction.h src/canbus.h src/protocol.h vendor/cJSON.h

build/piper-can: src/main.c src/protocol.c src/protocol.h | build
	$(CC) $(CPPFLAGS) $(CFLAGS) src/main.c src/protocol.c -o $@ $(LDLIBS)

build/piper-trace: src/trace.c src/canbus.c src/protocol.c $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) src/trace.c src/canbus.c src/protocol.c -o $@ $(LDLIBS)

build/piper-record: src/record.c src/canbus.c src/protocol.c vendor/cJSON.c $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) src/record.c src/canbus.c src/protocol.c vendor/cJSON.c -o $@ $(LDLIBS)

build/piper-control: src/server.c src/camera.c src/camera.h $(CORE) $(HEADERS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) src/server.c src/camera.c $(CORE) -o $@ $(LDLIBS)

# Native tests. build/test-NAME is tests/test_NAME.c (dashes become
# underscores), linked by the one rule below. What differs between tests is
# data:
#   BUS_TESTS    the controller tests. Each links CORE and the shared fake CAN
#                bus (tests/fake_bus.c: injected clock, MCU model, record of
#                emitted frames) through the FAKE_BUS link seams.
#   SRC_NAME     production sources of any other test (default: CORE).
#   WRAP_NAME    further boundaries a test replaces through -Wl,--wrap.
#   FLAGS_NAME, LIBS_NAME   extra compiler flags and libraries.
BUS_TESTS = speed-units control range-drift range-transition arrival-tolerance configuration-cache \
	idle-reconnect multi-lanes pose-entry-order startup reset-retry enable-sequence disable-sequence cycle \
	gripper obstruction-controller follow follow-noop trajectory-follow trajectory-viability teaching \
	teach-batches stationary recover-at-pose stop-generation hold-target record-load
FAKE_BUS = piper_now can_bus_receive can_bus_send
WRAP_idle-reconnect = can_bus_reconnect can_bus_reconnect_device
# The real clock is the unit under test, so this one keeps the real bus.
WRAP_clock-failure = clock_gettime
SRC_protocol = src/protocol.c
SRC_obstruction =
SRC_can-lost = src/canbus.c src/protocol.c
SRC_camera = src/camera.c src/canbus.c src/protocol.c vendor/cJSON.c
# The Windows transport tests include the transport source they fake.
SRC_can-windows-pump = src/protocol.c
SRC_can-windows-hotplug = src/controller.c src/protocol.c vendor/cJSON.c
SRC_candle-tx-pump-windows = vendor/candle/candle_ctrl_req.c
# It also includes the vendored driver, which is not held to our warnings.
FLAGS_candle-tx-pump-windows = -DCANDLE_API_LIBRARY -DUNICODE -D_UNICODE -Ivendor/candle -Wno-error
LIBS_candle-tx-pump-windows = -lwinusb -lole32

comma := ,
on_bus = $(filter $1,$(BUS_TESTS))
test_sources = $(if $(call on_bus,$1),$(CORE) tests/fake_bus.c,$(if $(filter undefined,$(origin SRC_$1)),$(CORE),$(SRC_$1)))
test_wraps = $(if $(call on_bus,$1),$(FAKE_BUS)) $(WRAP_$1)
# Any source or header change rebuilds every test; none is slow to build.
TEST_INPUTS = $(wildcard src/*.c src/*.h src/*.inc vendor/*.c vendor/*.h vendor/candle/*.c vendor/candle/*.h tests/*.h tests/fake_bus.c)
.SECONDEXPANSION:
build/test-%: tests/test_$$(subst -,_,$$*).c $(TEST_INPUTS) | build
	$(CC) $(CPPFLAGS) $(CFLAGS) $(FLAGS_$*) $< $(call test_sources,$*) $(patsubst %,-Wl$(comma)--wrap=%,$(call test_wraps,$*)) -o $@ $(LDLIBS) $(LIBS_$*)

TESTS = $(BUS_TESTS) clock-failure can-lost obstruction protocol camera
# NO_SKIPS=1 (CI) turns a skipped native check into a failure.
test: all $(TESTS:%=build/test-%)
	@rm -f build/test-skips.log
	for t in $(TESTS); do ./build/test-$$t >build/test-$$t.log 2>&1; rc=$$?; cat build/test-$$t.log; \
		grep "^SKIP" build/test-$$t.log >>build/test-skips.log; \
		[ $$rc -eq 0 ] || { echo "FAILED: build/test-$$t"; exit 1; }; done
	@# A skipped check still exits 0, so machines without its tool pass; say so loudly.
	@if [ -s build/test-skips.log ]; then echo "==== SKIPPED native checks ===="; cat build/test-skips.log; echo "==============================="; \
		[ -z "$(NO_SKIPS)" ] || { echo "FAILED: NO_SKIPS is set and native checks were skipped"; exit 1; }; fi
	./build/piper-can --help >/dev/null
	! ./build/piper-can --query-firmware >/dev/null 2>&1
	! ./build/piper-can --seconds nonsense >/dev/null 2>&1
	! ./build/piper-can --interface lo --query-firmware --seconds 1 >/dev/null 2>&1
	$(MAKE) test-web
	node tests/test_api.mjs

# Dashboard modules and their fixtures: Node only, no server and no compiler,
# so the same list runs on Linux and on Windows.
WEB_MODULES = web/app.js web/bench.mjs web/pika.mjs web/pika_remote.mjs
WEB_TESTS = pika_remote pika_windows_block pika pika_ui control_prompt camera_profiles
test-web:
	for m in $(WEB_MODULES); do node --check $$m || exit 1; done
	for t in $(WEB_TESTS); do node tests/test_$$t.mjs || { echo "FAILED: tests/test_$$t.mjs"; exit 1; }; done

# Windows transport fixtures, built with MinGW on Windows only. Every USB and
# process boundary is a fake callback: nothing enumerates or opens an adapter,
# starts a server or sends a robot command. The shared controller fixtures in
# the list are the ones that also run on Windows.
WINDOWS_TESTS = candle-tx-pump-windows can-windows-pump can-windows-hotplug idle-reconnect multi-lanes pose-entry-order disable-sequence
test-windows: $(WINDOWS_TESTS:%=build/test-%)
	for t in $(WINDOWS_TESTS); do ./build/test-$$t || { echo "FAILED: build/test-$$t"; exit 1; }; done

clean:
	rm -rf build

test-vcan: build/piper-can
	bash tests/test_vcan.sh

test-record: build/piper-record
	python3 tests/test_record.py

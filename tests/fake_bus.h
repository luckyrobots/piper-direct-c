#ifndef PIPER_TEST_FAKE_BUS_H
#define PIPER_TEST_FAKE_BUS_H
/* The one fake CAN bus of the offline controller tests.

   A test binary linked with tests/fake_bus.c and
   -Wl,--wrap=piper_now,--wrap=can_bus_receive,--wrap=can_bus_send (the
   Makefile does both for every controller test) gets:

   - an injected clock. piper_now() returns fake_bus.now, which only the test
     moves. Nothing reads the wall clock and nothing sleeps;
   - an MCU. By default the core's own simulator (canbus.c) answers, on the
     injected clock. A test that needs a different firmware model installs
     its own receive/send pair with fake_bus_model();
   - a script of feedback frames, each ingested by the production decoder at
     its time;
   - a record of every frame the controller emitted: when, to which device,
     whether the bus accepted it, and what control-state.json said about that
     device at that moment. The last part is what proves "persisted before
     sent";
   - transmit-failure injection.

   fake_bus_reset() returns all of it to the initial state, which is also the
   state a test process starts in. */
#include "controller_internal.h"

struct fake_frame {
    double at;
    unsigned device,batch;      /* bus slot; frames of one can_bus_send share a batch number */
    struct can_frame frame;
    bool accepted;              /* false: the bus refused or failed the batch */
    /* The device's persisted record when the frame was sent ("" when there is
       none, or when no controller is watched). */
    char state[16],kind[16];
};
typedef void fake_bus_receive_fn(struct can_bus *bus,double now);
typedef bool fake_bus_send_fn(struct can_bus *bus,struct can_device *device,unsigned generation,const struct can_frame *frames,size_t count);

struct fake_bus {
    double now;
    fake_bus_receive_fn *receive,*after_receive;
    fake_bus_send_fn *send;
    const struct controller *watched;
    struct fake_frame *frames; size_t count,capacity; unsigned batches;
    struct { unsigned device; double at; struct can_frame frame; bool delivered; } script[64]; unsigned scripted;
    int fail_device; unsigned fail_id,fail_times;
};
extern struct fake_bus fake_bus;

/* Forget the model, script, record and injected failures; set the clock. */
void fake_bus_reset(double now);
static inline double fake_bus_advance(double seconds) { return fake_bus.now+=seconds; }
/* Replace the built-in simulator with a test's MCU model. */
void fake_bus_model(fake_bus_receive_fn *receive,fake_bus_send_fn *send);
/* The built-in simulator, for a model that only decorates it. */
void fake_bus_simulator_receive(struct can_bus *bus,double now);
bool fake_bus_simulator_send(struct can_bus *bus,struct can_device *device,unsigned generation,const struct can_frame *frames,size_t count);
/* Deliver `frame` as feedback from bus slot `device` on the first receive at or after `at`. */
void fake_bus_feed(unsigned device,double at,struct can_frame frame);
/* Fail the next `times` batches to bus slot `device` (-1: any) that contain
   CAN ID `id` (0: any). The failure is recorded like a transport error. */
void fake_bus_fail(int device,unsigned id,unsigned times);
/* Record the persisted latch of this controller's devices with every frame. */
void fake_bus_watch(const struct controller *c);

/* The record. A mark is a position in it; helpers look at frames from a mark on. */
static inline size_t fake_bus_mark(void) { return fake_bus.count; }
/* The next recorded frame at or after *cursor for a slot (-1: any) and CAN ID
   (0: any); advances *cursor past it. NULL when there is none. */
const struct fake_frame *fake_bus_next(size_t *cursor,int device,unsigned id);
/* How many such frames were emitted since `mark`. */
unsigned fake_bus_sent(size_t mark,int device,unsigned id);
#endif

#ifndef PIPER_CANBUS_H
#define PIPER_CANBUS_H
#include "protocol.h"
#ifdef _WIN32
#define IF_NAMESIZE 32
#else
#include <net/if.h>
#endif
#define CANBUS_MAX 16
struct can_device {
    char name[IF_NAMESIZE], id[160], path[512], label[80];
    unsigned index, generation, bitrate, bus_state, control_flags;
    bool link_verified;
    unsigned long kernel_rx, kernel_tx;
    int fd;
    bool present, up, stable, simulated;
    char problem[160];
    unsigned long long rx, tx, local, errors;
    double last_rx, stationary_since, last_angles[6], frame_at, configuration_since;
    struct piper_feedback feedback;
};
struct can_bus {
    struct can_device devices[CANBUS_MAX];
    unsigned sequence;
    bool simulated;
    double sim_target[6], sim_gripper, sim_last;
    unsigned sim_enabled;
};
double piper_now(void);
void can_bus_init(struct can_bus *bus, bool simulated);
void can_bus_scan(struct can_bus *bus);
void can_bus_receive(struct can_bus *bus, double now);
void can_bus_close(struct can_bus *bus);
void can_bus_reconnect(struct can_bus *bus);
struct can_device *can_bus_find(struct can_bus *bus, const char *id);
bool can_bus_send(struct can_bus *bus, struct can_device *d, unsigned generation, const struct can_frame *frames, size_t count);
void can_bus_ingest(struct can_device *d, const struct can_frame *frame, bool local, double now);
void can_bus_invalidate_configuration(struct can_device *d, double now);
#endif

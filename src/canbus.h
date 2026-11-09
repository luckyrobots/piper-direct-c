#ifndef PIPER_CANBUS_H
#define PIPER_CANBUS_H
#include "protocol.h"
#ifdef _WIN32
#define IF_NAMESIZE 32
/* Values from the Linux CAN headers, used by the shared transport guards. */
#define CAN_ERR_BUSOFF 0x40u
#define CAN_STATE_BUS_OFF 3
#define CAN_CTRLMODE_LOOPBACK 1
#define CAN_CTRLMODE_LISTENONLY 2
#define CAN_CTRLMODE_FD 32
#else
#include <net/if.h>
#include <linux/can/error.h>
#include <linux/can/netlink.h>
#endif
#define CANBUS_MAX 16
#define CANBUS_STATIONARY_SAMPLES 5
struct can_tx_failure {
    bool seen;
    double at;
    unsigned generation, frame_index, batch_count, can_id, os_error;
    int transport_error;
    unsigned long long rx, tx, local, errors;
    char problem[160];
};
struct can_device {
    char name[IF_NAMESIZE], id[160], path[512], label[80];
    unsigned index, generation, bitrate, bus_state, control_flags;
    bool link_verified;
    unsigned long kernel_rx, kernel_tx;
    int fd;
    bool present, up, stable, simulated;
    char problem[160];
    unsigned long long rx, tx, local, errors;
    struct can_tx_failure tx_failure;
    double last_rx, stationary_since, last_angles[6], frame_at, configuration_since;
    /* Coherent pose window; last_angles remains a fixed quiet-pose anchor so
       filtering encoder noise cannot hide accumulated slow movement. */
    double stationary_samples[CANBUS_STATIONARY_SAMPLES][6], stationary_sample_at;
    double stationary_velocities[CANBUS_STATIONARY_SAMPLES][6];
    double pose_stationary_since, stationary_encoders[CANBUS_STATIONARY_SAMPLES][6], stationary_encoder_anchor[6];
    bool stationary_encoder_seen[6], strict_stationary_moving;
    unsigned stationary_count, stationary_next, stationary_generation;
    bool stationary_moving;
    struct piper_feedback feedback;
    /* Additional simulated devices own independent actuator state. Device zero
       retains the legacy bus simulation fields for source/API compatibility. */
    double sim_target[6], sim_gripper, sim_last;
    unsigned sim_enabled;
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
bool can_bus_init_simulated(struct can_bus *bus, unsigned count);
void can_bus_scan(struct can_bus *bus);
#ifdef _WIN32
void can_bus_check_connections(struct can_bus *bus);
#endif
void can_bus_receive(struct can_bus *bus, double now);
void can_bus_close(struct can_bus *bus);
void can_bus_reconnect(struct can_bus *bus);
/* Idle automatic recovery reopens only this adapter. Other live handles,
   connection generations and cached robot configuration remain unchanged. */
void can_bus_reconnect_device(struct can_bus *bus, struct can_device *device);
struct can_device *can_bus_find(struct can_bus *bus, const char *id);
bool can_bus_send(struct can_bus *bus, struct can_device *d, unsigned generation, const struct can_frame *frames, size_t count);
/* Pure production wire validation, also used before experimental admission.
   It never sends a frame or changes feedback/state. */
bool can_bus_batch_allowed(const struct can_device *d,const struct can_frame *frames,size_t count);
void can_bus_record_tx_failure(struct can_device *d,unsigned generation,unsigned frame_index,unsigned batch_count,unsigned can_id,unsigned os_error,int transport_error);
void can_bus_ingest(struct can_device *d, const struct can_frame *frame, bool local, double now);
void can_bus_invalidate_configuration(struct can_device *d, double now);
#endif

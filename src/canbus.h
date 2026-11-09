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
/* Stationary detector (C-B), advanced by each new six-joint observation:
     UNKNOWN     -> MOVING        first fresh window after a reset or loss
     MOVING      -> POSE_QUIET    a quiet, corroborated pose window
     POSE_QUIET  -> STRICT_STILL  joint velocities are quiet as well
     STRICT_STILL-> POSE_QUIET    velocity movement only
     any         -> MOVING        pose movement
     any         -> UNKNOWN       stale/invalid feedback, transport loss, reconnect
   pose_stationary_since is nonzero exactly in POSE_QUIET and STRICT_STILL,
   and stationary_since exactly in STRICT_STILL. Readers use the helpers. */
enum stationary_phase { STATIONARY_UNKNOWN, STATIONARY_MOVING, STATIONARY_POSE_QUIET, STATIONARY_STRICT_STILL };
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
    bool stationary_encoder_seen[6];
    unsigned stationary_count, stationary_next, stationary_generation;
    enum stationary_phase stationary_phase;
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
/* fd is CANBUS_CLOSED_FD when no transport is open. Offline fixtures mark a
   fake open transport with another negative value, so no OS call can ever
   reach a real descriptor. */
#define CANBUS_CLOSED_FD (-1)
static inline bool can_bus_transport_open(const struct can_device *d) { return d->simulated||d->fd!=CANBUS_CLOSED_FD; }
/* Seconds of strict stillness (power/reset/teach/arrival evidence), else 0. */
static inline double can_bus_strict_still_for(const struct can_device *d,double now)
{
    return d->stationary_phase==STATIONARY_STRICT_STILL?now-d->stationary_since:0;
}
/* Seconds of a quiet measured pose (ordinary position-control readiness), else 0. */
static inline double can_bus_pose_still_for(const struct can_device *d,double now)
{
    return d->stationary_phase>=STATIONARY_POSE_QUIET?now-d->pose_stationary_since:0;
}
/* Forget all stillness evidence; the next fresh observation restarts the window. */
void can_bus_forget_stationary(struct can_device *d);
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

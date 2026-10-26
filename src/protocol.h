#ifndef PIPER_PROTOCOL_H
#define PIPER_PROTOCOL_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <linux/can.h>

#define PIPER_FRESH_SECONDS 0.35

struct piper_sample {
    double deg[6], joint_at[6], status_at;
    bool joint_seen[6], status_seen;
    uint8_t control, arm_status, motion_mode, teach, motion_status;
    uint16_t error;
};

/* Three documented feedback address ranges, kept separate. */
struct piper_motor {
    bool seen, high_seen, enabled;
    uint8_t fault;
    double at, high_at, voltage, temperature, current, velocity;
};
struct piper_limit { bool seen; double min, max, speed, at; };
struct piper_acceleration { bool seen; double max, at; };
struct piper_feedback {
    struct piper_sample arm[3];
    struct piper_motor motor[6];
    struct piper_limit limit[6];
    struct piper_acceleration acceleration[6];
    double foreign_at, firmware_at, gripper_at, gripper_mm, gripper_effort;
    bool foreign_seen, gripper_seen, gripper_enabled;
    uint8_t gripper_fault;
    bool gripper_config_seen;
    uint8_t gripper_max_mm;
    double gripper_config_at;
    char firmware_raw[513], version[40], hardware[40], build[16];
    size_t firmware_len;
};

bool piper_decode(struct piper_feedback *state, uint32_t id,
                  const uint8_t *data, size_t len, double now);
bool piper_fresh(const struct piper_sample *state, double now);
bool piper_recent(double at, double now);
bool piper_targets(const double degrees[6], struct can_frame frames[3]);
struct can_frame piper_mode(unsigned speed_percent);
struct can_frame piper_power(unsigned joint, bool enabled);
struct can_frame piper_stop_frame(void);
struct can_frame piper_reset_frame(void);
bool piper_gripper(double mm, double effort_nm, bool enabled, struct can_frame *frame);
bool piper_supported(const struct piper_feedback *state);

#endif

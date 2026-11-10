#ifndef PIPER_PROTOCOL_H
#define PIPER_PROTOCOL_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#ifdef _WIN32
struct can_frame { uint32_t can_id; uint8_t len, pad[3]; uint8_t data[8]; };
#define CAN_ERR_FLAG 0x20000000u
#define CAN_EFF_FLAG 0x80000000u
#define CAN_RTR_FLAG 0x40000000u
#else
#include <linux/can.h>
#endif

#define PIPER_FRESH_SECONDS 0.35
#define PIPER_PI 3.141592653589793
/* Same evaluation order as the original inline (rad*180)/pi expressions, so
   admitted and re-checked trajectory limits stay bit-identical. */
#define RAD2DEG(rad) ((rad)*180/PIPER_PI)

/* 0x2A1 status codes used by the controller guards. */
enum {
    PIPER_ARM_STATUS_NORMAL=0, PIPER_ARM_STATUS_STOPPED=1, PIPER_ARM_STATUS_JOINT_COMMUNICATION=5,
    PIPER_ARM_STATUS_TEACHING=11, PIPER_ARM_STATUS_PLAYBACK=12, PIPER_ARM_STATUS_PLAYBACK_PAUSED=13
};
enum { PIPER_CONTROL_STANDBY=0, PIPER_CONTROL_CAN=1, PIPER_CONTROL_TEACH=2 };
/* Teach byte: 1 records (hand-guiding), 3/5/7 play back, 4 pauses playback;
   0, 2 and 6 are the idle/ended/terminated states. */
enum { PIPER_TEACH_RECORDING=1, PIPER_TEACH_PLAYBACK_PAUSED=4, PIPER_TEACH_LAST_KNOWN=7 };
static inline bool piper_teach_playback(uint8_t teach) { return teach==3||teach==5||teach==7; }
static inline bool piper_teach_active(uint8_t teach) { return teach==PIPER_TEACH_RECORDING||teach==PIPER_TEACH_PLAYBACK_PAUSED||piper_teach_playback(teach); }
static inline bool piper_teach_idle(uint8_t teach) { return teach==0||teach==2||teach==6; }
/* Another controller's command IDs seen on this bus (motion 0x150-0x18F,
   power 0x471, zeroing 0x470). */
static inline bool piper_foreign_command(uint32_t id) { return (id>=0x150&&id<=0x18f)||id==0x471||id==0x470; }

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
    int32_t position_raw;
};
struct piper_limit {
    bool seen, speed_raw_seen, speed_unit_known;
    uint16_t speed_raw;
    double min, max, speed, at, speed_resolution_rad_s;
    char speed_firmware[40];
};
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
/* Read-only configuration requests. */
struct can_frame piper_firmware_query(void);
struct can_frame piper_gripper_config_query(void);
/* joint is 1..6; acceleration selects the 0x47C reply instead of 0x473. */
struct can_frame piper_limit_query(unsigned joint, bool acceleration);
struct can_frame piper_stop_frame(void);
struct can_frame piper_reset_frame(void);
bool piper_gripper(double mm, double effort_nm, bool enabled, struct can_frame *frame);
bool piper_supported(const struct piper_feedback *state);
bool piper_seamless_teach_supported(const struct piper_feedback *state);

#endif

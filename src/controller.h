#ifndef PIPER_CONTROLLER_H
#define PIPER_CONTROLLER_H
#include "canbus.h"
#include "cJSON.h"
#include "obstruction.h"

enum operation { OP_IDLE, OP_ENABLE, OP_MOVE, OP_ALIGN, OP_CYCLE, OP_GRIPPER, OP_DISABLE, OP_RESET };
struct controller {
    struct can_bus bus;
    char selected[160], message[256], phase[32], error[256];
    char state_dir[512], legacy_dir[512], owner[81];
    cJSON *saved, *profiles, *legacy_stops;
    bool storage_failed;
    enum operation op;
    unsigned generation, request_id, enable_count;
    double started, heartbeat, last_tick, sent_at, settled_at, progress_at, best_error, final_sent_at;
    double target[6], command[6], initial[6], speed, tolerance, gripper_target, gripper_command;
    double gripper_effort;
    uint8_t gripper_max_mm;
    bool gripper_entry;
    bool synchronized_pose;
    bool following, follow_committed;
    double follow_updated;
    unsigned follow_sequence;
    unsigned cycle_stage;
    double leg_start[6], leg_span, leg_fraction, pause_until;
    bool query, disable_keeps_recovery;
    unsigned query_joint, query_generation;
    double query_start, query_next;
    double auto_reconnect_at, auto_query_at;
    struct obstruction_watch obstruction[6];
    unsigned obstruction_joint;
    double speed_percent;
};
bool controller_init(struct controller *c,bool simulated,const char *directory,const char *legacy);
void controller_close(struct controller *c);
void controller_tick(struct controller *c,double now);
const char *controller_blocker(struct controller *c,struct can_device *d,double now);
cJSON *controller_state(struct controller *c,const char *token);
cJSON *controller_action(struct controller *c,const char *route,const cJSON *body,int *status);
void controller_stop(struct controller *c,const char *reason);
#endif

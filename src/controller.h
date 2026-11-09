#ifndef PIPER_CONTROLLER_H
#define PIPER_CONTROLLER_H
#include "canbus.h"
#include "cJSON.h"
#include "obstruction.h"

/* Lane operation machine (C-A). op is the top-level phase and stage refines it:
     OP_IDLE --admission--> ENABLE | DISABLE | RESET | GRIPPER | MOVE | ALIGN | CYCLE | TEACH_RETURN
     ALIGN with range_followup --verified range entry--> MOVE
     CYCLE:  STAGE_CYCLE_RAISING -> STAGE_CYCLE_UPRIGHT -> STAGE_CYCLE_RETURNING -> IDLE
             (hold during CYCLE -> MOVE with STAGE_NONE)
     TEACH_RETURN, legacy:   STAGE_TEACH_STANDBY -> STAGE_TEACH_CAN -> IDLE
     TEACH_RETURN, seamless: STAGE_TEACH_SEEDED -> STAGE_TEACH_MODE -> IDLE
     MOVE follow:  FOLLOW_LIVE --follow-commit--> FOLLOW_COMMITTED; otherwise FOLLOW_NONE
   Every exit to OP_IDLE goes through lane_go_idle(), and lane_check() asserts
   that stage, follow and the remaining mode flags agree with op. */
enum operation { OP_IDLE, OP_ENABLE, OP_MOVE, OP_ALIGN, OP_CYCLE, OP_GRIPPER, OP_DISABLE, OP_RESET, OP_TEACH_RETURN };
enum lane_stage {
    STAGE_NONE, STAGE_CYCLE_RAISING, STAGE_CYCLE_UPRIGHT, STAGE_CYCLE_RETURNING,
    STAGE_TEACH_STANDBY, STAGE_TEACH_CAN, STAGE_TEACH_SEEDED, STAGE_TEACH_MODE
};
enum follow_phase { FOLLOW_NONE, FOLLOW_LIVE, FOLLOW_COMMITTED };
/* Teach-cycle evidence (C-D), volatile and bound to one connection generation:
     NONE -> SEEN        fresh, benign hand-guiding (teach 1, status 11)
     SEEN -> COMPLETED   a newer idle-teach status while SEEN is still fresh
     any  -> NONE        stale or unhealthy feedback, another teach state, a
                         STOP, a recovery attempt, 120 s after completion, or a
                         new connection generation */
enum teach_cycle { TEACH_CYCLE_NONE, TEACH_CYCLE_SEEN, TEACH_CYCLE_COMPLETED };
/* Read-only information query (C-E):
     IDLE -> INITIAL   start_query sends firmware + gripper requests
     INITIAL -> RETRY  after the twelve limit/acceleration requests
     RETRY -> IDLE     all configuration received, deadline, or connection change */
enum query_phase { QUERY_IDLE, QUERY_INITIAL, QUERY_RETRY };
struct controller_lane {
#include "controller_lane_fields.inc"
};
struct controller {
    struct can_bus bus;
    char selected[160], state_dir[512];
    cJSON *saved, *profiles, *legacy_stops;
    bool storage_failed;
    /* The serialized execution cursor contains only POD lane state. The bus
       and recovery store above are never copied or replaced when binding a lane.
       Anonymous aliases retain the native single-lane source/test interface. */
    union {
        struct controller_lane lane;
        struct {
#include "controller_lane_fields.inc"
        };
    };
    struct controller_lane lanes[CANBUS_MAX];
    unsigned next_request_id, next_tick_lane;
    double inventory_scanned;
    bool defer_transport_maintenance;
};
bool controller_init(struct controller *c,bool simulated,const char *directory,const char *legacy);
bool controller_init_simulated(struct controller *c,unsigned count,const char *directory,const char *legacy);
bool controller_any_motion_active(struct controller *c);
bool controller_any_query_active(struct controller *c);
void controller_close(struct controller *c);
void controller_tick(struct controller *c,double now);
const char *controller_blocker(struct controller *c,struct can_device *d,double now);
cJSON *controller_state(struct controller *c,const char *token);
cJSON *controller_action(struct controller *c,const char *route,const cJSON *body,int *status);
void controller_stop(struct controller *c,const char *reason);
#endif

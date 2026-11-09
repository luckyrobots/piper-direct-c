#ifndef PIPER_CONTROLLER_H
#define PIPER_CONTROLLER_H
#include "canbus.h"
#include "cJSON.h"
#include "obstruction.h"

enum operation { OP_IDLE, OP_ENABLE, OP_MOVE, OP_ALIGN, OP_CYCLE, OP_GRIPPER, OP_DISABLE, OP_RESET, OP_TEACH_RETURN };
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

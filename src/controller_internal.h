#ifndef PIPER_CONTROLLER_INTERNAL_H
#define PIPER_CONTROLLER_INTERNAL_H
/* White-box entry points for the offline native tests. Production callers use
   controller.h only; nothing here changes the HTTP/JSON control surface. */
#include "controller.h"

/* STOP recorded as a software obstruction (kind "obstruction"), the only
   incident that recover-at-pose may clear. controller_stop records "stop". */
void controller_stop_obstruction(struct controller *c,const char *reason);
/* The lane for device id, created on first use; NULL when id is empty or no
   slot is free. */
struct controller_lane *controller_lane(struct controller *c,const char *id);
/* One bounded, quantized trajectory reference interval (white-box tested). */
enum trajectory_step_result { TRAJECTORY_STEP_INVALID, TRAJECTORY_STEP_OK, TRAJECTORY_STEP_BOUNDARY };
enum trajectory_step_result trajectory_reference_step(struct controller_lane *l,struct can_device *d,double now,double dt);
#endif

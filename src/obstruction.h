#ifndef PIPER_OBSTRUCTION_H
#define PIPER_OBSTRUCTION_H
#include <math.h>
#include <stdbool.h>
/* Position-feedback stall detection, not a force sensor or safety-rated stop. */
struct obstruction_watch { double progress_position, progress_at; int direction; };
static inline void obstruction_reset(struct obstruction_watch *w, double now) {
    w->progress_position=0; w->progress_at=now; w->direction=0;
}
static inline bool obstruction_stalled(struct obstruction_watch *w, double now,
    double actual, double command, double target, double tolerance) {
    double error=fabs(target-actual);
    /* Synchronized moves give short-travel joints very small command steps.
       Do not call that a stall while tracking is within the accepted tolerance. */
    if (error<=tolerance || fabs(command-actual)<fmax(.05,tolerance)) {
        w->progress_position=actual; w->progress_at=now; w->direction=0; return false;
    }
    /* A dragged target can move faster than the arm. Measure encoder progress
       toward the commanded direction, not distance to that changing target.
       Reversing or replacing a target alone must never renew the stall timer. */
    int direction=command>actual?1:-1;
    if (w->direction!=direction) { w->progress_position=actual; w->direction=direction; }
    if (direction*(actual-w->progress_position)>.01) { w->progress_position=actual; w->progress_at=now; }
    return now-w->progress_at>=.5;
}
#endif

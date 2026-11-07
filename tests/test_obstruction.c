#include "obstruction.h"
#include <assert.h>
#include <stdio.h>
int main(void) {
    struct obstruction_watch w[6];
    for (unsigned j=0;j<6;j++) obstruction_reset(w+j,0);
    /* Frozen joint: never trip before .5 s, trip at the threshold. */
    assert(!obstruction_stalled(w,0,0,1,20,.2));
    assert(!obstruction_stalled(w,.499,0,1,20,.2));
    assert(obstruction_stalled(w,.5,0,1,20,.2));
    /* The leader can recede faster than a healthy 100% move. Remaining
       distance grows, but measured progress must keep the watch satisfied. */
    obstruction_reset(w,0);
    for (unsigned n=0;n<=100;n++) {
        double t=n*.02, actual=-30+17*t;
        assert(!obstruction_stalled(w,t,actual,actual+1,actual+10+30*t,.5));
    }
    /* Repeatedly reversing a frozen joint's target cannot postpone a stop. */
    obstruction_reset(w,0);
    for (unsigned n=0;n<50;n++) {
        double direction=n%2?1:-1;
        assert(!obstruction_stalled(w,n*.01,0,direction,20*direction,.2));
    }
    assert(obstruction_stalled(w,.5,0,1,20,.2));
    /* Other joints progressing must not hide the blocked joint. */
    for (unsigned n=1;n<=50;n++) {
        double t=n*.01;
        assert(!obstruction_stalled(w+1,t,t,t+.1,20,.2));
    }
    assert(obstruction_stalled(w,.51,0,1,20,.2));
    /* Slow movement in either direction remains valid at minimum speed. */
    for (unsigned dir=0;dir<2;dir++) {
        obstruction_reset(w,0); double sign=dir?-1:1;
        for(unsigned n=0;n<1000;n++) {
            double t=n*.01, actual=sign*t*.1;
            assert(!obstruction_stalled(w,t,actual,actual+sign*.06,sign*20,.2));
        }
    }
    /* Reproduce the slow synchronized all-zero J6 false positive: its final
       target is distant, but its commanded lead stays inside 0.5 degrees. */
    obstruction_reset(w,0);
    for (unsigned n=0;n<=100;n++) {
        double t=n*.01;
        assert(!obstruction_stalled(w,t,4.531,4.531-.111*t,0,.5));
    }
    /* Once tracking leaves tolerance, a frozen joint still trips in 500 ms. */
    assert(!obstruction_stalled(w,1.01,4.531,3.9,0,.5));
    assert(!obstruction_stalled(w,1.49,4.531,3.9,0,.5));
    assert(obstruction_stalled(w,1.51,4.531,3.9,0,.5));
    /* Settled or not-yet-commanded joints cannot cause a false stall. */
    obstruction_reset(w,0);
    assert(!obstruction_stalled(w,10,19.9,20,20,.2));
    assert(!obstruction_stalled(w,20,0,0,20,.2));
    /* Small oscillations cannot keep resetting the progress clock. */
    obstruction_reset(w,0); assert(!obstruction_stalled(w,0,0,1,20,.2));
    for(unsigned n=1;n<50;n++) assert(!obstruction_stalled(w,n*.01,(n%2)*.002,1,20,.2));
    assert(obstruction_stalled(w,.5,0,1,20,.2));
    puts("Obstruction checks passed: 500 ms threshold, independent joints, slow bidirectional progress, settling and jitter.");
}

#define _GNU_SOURCE
#include "fixture.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

struct fixture { struct controller c; char directory[80]; double now; };
static const double original[6]={-1,.5,-.5,44.8,31.3,164.8};
static void setup(struct fixture *f)
{
    fixture_directory(f->directory,sizeof f->directory,"piper-cycle");
    CHECK(controller_init(&f->c,true,f->directory,"")); f->now=piper_now();
    struct can_device *d=&f->c.bus.devices[0];
    memcpy(d->feedback.arm[0].deg,original,sizeof original); memcpy(f->c.bus.sim_target,original,sizeof original);
    f->c.bus.sim_enabled=63; controller_tick(&f->c,f->now);
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.8-2");
    fixture_vendor_limits(d,.3,f->now);
    fixture_still(d,f->now-1);
}
static int request(struct fixture *f,const char *action)
{
    struct can_device *d=&f->c.bus.devices[0]; cJSON *j=cJSON_CreateObject();
    cJSON_AddStringToObject(j,"device_id",d->id); cJSON_AddNumberToObject(j,"generation",d->generation);
    cJSON_AddStringToObject(j,"owner","cycle-regression-client"); cJSON_AddStringToObject(j,"action",action);
    cJSON_AddBoolToObject(j,"standalone",true); cJSON_AddBoolToObject(j,"clear",true);
    if (!strcmp(action,"pose")) {
        const double target[6]={4,10.5,-5.5,41.8,31.3,163.8};
        cJSON_AddItemToObject(j,"degrees",cJSON_CreateDoubleArray(target,6));
        cJSON_AddBoolToObject(j,"synchronized",true);
    }
    int status; cJSON *r=controller_action(&f->c,"/api/command",j,&status); cJSON_Delete(r); cJSON_Delete(j); return status;
}
static void tick(struct fixture *f,bool heartbeat)
{
    f->now+=.025; if (heartbeat) LANE(&f->c)->heartbeat=f->now; controller_tick(&f->c,f->now);
}
static void cleanup(struct fixture *f)
{
    controller_close(&f->c); char path[160]; snprintf(path,sizeof path,"%s/control-state.json",f->directory);
    CHECK(!unlink(path)); CHECK(!rmdir(f->directory));
}
static void reach_stage(struct fixture *f,enum lane_stage wanted)
{
    for (unsigned i=0;i<7000&&LANE(&f->c)->stage!=wanted&&LANE(&f->c)->op!=OP_IDLE;++i) tick(f,true);
    CHECK(LANE(&f->c)->op==OP_CYCLE&&LANE(&f->c)->stage==wanted);
}
int main(void)
{
    struct fixture f; setup(&f);
    CHECK(request(&f,"cycle")==202); CHECK(LANE(&f.c)->op==OP_CYCLE);
    CHECK(LANE(&f.c)->speed==3&&LANE(&f.c)->tolerance==.2);
    CHECK(!memcmp(LANE(&f.c)->initial,original,sizeof original));
    bool saw_pause=false,saw_return=false; double paused_at=0;
    for (unsigned t=0;t<7000&&LANE(&f.c)->op!=OP_IDLE;++t) {
        double before[6]; memcpy(before,LANE(&f.c)->command,sizeof before);
        enum lane_stage stage=LANE(&f.c)->stage; tick(&f,true);
        for (unsigned i=0;i<6;++i) CHECK(fabs(LANE(&f.c)->command[i]-before[i])<=.15+.001);
        CHECK(fabs(LANE(&f.c)->command[0]-original[0])<.001&&fabs(LANE(&f.c)->command[5]-original[5])<.001);
        if (LANE(&f.c)->stage==STAGE_CYCLE_UPRIGHT&&!saw_pause) { saw_pause=true; paused_at=f.now; }
        if (stage==STAGE_CYCLE_UPRIGHT&&LANE(&f.c)->stage==STAGE_CYCLE_RETURNING) { saw_return=true; CHECK(f.now-paused_at>=2); }
    }
    CHECK(saw_pause&&saw_return); CHECK(LANE(&f.c)->op==OP_IDLE&&!strcmp(LANE(&f.c)->phase,"complete"));
    for (unsigned i=0;i<6;++i) CHECK(fabs(f.c.bus.devices[0].feedback.arm[0].deg[i]-original[i])<.001);
    cleanup(&f);
    // Cancelling at any phase must never resume or begin an automatic return.
    for (enum lane_stage stage=STAGE_CYCLE_RAISING;stage<=STAGE_CYCLE_RETURNING;++stage) {
        setup(&f); CHECK(request(&f,"cycle")==202); reach_stage(&f,stage);
        controller_stop(&f.c,"Cycle stop fixture.");
        uint64_t tx=f.c.bus.devices[0].tx;
        for (unsigned i=0;i<100;++i) tick(&f,true);
        CHECK(LANE(&f.c)->op==OP_IDLE&&LANE(&f.c)->stage==STAGE_NONE&&f.c.bus.devices[0].tx==tx); cleanup(&f);
    }
    setup(&f); CHECK(request(&f,"cycle")==202);
    for (unsigned i=0;i<40;++i) tick(&f,false);
    CHECK(LANE(&f.c)->op==OP_IDLE&&strstr(LANE(&f.c)->message,"heartbeat expired")); cleanup(&f);
    setup(&f); CHECK(request(&f,"cycle")==202); CHECK(request(&f,"hold")==202);
    CHECK(LANE(&f.c)->op==OP_MOVE&&LANE(&f.c)->stage==STAGE_NONE);
    for (unsigned i=0;i<100&&LANE(&f.c)->op!=OP_IDLE;++i) tick(&f,true);
    CHECK(LANE(&f.c)->op==OP_IDLE&&!strcmp(LANE(&f.c)->phase,"complete")); cleanup(&f);
    setup(&f); f.c.bus.devices[0].feedback.limit[2].min=-100;
    CHECK(request(&f,"cycle")==400&&f.c.bus.devices[0].tx==0); cleanup(&f);
    setup(&f); f.c.bus.devices[0].feedback.arm[0].deg[1]=-2;
    CHECK(request(&f,"cycle")==409&&f.c.bus.devices[0].tx==0); cleanup(&f);
    setup(&f); CHECK(request(&f,"cycle")==202); reach_stage(&f,2);
    f.c.bus.devices[0].feedback.limit[1].min=60; tick(&f,true);
    CHECK(LANE(&f.c)->op==OP_IDLE&&strstr(LANE(&f.c)->message,"return pose")); cleanup(&f);
    setup(&f); CHECK(request(&f,"pose")==202&&(LANE(&f.c)->stage==STAGE_MOVE_SYNCHRONIZED));
    while (LANE(&f.c)->op!=OP_IDLE) {
        tick(&f,true);
        double fraction=(LANE(&f.c)->command[1]-original[1])/10;
        CHECK(fabs((LANE(&f.c)->command[0]-original[0])-fraction*5)<1e-8);
        CHECK(fabs((LANE(&f.c)->command[2]-original[2])+fraction*5)<1e-8);
        CHECK(fabs(LANE(&f.c)->command[4]-original[4])<1e-8);
    }
    CHECK(!strcmp(LANE(&f.c)->phase,"complete")); cleanup(&f);
    setup(&f); CHECK(request(&f,"pose")==202);
    CHECK(request(&f,"hold")==202&&!(LANE(&f.c)->stage==STAGE_MOVE_SYNCHRONIZED));
    for (unsigned i=0;i<100&&LANE(&f.c)->op!=OP_IDLE;++i) tick(&f,true);
    CHECK(LANE(&f.c)->op==OP_IDLE&&!strcmp(LANE(&f.c)->phase,"complete")); cleanup(&f);
    /* A broken C-A invariant on a live lane fails safe after a tick: STOP,
       a latched record, and the process keeps running. */
    setup(&f); CHECK(request(&f,"cycle")==202);
    LANE(&f.c)->stage=STAGE_MOVE_SYNCHRONIZED; /* not a CYCLE stage */
    tick(&f,true);
    CHECK(LANE(&f.c)->op==OP_IDLE&&!strcmp(LANE(&f.c)->phase,"stopped")&&strstr(LANE(&f.c)->message,"inconsistent")&&LANE(&f.c)->stop_delivered);
    CHECK(!strcmp(cJSON_GetStringValue(cJSON_GetObjectItem(cJSON_GetObjectItem(cJSON_GetObjectItem(f.c.saved,"devices"),f.c.bus.devices[0].id),"state")),"latched"));
    cleanup(&f);
    /* After an action, the reply reports the failure instead of its result. */
    setup(&f); LANE(&f.c)->pose_phase=POSE_ARRIVAL_HOLD; /* idle lane */
    {
        struct can_device *d=&f.c.bus.devices[0]; cJSON *j=cJSON_CreateObject(); int status;
        cJSON_AddStringToObject(j,"device_id",d->id); cJSON_AddNumberToObject(j,"generation",d->generation);
        cJSON *r=controller_action(&f.c,"/api/heartbeat",j,&status); cJSON_Delete(r); cJSON_Delete(j);
        CHECK(status==500&&LANE(&f.c)->pose_phase==POSE_NONE&&!strcmp(LANE(&f.c)->phase,"stopped"));
    }
    cleanup(&f);
    puts("Cycle passed: full upright/pause/return, captured pose, fixed axes, bounded synchronized steps, Stop in all phases, Hold, lease loss, invalid presets/start/return limits.");
}

#define _GNU_SOURCE
#include "controller.h"
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
    strcpy(f->directory,"/tmp/piper-cycle-XXXXXX"); assert(mkdtemp(f->directory));
    assert(controller_init(&f->c,true,f->directory,"")); f->now=piper_now();
    struct can_device *d=&f->c.bus.devices[0];
    memcpy(d->feedback.arm[0].deg,original,sizeof original); memcpy(f->c.bus.sim_target,original,sizeof original);
    f->c.bus.sim_enabled=63; controller_tick(&f->c,f->now);
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.8-2");
    const double limits[6][2]={{-150,150},{0,180},{-170,0},{-100,100},{-70,70},{-180,180}};
    for (unsigned i=0;i<6;++i) d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=limits[i][0],.max=limits[i][1],.speed=.3,.at=f->now};
    for (unsigned i=0;i<6;++i) d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=f->now};
    d->stationary_since=f->now-1;
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
    f->now+=.025; if (heartbeat) f->c.heartbeat=f->now; controller_tick(&f->c,f->now);
}
static void cleanup(struct fixture *f)
{
    controller_close(&f->c); char path[160]; snprintf(path,sizeof path,"%s/control-state.json",f->directory);
    assert(!unlink(path)); assert(!rmdir(f->directory));
}
static void reach_stage(struct fixture *f,unsigned wanted)
{
    for (unsigned i=0;i<7000&&f->c.cycle_stage!=wanted&&f->c.op!=OP_IDLE;++i) tick(f,true);
    assert(f->c.op==OP_CYCLE&&f->c.cycle_stage==wanted);
}
int main(void)
{
    struct fixture f; setup(&f);
    assert(request(&f,"cycle")==202); assert(f.c.op==OP_CYCLE);
    assert(f.c.speed==3&&f.c.tolerance==.2);
    assert(!memcmp(f.c.initial,original,sizeof original));
    bool saw_pause=false,saw_return=false; double paused_at=0;
    for (unsigned t=0;t<7000&&f.c.op!=OP_IDLE;++t) {
        double before[6]; memcpy(before,f.c.command,sizeof before);
        unsigned stage=f.c.cycle_stage; tick(&f,true);
        for (unsigned i=0;i<6;++i) assert(fabs(f.c.command[i]-before[i])<=.15+.001);
        assert(fabs(f.c.command[0]-original[0])<.001&&fabs(f.c.command[5]-original[5])<.001);
        if (f.c.cycle_stage==2&&!saw_pause) { saw_pause=true; paused_at=f.now; }
        if (stage==2&&f.c.cycle_stage==3) { saw_return=true; assert(f.now-paused_at>=2); }
    }
    assert(saw_pause&&saw_return); assert(f.c.op==OP_IDLE&&!strcmp(f.c.phase,"complete"));
    for (unsigned i=0;i<6;++i) assert(fabs(f.c.bus.devices[0].feedback.arm[0].deg[i]-original[i])<.001);
    cleanup(&f);
    // Cancelling at any phase must never resume or begin an automatic return.
    for (unsigned stage=1;stage<=3;++stage) {
        setup(&f); assert(request(&f,"cycle")==202); reach_stage(&f,stage);
        controller_stop(&f.c,"Cycle stop fixture.");
        uint64_t tx=f.c.bus.devices[0].tx;
        for (unsigned i=0;i<100;++i) tick(&f,true);
        assert(f.c.op==OP_IDLE&&f.c.cycle_stage==0&&f.c.bus.devices[0].tx==tx); cleanup(&f);
    }
    setup(&f); assert(request(&f,"cycle")==202);
    for (unsigned i=0;i<40;++i) tick(&f,false);
    assert(f.c.op==OP_IDLE&&strstr(f.c.message,"heartbeat expired")); cleanup(&f);
    setup(&f); assert(request(&f,"cycle")==202); assert(request(&f,"hold")==202);
    assert(f.c.op==OP_MOVE&&f.c.cycle_stage==0);
    for (unsigned i=0;i<100&&f.c.op!=OP_IDLE;++i) tick(&f,true);
    assert(f.c.op==OP_IDLE&&!strcmp(f.c.phase,"complete")); cleanup(&f);
    setup(&f); f.c.bus.devices[0].feedback.limit[2].min=-100;
    assert(request(&f,"cycle")==400&&f.c.bus.devices[0].tx==0); cleanup(&f);
    setup(&f); f.c.bus.devices[0].feedback.arm[0].deg[1]=-2;
    assert(request(&f,"cycle")==409&&f.c.bus.devices[0].tx==0); cleanup(&f);
    setup(&f); assert(request(&f,"cycle")==202); reach_stage(&f,2);
    f.c.bus.devices[0].feedback.limit[1].min=60; tick(&f,true);
    assert(f.c.op==OP_IDLE&&strstr(f.c.message,"return pose")); cleanup(&f);
    setup(&f); assert(request(&f,"pose")==202&&f.c.synchronized_pose);
    while (f.c.op!=OP_IDLE) {
        tick(&f,true);
        double fraction=(f.c.command[1]-original[1])/10;
        assert(fabs((f.c.command[0]-original[0])-fraction*5)<1e-8);
        assert(fabs((f.c.command[2]-original[2])+fraction*5)<1e-8);
        assert(fabs(f.c.command[4]-original[4])<1e-8);
    }
    assert(!strcmp(f.c.phase,"complete")); cleanup(&f);
    setup(&f); assert(request(&f,"pose")==202);
    assert(request(&f,"hold")==202&&!f.c.synchronized_pose);
    for (unsigned i=0;i<100&&f.c.op!=OP_IDLE;++i) tick(&f,true);
    assert(f.c.op==OP_IDLE&&!strcmp(f.c.phase,"complete")); cleanup(&f);
    puts("Cycle passed: full upright/pause/return, captured pose, fixed axes, bounded synchronized steps, Stop in all phases, Hold, lease loss, invalid presets/start/return limits.");
}

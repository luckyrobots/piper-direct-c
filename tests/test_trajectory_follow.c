#define _GNU_SOURCE
#include "controller.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

/* All protocol/receipt clocks use the same deterministic monotonic fixture.
   CPU load must not turn a reference-math test into a real 500-ms stale gap. */
static double test_clock=1000;
double __wrap_piper_now(void) { return test_clock; }

static cJSON *request(struct controller *c,const char *action,bool trajectory)
{
    struct can_device *d=&c->bus.devices[0]; cJSON *j=cJSON_CreateObject();
    cJSON_AddStringToObject(j,"device_id",d->id); cJSON_AddNumberToObject(j,"generation",d->generation);
    cJSON_AddStringToObject(j,"owner","trajectory-tests"); cJSON_AddStringToObject(j,"action",action);
    cJSON_AddNumberToObject(j,"request_id",c->request_id); cJSON_AddNumberToObject(j,"speed_percent",60);
    cJSON_AddBoolToObject(j,"standalone",true); cJSON_AddBoolToObject(j,"clear",true);
    if (trajectory) cJSON_AddBoolToObject(j,"trajectory",true);
    return j;
}
static void action(struct controller *c,cJSON *j,int expected)
{
    int status; cJSON *reply=controller_action(c,"/api/command",j,&status);
    if (status!=expected) { char *s=cJSON_PrintUnformatted(reply); fprintf(stderr,"%d expected %d: %s\n",status,expected,s); free(s); }
    assert(status==expected); cJSON_Delete(reply); cJSON_Delete(j);
}
static void setup(struct controller *c,char *directory)
{
    assert(mkdtemp(directory)); assert(controller_init(c,true,directory,""));
    struct can_device *d=&c->bus.devices[0];
    memset(c->bus.sim_target,0,sizeof c->bus.sim_target); memset(d->feedback.arm[0].deg,0,sizeof d->feedback.arm[0].deg);
    c->bus.sim_enabled=63; double now=piper_now(); controller_tick(c,now);
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.8-2");
    for (unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=-12,.max=12,.speed=.06+.01*i,.at=now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=.1+.05*i,.at=now};
    }
    d->stationary_since=now-1;
}
static void cleanup(struct controller *c,const char *directory)
{
    controller_close(c); char path[256]; snprintf(path,sizeof path,"%s/control-state.json",directory);
    assert(!unlink(path)); assert(!rmdir(directory));
}
static double tick(struct controller *c,double now,double dt)
{
    now+=dt; test_clock=now; c->follow_updated=c->heartbeat=now; controller_tick(c,now); return now;
}
int main(void)
{
    struct controller c; char directory[]="/tmp/piper-trajectory-XXXXXX"; setup(&c,directory);
    action(&c,request(&c,"follow-start",true),202); assert(c.trajectory_follow&&c.following);
    cJSON *update=request(&c,"follow-update",false);
    cJSON_AddNumberToObject(update,"sequence",1); double goal[6]={8,-8,8,-8,8,-8};
    cJSON_AddItemToObject(update,"degrees",cJSON_CreateDoubleArray(goal,6)); action(&c,update,202);
    cJSON *old=request(&c,"follow-update",false); cJSON_AddNumberToObject(old,"sequence",1);
    cJSON_AddItemToObject(old,"degrees",cJSON_CreateDoubleArray(goal,6)); action(&c,old,409); assert(c.follow_sequence==1);
    cJSON *commit=request(&c,"follow-commit",false); action(&c,commit,409); assert(c.following);
    double now=c.last_tick,previous_velocity[6]={0},previous_dt=0;
    for (unsigned n=0;n<1200;++n) {
        double before[6]; memcpy(before,c.command,sizeof before);
        double dt=n%3==0?.025:n%3==1?.035:.05;
        if (n==180||n==360) for (unsigned i=0;i<6;++i) c.target[i]=-c.target[i];
        if (n==540) for (unsigned i=0;i<6;++i) c.target[i]=.07;
        now=tick(&c,now,dt);
        if (c.op!=OP_MOVE) fprintf(stderr,"tick %u: %s\n",n,c.message);
        assert(c.op==OP_MOVE&&c.trajectory_follow);
        double actual_dt=c.trajectory_previous_dt,midpoint=previous_dt>0?(previous_dt+actual_dt)/2:actual_dt;
        for (unsigned i=0;i<6;++i) {
            double velocity=(c.command[i]-before[i])/actual_dt;
            assert(fabs(velocity)<=c.trajectory_speed[i]+1e-7);
            assert(fabs(velocity-previous_velocity[i])/midpoint<=c.trajectory_acceleration[i]+1e-6);
            assert(fabs(c.command[i]-c.bus.sim_target[i])<1e-9); /* Actual encoded CAN target. */
            assert(c.command[i]>=-12&&c.command[i]<=12);
            previous_velocity[i]=velocity;
        }
        previous_dt=actual_dt;
    }
    for (unsigned i=0;i<6;++i) assert(fabs(c.command[i]-.07)<.003);
    cJSON *state=controller_state(&c,"not-secret"),*motion=cJSON_GetObjectItem(state,"motion");
    assert(cJSON_IsTrue(cJSON_GetObjectItem(state,"trajectory_follow_supported")));
    assert(cJSON_IsTrue(cJSON_GetObjectItem(motion,"trajectory_follow")));
    assert(cJSON_GetArraySize(cJSON_GetObjectItem(motion,"reference_velocity_deg_s"))==6); cJSON_Delete(state);
    test_clock=c.follow_updated+.251; controller_tick(&c,test_clock);
    assert(c.op==OP_IDLE&&!c.trajectory_follow&&c.bus.sim_enabled==63&&strstr(c.message,"expired"));
    cleanup(&c,directory);

    char boundary[]="/tmp/piper-trajectory-boundary-XXXXXX"; setup(&c,boundary);
    action(&c,request(&c,"follow-start",true),202); now=c.last_tick;
    for (unsigned n=0;n<1500;++n) {
        for (unsigned i=0;i<6;++i) c.target[i]=n<500?12:-12;
        now=tick(&c,now,.025);
        if (c.op!=OP_MOVE) fprintf(stderr,"boundary tick %u: J6 cmd %.9f vel %.9f actual %.9f\n",n,c.command[5],c.trajectory_velocity[5],c.bus.devices[0].feedback.arm[0].deg[5]);
        assert(c.op==OP_MOVE&&c.trajectory_follow);
        for (unsigned i=0;i<6;++i) assert(c.command[i]>=-12&&c.command[i]<=12);
    }
    for (unsigned i=0;i<6;++i) assert(fabs(c.command[i]+12)<.003);
    test_clock=c.follow_updated+.251; controller_tick(&c,test_clock); cleanup(&c,boundary);

    char changed[]="/tmp/piper-trajectory-limit-XXXXXX"; setup(&c,changed);
    action(&c,request(&c,"follow-start",true),202);
    c.target[0]=5; now=tick(&c,c.last_tick,.025);
    c.bus.devices[0].feedback.acceleration[0].max=.2;
    tick(&c,now,.025); assert(c.op==OP_IDLE&&!c.trajectory_follow&&strstr(c.message,"limits changed")); cleanup(&c,changed);

    char stale[]="/tmp/piper-trajectory-stale-XXXXXX"; setup(&c,stale);
    action(&c,request(&c,"follow-start",true),202); c.bus.devices[0].feedback.acceleration[3].seen=false;
    tick(&c,c.last_tick,.025); assert(c.op==OP_IDLE&&!c.trajectory_follow&&strstr(c.message,"limits changed")); cleanup(&c,stale);

    char range[]="/tmp/piper-trajectory-range-XXXXXX"; setup(&c,range);
    c.bus.devices[0].feedback.arm[0].deg[0]=-12.05;
    action(&c,request(&c,"follow-start",true),409); assert(c.op==OP_IDLE); cleanup(&c,range);

    char invalid[]="/tmp/piper-trajectory-invalid-XXXXXX"; setup(&c,invalid);
    c.bus.devices[0].feedback.acceleration[0].max=0; action(&c,request(&c,"follow-start",true),409); assert(c.op==OP_IDLE);
    c.bus.devices[0].feedback.acceleration[0].max=.001; action(&c,request(&c,"follow-start",true),409); assert(c.op==OP_IDLE);
    c.bus.devices[0].feedback.acceleration[0].max=.5;
    cJSON *bad=request(&c,"follow-start",false); cJSON_AddStringToObject(bad,"trajectory","yes"); action(&c,bad,400);
    action(&c,request(&c,"pose",true),400); assert(c.op==OP_IDLE);
    action(&c,request(&c,"follow-start",false),202); assert(!c.trajectory_follow); cleanup(&c,invalid);

    char fault[]="/tmp/piper-trajectory-fault-XXXXXX"; setup(&c,fault);
    action(&c,request(&c,"follow-start",true),202); c.target[0]=4;
    now=tick(&c,c.last_tick,.025); c.bus.devices[0].feedback.arm[0].arm_status=1;
    tick(&c,now,.025); assert(c.op==OP_IDLE&&!c.trajectory_follow); cleanup(&c,fault);

    char delayed[]="/tmp/piper-trajectory-delayed-XXXXXX"; setup(&c,delayed);
    action(&c,request(&c,"follow-start",true),202); c.target[0]=4;
    now=tick(&c,c.last_tick,.025); uint64_t before_hold=c.bus.devices[0].tx;
    now=tick(&c,now,.053);
    assert(c.op==OP_IDLE&&!c.trajectory_follow&&strstr(c.message,"tick exceeded")&&!strcmp(c.phase,"complete")&&c.bus.sim_enabled==63);
    assert(c.bus.devices[0].tx-before_hold<=4); /* At most one complete measured hold; no delayed trajectory replay. */
    for (unsigned i=0;i<6;++i) assert(c.target[i]==c.bus.devices[0].feedback.arm[0].deg[i]);
    state=controller_state(&c,"test"); cJSON *device=cJSON_GetArrayItem(cJSON_GetObjectItem(state,"devices"),0);
    assert(cJSON_IsFalse(cJSON_GetObjectItem(device,"recovery_required"))); cJSON_Delete(state);
    before_hold=c.bus.devices[0].tx;
    for (unsigned i=0;i<10;++i) now=tick(&c,now,.025);
    assert(c.op==OP_IDLE&&c.bus.devices[0].tx==before_hold); /* Refresh/input cannot revive the retired trajectory. */
    cleanup(&c,delayed);

    char lead[]="/tmp/piper-trajectory-lead-XXXXXX"; setup(&c,lead);
    action(&c,request(&c,"follow-start",true),202); c.target[0]=10;
    now=c.last_tick;
    for (unsigned n=0;n<100&&c.op!=OP_IDLE;++n) {
        c.bus.sim_target[0]=0; now=tick(&c,now,.025);
    }
    assert(c.op==OP_IDLE&&!c.trajectory_follow); cleanup(&c,lead);
    puts("Trajectory following: six encoded-reference speed/acceleration caps, reversal, variable dt, expiry hold, limits/fault cancellation and legacy isolation passed.");
}

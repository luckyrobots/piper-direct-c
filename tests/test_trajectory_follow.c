#define _GNU_SOURCE
#include "fixture.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

/* All protocol/receipt clocks use the same deterministic monotonic fixture.
   CPU load must not turn a reference-math test into a real 500-ms stale gap. */
static double test_clock=1000;
FIXTURE_WRAP_PIPER_NOW(test_clock)

static cJSON *request(struct controller *c,const char *action,bool trajectory)
{
    struct can_device *d=&c->bus.devices[0]; cJSON *j=cJSON_CreateObject();
    cJSON_AddStringToObject(j,"device_id",d->id); cJSON_AddNumberToObject(j,"generation",d->generation);
    cJSON_AddStringToObject(j,"owner","trajectory-tests"); cJSON_AddStringToObject(j,"action",action);
    cJSON_AddNumberToObject(j,"request_id",LANE(c)->request_id); cJSON_AddNumberToObject(j,"speed_percent",60);
    cJSON_AddBoolToObject(j,"standalone",true); cJSON_AddBoolToObject(j,"clear",true);
    if (trajectory) cJSON_AddBoolToObject(j,"trajectory",true);
    return j;
}
static void action(struct controller *c,cJSON *j,int expected)
{
    int status; cJSON *reply=controller_action(c,"/api/command",j,&status);
    if (status!=expected) { char *s=cJSON_PrintUnformatted(reply); fprintf(stderr,"%d expected %d: %s\n",status,expected,s); free(s); }
    CHECK(status==expected); cJSON_Delete(reply); cJSON_Delete(j);
}
static void setup(struct controller *c,char *directory)
{
    CHECK(mkdtemp(directory)); CHECK(controller_init(c,true,directory,""));
    struct can_device *d=&c->bus.devices[0];
    memset(c->bus.sim_target,0,sizeof c->bus.sim_target); memset(d->feedback.arm[0].deg,0,sizeof d->feedback.arm[0].deg);
    c->bus.sim_enabled=63; double now=piper_now(); controller_tick(c,now);
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.8-2");
    for (unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=-12,.max=12,.speed=.06+.01*i,.at=now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=.1+.05*i,.at=now};
    }
    fixture_still(d,now-1);
}
static void cleanup(struct controller *c,const char *directory)
{
    fixture_cleanup(c,directory);
}
static double tick(struct controller *c,double now,double dt)
{
    now+=dt; test_clock=now; LANE(c)->follow_updated=LANE(c)->heartbeat=now; controller_tick(c,now); return now;
}
int main(void)
{
    struct controller c; char directory[]="/tmp/piper-trajectory-XXXXXX"; setup(&c,directory);
    /* The removed streamed-position transport is no longer a known name: it is
       a malformed request like any other unknown transport, before any intent or TX. */
    unsigned long long tx_before=c.bus.devices[0].tx;
    cJSON *streamed=request(&c,"follow-start",true); cJSON_AddStringToObject(streamed,"trajectory_transport","streamed-position");
    action(&c,streamed,400); CHECK(LANE(&c)->op==OP_IDLE&&c.bus.devices[0].tx==tx_before);
    cJSON *unknown=request(&c,"follow-start",true); cJSON_AddStringToObject(unknown,"trajectory_transport","other");
    action(&c,unknown,400); CHECK(LANE(&c)->op==OP_IDLE&&c.bus.devices[0].tx==tx_before);
    action(&c,request(&c,"follow-start",true),202); CHECK((LANE(&c)->stage==STAGE_MOVE_TRAJECTORY)&&LANE(&c)->follow==FOLLOW_LIVE);
    cJSON *update=request(&c,"follow-update",false);
    cJSON_AddNumberToObject(update,"sequence",1); double goal[6]={8,-8,8,-8,8,-8};
    cJSON_AddItemToObject(update,"degrees",cJSON_CreateDoubleArray(goal,6)); action(&c,update,202);
    cJSON *old=request(&c,"follow-update",false); cJSON_AddNumberToObject(old,"sequence",1);
    cJSON_AddItemToObject(old,"degrees",cJSON_CreateDoubleArray(goal,6)); action(&c,old,409); CHECK(LANE(&c)->follow_sequence==1);
    cJSON *commit=request(&c,"follow-commit",false); action(&c,commit,409); CHECK(LANE(&c)->follow==FOLLOW_LIVE);
    double now=LANE(&c)->last_tick,previous_velocity[6]={0},previous_dt=0;
    for (unsigned n=0;n<1200;++n) {
        double before[6]; memcpy(before,LANE(&c)->command,sizeof before);
        double dt=n%3==0?.025:n%3==1?.035:.05;
        if (n==180||n==360) for (unsigned i=0;i<6;++i) LANE(&c)->target[i]=-LANE(&c)->target[i];
        if (n==540) for (unsigned i=0;i<6;++i) LANE(&c)->target[i]=.07;
        now=tick(&c,now,dt);
        if (LANE(&c)->op!=OP_MOVE) fprintf(stderr,"tick %u: %s\n",n,LANE(&c)->message);
        CHECK(LANE(&c)->op==OP_MOVE&&(LANE(&c)->stage==STAGE_MOVE_TRAJECTORY));
        double actual_dt=LANE(&c)->trajectory_previous_dt,midpoint=previous_dt>0?(previous_dt+actual_dt)/2:actual_dt;
        for (unsigned i=0;i<6;++i) {
            double velocity=(LANE(&c)->command[i]-before[i])/actual_dt;
            CHECK(fabs(velocity)<=LANE(&c)->trajectory_speed[i]+1e-7);
            CHECK(fabs(velocity-previous_velocity[i])/midpoint<=LANE(&c)->trajectory_acceleration[i]+1e-6);
            CHECK(fabs(LANE(&c)->command[i]-c.bus.sim_target[i])<1e-9); /* Actual encoded CAN target. */
            CHECK(LANE(&c)->command[i]>=-12&&LANE(&c)->command[i]<=12);
            previous_velocity[i]=velocity;
        }
        previous_dt=actual_dt;
    }
    for (unsigned i=0;i<6;++i) CHECK(fabs(LANE(&c)->command[i]-.07)<.003);
    cJSON *state=controller_state(&c,"not-secret"),*motion=cJSON_GetObjectItem(state,"motion");
    CHECK(cJSON_IsTrue(cJSON_GetObjectItem(state,"trajectory_follow_supported")));
    CHECK(cJSON_IsTrue(cJSON_GetObjectItem(motion,"trajectory_follow")));
    CHECK(cJSON_GetArraySize(cJSON_GetObjectItem(motion,"reference_velocity_deg_s"))==6); cJSON_Delete(state);
    test_clock=LANE(&c)->follow_updated+.251; controller_tick(&c,test_clock);
    CHECK(LANE(&c)->op==OP_IDLE&&!(LANE(&c)->stage==STAGE_MOVE_TRAJECTORY)&&c.bus.sim_enabled==63&&strstr(LANE(&c)->message,"expired"));
    cleanup(&c,directory);

    char boundary[]="/tmp/piper-trajectory-boundary-XXXXXX"; setup(&c,boundary);
    action(&c,request(&c,"follow-start",true),202); now=LANE(&c)->last_tick;
    for (unsigned n=0;n<1500;++n) {
        for (unsigned i=0;i<6;++i) LANE(&c)->target[i]=n<500?12:-12;
        now=tick(&c,now,.025);
        if (LANE(&c)->op!=OP_MOVE) fprintf(stderr,"boundary tick %u: J6 cmd %.9f vel %.9f actual %.9f\n",n,LANE(&c)->command[5],LANE(&c)->trajectory_velocity[5],c.bus.devices[0].feedback.arm[0].deg[5]);
        CHECK(LANE(&c)->op==OP_MOVE&&(LANE(&c)->stage==STAGE_MOVE_TRAJECTORY));
        for (unsigned i=0;i<6;++i) CHECK(LANE(&c)->command[i]>=-12&&LANE(&c)->command[i]<=12);
    }
    for (unsigned i=0;i<6;++i) CHECK(fabs(LANE(&c)->command[i]+12)<.003);
    test_clock=LANE(&c)->follow_updated+.251; controller_tick(&c,test_clock); cleanup(&c,boundary);

    char changed[]="/tmp/piper-trajectory-limit-XXXXXX"; setup(&c,changed);
    action(&c,request(&c,"follow-start",true),202);
    LANE(&c)->target[0]=5; now=tick(&c,LANE(&c)->last_tick,.025);
    c.bus.devices[0].feedback.acceleration[0].max=.2;
    tick(&c,now,.025); CHECK(LANE(&c)->op==OP_IDLE&&!(LANE(&c)->stage==STAGE_MOVE_TRAJECTORY)&&strstr(LANE(&c)->message,"limits changed")); cleanup(&c,changed);

    char stale[]="/tmp/piper-trajectory-stale-XXXXXX"; setup(&c,stale);
    action(&c,request(&c,"follow-start",true),202); c.bus.devices[0].feedback.acceleration[3].seen=false;
    tick(&c,LANE(&c)->last_tick,.025); CHECK(LANE(&c)->op==OP_IDLE&&!(LANE(&c)->stage==STAGE_MOVE_TRAJECTORY)&&strstr(LANE(&c)->message,"limits changed")); cleanup(&c,stale);

    char range[]="/tmp/piper-trajectory-range-XXXXXX"; setup(&c,range);
    c.bus.devices[0].feedback.arm[0].deg[0]=-12.05;
    action(&c,request(&c,"follow-start",true),409); CHECK(LANE(&c)->op==OP_IDLE); cleanup(&c,range);

    char invalid[]="/tmp/piper-trajectory-invalid-XXXXXX"; setup(&c,invalid);
    c.bus.devices[0].feedback.acceleration[0].max=0; action(&c,request(&c,"follow-start",true),409); CHECK(LANE(&c)->op==OP_IDLE);
    c.bus.devices[0].feedback.acceleration[0].max=.001; action(&c,request(&c,"follow-start",true),409); CHECK(LANE(&c)->op==OP_IDLE);
    c.bus.devices[0].feedback.acceleration[0].max=.5;
    cJSON *bad=request(&c,"follow-start",false); cJSON_AddStringToObject(bad,"trajectory","yes"); action(&c,bad,400);
    action(&c,request(&c,"pose",true),400); CHECK(LANE(&c)->op==OP_IDLE);
    action(&c,request(&c,"follow-start",false),202); CHECK(!(LANE(&c)->stage==STAGE_MOVE_TRAJECTORY)); cleanup(&c,invalid);

    char fault[]="/tmp/piper-trajectory-fault-XXXXXX"; setup(&c,fault);
    action(&c,request(&c,"follow-start",true),202); LANE(&c)->target[0]=4;
    now=tick(&c,LANE(&c)->last_tick,.025); c.bus.devices[0].feedback.arm[0].arm_status=1;
    tick(&c,now,.025); CHECK(LANE(&c)->op==OP_IDLE&&!(LANE(&c)->stage==STAGE_MOVE_TRAJECTORY)); cleanup(&c,fault);

    char delayed[]="/tmp/piper-trajectory-delayed-XXXXXX"; setup(&c,delayed);
    action(&c,request(&c,"follow-start",true),202); LANE(&c)->target[0]=4;
    now=tick(&c,LANE(&c)->last_tick,.025); uint64_t before_hold=c.bus.devices[0].tx;
    now=tick(&c,now,.053);
    CHECK(LANE(&c)->op==OP_IDLE&&!(LANE(&c)->stage==STAGE_MOVE_TRAJECTORY)&&strstr(LANE(&c)->message,"tick exceeded")&&!strcmp(LANE(&c)->phase,"complete")&&c.bus.sim_enabled==63);
    CHECK(c.bus.devices[0].tx-before_hold<=4); /* At most one complete measured hold; no delayed trajectory replay. */
    for (unsigned i=0;i<6;++i) CHECK(LANE(&c)->target[i]==c.bus.devices[0].feedback.arm[0].deg[i]);
    state=controller_state(&c,"test"); cJSON *device=cJSON_GetArrayItem(cJSON_GetObjectItem(state,"devices"),0);
    CHECK(cJSON_IsFalse(cJSON_GetObjectItem(device,"recovery_required"))); cJSON_Delete(state);
    before_hold=c.bus.devices[0].tx;
    for (unsigned i=0;i<10;++i) now=tick(&c,now,.025);
    CHECK(LANE(&c)->op==OP_IDLE&&c.bus.devices[0].tx==before_hold); /* Refresh/input cannot revive the retired trajectory. */
    cleanup(&c,delayed);

    char lead[]="/tmp/piper-trajectory-lead-XXXXXX"; setup(&c,lead);
    action(&c,request(&c,"follow-start",true),202); LANE(&c)->target[0]=10;
    now=LANE(&c)->last_tick;
    for (unsigned n=0;n<100&&LANE(&c)->op!=OP_IDLE;++n) {
        c.bus.sim_target[0]=0; now=tick(&c,now,.025);
    }
    CHECK(LANE(&c)->op==OP_IDLE&&!(LANE(&c)->stage==STAGE_MOVE_TRAJECTORY)); cleanup(&c,lead);
    puts("Trajectory following: six encoded-reference speed/acceleration caps, reversal, variable dt, expiry hold, limits/fault cancellation and legacy isolation passed.");
}

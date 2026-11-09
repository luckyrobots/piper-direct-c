#define _GNU_SOURCE
#include "fixture.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

/* A supported, unpowered joint may move slightly while another driver wakes.
   Model independent delayed acknowledgements instead of ideal instant power. */
static double clock_now=100,first_wire[6],last_wire[6],wire_pose[6],ack_delay[6],low_stamp[6];
static unsigned power_count[6],stops;
static double previous_power;
static bool pump_before_completion;
FIXTURE_WRAP_PIPER_NOW(clock_now)
void __wrap_can_bus_receive(struct can_bus *bus,double now)
{
    struct can_device *d=&bus->devices[0]; struct piper_sample *s=&d->feedback.arm[0];
    s->status_seen=true; s->status_at=now;
    for (unsigned i=0;i<6;++i) {
        struct piper_motor *m=&d->feedback.motor[i];
        if (first_wire[i]&&ack_delay[i]>=0&&now-first_wire[i]>=ack_delay[i]) m->enabled=true;
        s->joint_seen[i]=true; s->joint_at[i]=now;
        m->seen=m->high_seen=true; m->at=low_stamp[i]>=0?low_stamp[i]:now; m->high_at=now;
        m->position_raw=(int32_t)llround(s->deg[i]*1000*3.141592653589793/180);
    }
}
bool __wrap_can_bus_send(struct can_bus *bus,struct can_device *d,unsigned generation,const struct can_frame *frames,size_t count)
{
    CHECK(bus->simulated&&d&&generation==d->generation&&count==1);
    const struct can_frame *f=&frames[0]; CHECK(f->len==8);
    if (f->can_id==0x471) {
        CHECK(f->data[0]>=1&&f->data[0]<=6&&f->data[1]==2);
        for (unsigned b=2;b<8;++b) CHECK(!f->data[b]);
        unsigned i=f->data[0]-1;
        if (previous_power) CHECK(clock_now-previous_power>=.02-1e-9);
        if (power_count[i]) CHECK(clock_now-last_wire[i]>=.15-1e-9);
        else { first_wire[i]=clock_now; wire_pose[i]=d->feedback.arm[0].deg[i]; }
        ++power_count[i]; last_wire[i]=previous_power=clock_now;
        if (pump_before_completion&&i==0&&power_count[i]==1) {
            clock_now+=.001;
            d->feedback.motor[i].enabled=true; d->feedback.motor[i].at=low_stamp[i]=clock_now;
            d->feedback.arm[0].joint_at[i]=d->feedback.motor[i].high_at=clock_now;
            clock_now+=.001; /* Simulate receive pumping before the OUT write completes. */
        }
    } else {
        /* A failed guard can STOP; no mode, position, reset, broadcast or
           gripper packet is ever part of power-only enabling. */
        CHECK(f->can_id==0x150&&f->data[0]==1);
        for (unsigned b=1;b<8;++b) CHECK(!f->data[b]);
        ++stops;
    }
    ++d->tx; return true;
}
static void fixture_pose(struct controller *c,char directory[96],const double *resting_pose)
{
    fixture_directory(directory,96,"piper-enable-sequence");
    clock_now+=10; CHECK(controller_init(c,true,directory,""));
    memset(first_wire,0,sizeof first_wire); memset(last_wire,0,sizeof last_wire);
    memset(power_count,0,sizeof power_count); previous_power=0; stops=0;
    pump_before_completion=false;
    struct can_device *d=&c->bus.devices[0];
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.8-2");
    d->feedback.arm[0].motion_mode=1; fixture_still(d,clock_now-1);
    const double limits[6][2]={{-150,150},{0,180},{-170,0},{-100,100},{-70,70},{-180,180}};
    const double pose[6]={2,25,-25,-20,60,8};
    memcpy(d->feedback.arm[0].deg,resting_pose?resting_pose:pose,sizeof pose);
    for (unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=limits[i][0],.max=limits[i][1],.speed=.3,.at=clock_now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=clock_now};
        d->feedback.motor[i].enabled=false; ack_delay[i]=.025; low_stamp[i]=-1;
    }
    __wrap_can_bus_receive(&c->bus,clock_now);
    cJSON *body=cJSON_CreateObject();
    cJSON_AddStringToObject(body,"device_id",d->id); cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddStringToObject(body,"owner","enable-sequence-regression-client"); cJSON_AddStringToObject(body,"action","enable");
    cJSON_AddBoolToObject(body,"standalone",true); cJSON_AddBoolToObject(body,"supported",true); cJSON_AddBoolToObject(body,"clear",true);
    int status; cJSON *reply=controller_action(c,"/api/command",body,&status);
    CHECK(status==202&&LANE(c)->op==OP_ENABLE); cJSON_Delete(reply); cJSON_Delete(body);
}
static void fixture(struct controller *c,char directory[96]) { fixture_pose(c,directory,NULL); }
static void tick(struct controller *c)
{
    clock_now+=.025; LANE(c)->heartbeat=clock_now; controller_tick(c,clock_now);
}
static void move(struct controller *c,unsigned joint,double degrees)
{
    struct can_device *d=&c->bus.devices[0]; d->feedback.arm[0].deg[joint]=degrees; fixture_still(d,clock_now);
}
static void complete(struct controller *c)
{
    for (unsigned tick_count=0;tick_count<400&&LANE(c)->op!=OP_IDLE;++tick_count) tick(c);
    CHECK(LANE(c)->op==OP_IDLE&&!strcmp(LANE(c)->phase,"complete")&&stops==0);
    for (unsigned i=0;i<6;++i) CHECK(LANE(c)->power_acknowledged[i]&&LANE(c)->enable_baseline[i]==wire_pose[i]);
}
static void close_fixture(struct controller *c,const char *directory)
{
    CHECK(LANE(c)->op==OP_IDLE); fixture_cleanup(c,directory);
}
int main(void)
{
    struct controller c; char directory[96]; fixture(&c,directory);
    /* Delayed J1 cannot starve the five later drivers. No completion can reuse
       stationary time from before their own acknowledgement/settling window. */
    ack_delay[0]=.6;
    for (unsigned i=0;i<6;++i) tick(&c);
    for (unsigned i=0;i<6;++i) CHECK(power_count[i]==1&&first_wire[i]<=LANE(&c)->started+.151);
    CHECK(!LANE(&c)->power_acknowledged[0]);
    double all_ack_at=0;
    for (unsigned n=0;n<100&&LANE(&c)->op!=OP_IDLE;++n) {
        tick(&c); bool all=true; for (unsigned i=0;i<6;++i) all=all&&LANE(&c)->power_acknowledged[i];
        if (all&&!all_ack_at) all_ack_at=clock_now;
    }
    CHECK(LANE(&c)->op==OP_IDLE&&!strcmp(LANE(&c)->phase,"complete")&&all_ack_at&&clock_now-all_ack_at>=.4-1e-9&&stops==0);
    for (unsigned i=1;i<6;++i) CHECK(power_count[i]==1);
    close_fixture(&c,directory);

    /* Resting overshoot is not a reason to make the operator lift the arm.
       Enable remains power-only at this measured pose; the separately
       requested zero action owns bounded inward range entry afterwards. */
    const double resting_pose[6]={0,-2.064,2.105,0,21.346,0};
    fixture_pose(&c,directory,resting_pose); complete(&c);
    for (unsigned i=0;i<6;++i) CHECK(power_count[i]==1&&fabs(wire_pose[i]-resting_pose[i])<1e-9);
    CHECK(c.bus.devices[0].feedback.arm[0].deg[1]<0&&c.bus.devices[0].feedback.arm[0].deg[2]>0);
    close_fixture(&c,directory);

    fixture(&c,directory); tick(&c); /* Only J1 has received a request. */
    move(&c,3,-19.445); tick(&c); /* J4 moves .555 degrees while still free. */
    CHECK(LANE(&c)->op==OP_ENABLE&&!LANE(&c)->power_requested[3]);
    complete(&c); CHECK(fabs(LANE(&c)->enable_baseline[3]+19.445)<1e-9&&LANE(&c)->initial[3]==-20);
    close_fixture(&c,directory);

    fixture(&c,directory); tick(&c); move(&c,3,-18.999); tick(&c);
    CHECK(LANE(&c)->op==OP_IDLE&&stops==1&&power_count[3]==0&&LANE(&c)->obstruction_joint==4&&strstr(LANE(&c)->message,"before its enable request"));
    close_fixture(&c,directory);

    fixture(&c,directory); for (unsigned i=0;i<4;++i) tick(&c);
    CHECK(LANE(&c)->power_requested[3]); move(&c,3,LANE(&c)->enable_baseline[3]+.501); tick(&c);
    CHECK(LANE(&c)->op==OP_IDLE&&stops==1&&LANE(&c)->obstruction_joint==4&&strstr(LANE(&c)->message,"after its enable request"));
    close_fixture(&c,directory);

    fixture(&c,directory); for (unsigned i=0;i<6;++i) ack_delay[i]=-1;
    tick(&c); double frozen=LANE(&c)->enable_baseline[0],first=LANE(&c)->power_first_sent_at[0];
    move(&c,0,frozen+.25);
    for (unsigned i=0;i<12;++i) tick(&c);
    CHECK(power_count[0]>=2&&LANE(&c)->enable_baseline[0]==frozen&&LANE(&c)->power_first_sent_at[0]==first);
    move(&c,0,frozen+.501); tick(&c);
    CHECK(LANE(&c)->op==OP_IDLE&&stops==1&&LANE(&c)->obstruction_joint==1); close_fixture(&c,directory);

    fixture(&c,directory); ack_delay[0]=-1; tick(&c);
    c.bus.devices[0].feedback.motor[0].enabled=true; low_stamp[0]=first_wire[0];
    for (unsigned i=0;i<4;++i) tick(&c);
    CHECK(LANE(&c)->op==OP_ENABLE&&!LANE(&c)->power_acknowledged[0]); /* Same-time sample is not a post-request ACK. */
    low_stamp[0]=-1; tick(&c); CHECK(LANE(&c)->power_acknowledged[0]); complete(&c); close_fixture(&c,directory);

    fixture(&c,directory); pump_before_completion=true; ack_delay[0]=-1; tick(&c);
    CHECK(low_stamp[0]<LANE(&c)->power_first_sent_at[0]);
    tick(&c); CHECK(!LANE(&c)->power_acknowledged[0]); /* A pumped sample before actual write completion is not an ACK. */
    low_stamp[0]=-1; tick(&c); CHECK(LANE(&c)->power_acknowledged[0]); complete(&c); close_fixture(&c,directory);

    fixture(&c,directory); tick(&c); tick(&c); CHECK(LANE(&c)->power_acknowledged[0]);
    ack_delay[0]=-1; c.bus.devices[0].feedback.motor[0].enabled=false; unsigned count=power_count[0]; tick(&c);
    CHECK(LANE(&c)->op==OP_IDLE&&stops==1&&power_count[0]==count&&LANE(&c)->obstruction_joint==1&&strstr(LANE(&c)->message,"no automatic re-enable"));
    close_fixture(&c,directory);

    fixture(&c,directory); for (unsigned i=0;i<6;++i) ack_delay[i]=-1;
    tick(&c); clock_now=LANE(&c)->started+8.01; LANE(&c)->heartbeat=clock_now; controller_tick(&c,clock_now);
    CHECK(LANE(&c)->op==OP_IDLE&&stops==1&&strstr(LANE(&c)->message,"Enable timed out")); close_fixture(&c,directory);
    puts("Enable sequencing passed: paced round-robin power only, per-driver retries/fresh ACKs, bounded free support motion, frozen requested baselines, lost-power guard and post-ACK settling.");
}

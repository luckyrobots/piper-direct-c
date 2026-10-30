#define _GNU_SOURCE
#include "controller.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static const double limits[6][2]={{-150,150},{0,180},{-170,0},{-100,100},{-70,70},{-180,180}};
static const double pose[6]={0,-2.332,-.056,0,0,-.833};
static char directory[96];

static void init(struct controller *c)
{
    strcpy(directory,"/tmp/piper-range-transition-XXXXXX"); assert(mkdtemp(directory));
    assert(controller_init(c,true,directory,""));
    struct can_device *d=&c->bus.devices[0]; double now=piper_now();
    memcpy(d->feedback.arm[0].deg,pose,sizeof pose); memcpy(c->bus.sim_target,pose,sizeof pose);
    c->bus.sim_enabled=63; controller_tick(c,now);
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.8-2");
    for (unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=limits[i][0],.max=limits[i][1],.speed=.3,.at=now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=now};
    }
    d->stationary_since=now-1;
}
static void close_fixture(struct controller *c)
{
    controller_close(c);
    char path[128]; snprintf(path,sizeof path,"%s/control-state.json",directory);
    assert(!unlink(path)); assert(!rmdir(directory));
}
static int command(struct controller *c,const char *action,unsigned joint,double degrees)
{
    struct can_device *d=&c->bus.devices[0]; cJSON *body=cJSON_CreateObject();
    bool stale_hold=!strcmp(action,"stale-hold");
    if (stale_hold) action="hold";
    cJSON_AddStringToObject(body,"device_id",d->id); cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddStringToObject(body,"owner","range-transition-regression"); cJSON_AddStringToObject(body,"action",action);
    cJSON_AddBoolToObject(body,"standalone",true); cJSON_AddBoolToObject(body,"clear",true);
    cJSON_AddNumberToObject(body,"speed_percent",100); cJSON_AddNumberToObject(body,"joint",joint); cJSON_AddNumberToObject(body,"degrees",degrees);
    if (!strcmp(action,"hold")) cJSON_AddNumberToObject(body,"request_id",stale_hold?c->request_id-1:c->request_id);
    int status; cJSON *reply=controller_action(c,"/api/command",body,&status);
    cJSON_Delete(reply); cJSON_Delete(body); return status;
}
static void drive(struct controller *c)
{
    double now=c->started; unsigned request=c->request_id; bool destination_seen=false;
    for (unsigned tick=0;tick<1000&&c->op!=OP_IDLE;++tick) {
        now+=.03; c->heartbeat=now; controller_tick(c,now);
        for (unsigned i=0;i<6;++i) assert(c->bus.sim_target[i]>=limits[i][0]&&c->bus.sim_target[i]<=limits[i][1]);
        if (c->op==OP_MOVE) {
            destination_seen=true; assert(!c->range_followup && c->synchronized_pose && c->speed_percent==100);
            assert(c->bus.devices[0].feedback.arm[0].deg[1]>=0); /* Never advance before entry is measured. */
        }
        assert(c->request_id==request); /* One explicit lease, not an automatic new command. */
    }
    assert(destination_seen && c->op==OP_IDLE && !strcmp(c->phase,"complete") && !c->range_followup);
    assert(c->bus.sim_enabled==63);
}
int main(void)
{
    struct controller c; init(&c); struct can_device *d=&c.bus.devices[0];
    cJSON *state=controller_state(&c,"test"),*device=cJSON_GetArrayItem(cJSON_GetObjectItem(state,"devices"),0);
    cJSON *range=cJSON_GetObjectItem(device,"range_recovery"),*joints=cJSON_GetObjectItem(device,"joints");
    assert(cJSON_IsTrue(cJSON_GetObjectItem(range,"required"))&&cJSON_IsTrue(cJSON_GetObjectItem(range,"can_move_to_zero")));
    assert(fabs(cJSON_GetObjectItem(range,"correction_deg")->valuedouble-2.832)<1e-9);
    assert(cJSON_IsTrue(cJSON_GetObjectItem(cJSON_GetArrayItem(joints,1),"can_move_inward")));
    assert(cJSON_IsTrue(cJSON_GetObjectItem(cJSON_GetArrayItem(joints,1),"can_move_positive")));
    assert(cJSON_IsFalse(cJSON_GetObjectItem(cJSON_GetArrayItem(joints,1),"can_move_negative")));
    assert(cJSON_IsFalse(cJSON_GetObjectItem(cJSON_GetArrayItem(joints,5),"can_move")));
    assert(strstr(cJSON_GetObjectItem(cJSON_GetArrayItem(joints,5),"motion_blocker")->valuestring,"J2"));
    cJSON_Delete(state);
    uint64_t tx=d->tx; assert(command(&c,"joint",6,1)==409 && d->tx==tx);
    assert(command(&c,"jog",2,-1)==400 && d->tx==tx);
    assert(command(&c,"jog",2,1)==202 && c.range_followup && c.op==OP_ALIGN);
    assert(c.range_followup_target[1]==1 && c.range_followup_target[5]==pose[5]);
    drive(&c); assert(c.target[1]==1 && c.target[5]==pose[5]); close_fixture(&c);

    init(&c); assert(command(&c,"move-to-zero",0,0)==202); drive(&c);
    for (unsigned i=0;i<6;++i) assert(c.target[i]==0); /* No encoder zero or padded endpoint substituted. */
    close_fixture(&c);
    init(&c); d=&c.bus.devices[0];
    d->feedback.arm[0].deg[2]=c.bus.sim_target[2]=1;
    tx=d->tx; assert(command(&c,"joint",2,1)==409&&d->tx==tx); /* Never silently fix another joint. */
    assert(command(&c,"move-to-zero",0,0)==202); drive(&c);
    for (unsigned i=0;i<6;++i) assert(c.target[i]==0);
    close_fixture(&c);

    for (unsigned failure=0;failure<7;++failure) {
        init(&c); d=&c.bus.devices[0]; tx=d->tx;
        if (failure==0) d->feedback.arm[0].status_at=piper_now()-1;
        if (failure==1) d->feedback.motor[3].fault=1;
        if (failure==2) d->feedback.arm[0].arm_status=5;
        if (failure==3) d->present=false;
        if (failure==4) d->feedback.motor[4].enabled=false;
        if (failure==5) d->feedback.limit[1].seen=false;
        if (failure==6) d->feedback.arm[0].deg[1]=-3.001;
        assert(command(&c,"move-to-zero",0,0)==409 && !c.range_followup && c.op==OP_IDLE && d->tx==tx);
        close_fixture(&c);
    }
    /* STOP/hold/teaching/scope loss discard the explicitly queued destination. */
    for (unsigned cancel=0;cancel<7;++cancel) {
        init(&c); d=&c.bus.devices[0]; assert(command(&c,"move-to-zero",0,0)==202 && c.range_followup);
        tx=d->tx; assert(command(&c,"stale-hold",0,0)==409&&c.range_followup&&d->tx==tx);
        if (cancel==0) controller_stop(&c,"Operator stop fixture.");
        if (cancel==1) assert(command(&c,"hold",0,0)==200);
        double now=piper_now()+.03;
        if (cancel==2) { d->feedback.arm[0].control=2; d->feedback.arm[0].teach=1; d->feedback.arm[0].arm_status=11; }
        if (cancel==3) ++d->generation;
        if (cancel==4) d->present=false;
        if (cancel==5) d->feedback.limit[1].min=1; /* Pending range target lost its reported legality. */
        c.heartbeat=cancel==6?now-1:now; controller_tick(&c,now);
        assert(c.op==OP_IDLE&&!c.range_followup);
        tx=d->tx;
        d->present=true; d->feedback.arm[0].control=0; d->feedback.arm[0].teach=0; d->feedback.arm[0].arm_status=0;
        for (unsigned tick=0;tick<30;++tick) { now+=.03; controller_tick(&c,now); }
        assert(c.op==OP_IDLE&&!c.range_followup&&d->tx==tx); /* Refresh is never a resume trigger. */
        close_fixture(&c);
    }
    init(&c); d=&c.bus.devices[0]; assert(command(&c,"move-to-zero",0,0)==202);
    double frozen_now=c.started;
    for (unsigned tick=0;tick<30&&c.op!=OP_IDLE;++tick) {
        memcpy(c.bus.sim_target,d->feedback.arm[0].deg,sizeof c.bus.sim_target);
        frozen_now+=.03; c.heartbeat=frozen_now; controller_tick(&c,frozen_now);
    }
    assert(c.op==OP_IDLE&&!c.range_followup&&strstr(c.message,"Possible obstruction"));
    close_fixture(&c);
    puts("Range transition passed: selected inward control, verified slow entry then zero, strict full-pose limits, fresh/fault guards and cancellation without destination replay.");
}

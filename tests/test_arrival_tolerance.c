#define _GNU_SOURCE
#include "controller.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

/* Inject a controller whose last fraction of a degree is quantized, and whose
   MCU busy bit remains latched even after measured motion has stopped. */
enum injection { NONE, BUSY, NONSTATIONARY, VELOCITY, PRETARGET_VELOCITY, STALE, FAULT, HOLD_STALE, HOLD_UNSTABLE, HOLD_ACK_IDLE, HOLD_FROZEN_TIMES };
static enum injection injection;
static struct controller *subject;
static unsigned stops,power;
static double post_hold_timestamp;
void __real_can_bus_receive(struct can_bus *,double);
bool __real_can_bus_send(struct can_bus *,struct can_device *,unsigned,const struct can_frame *,size_t);
void __wrap_can_bus_receive(struct can_bus *bus,double now)
{
    if (injection!=NONE) memcpy(bus->sim_target,bus->devices[0].feedback.arm[0].deg,sizeof bus->sim_target);
    __real_can_bus_receive(bus,now);
    if (injection==NONE) return;
    struct can_device *d=&bus->devices[0];
    d->feedback.arm[0].motion_status=injection==HOLD_ACK_IDLE&&subject->arrival_hold?0:1;
    if (injection==NONSTATIONARY||(injection==HOLD_UNSTABLE&&subject->arrival_hold)) d->stationary_since=now;
    if (injection==VELOCITY||(injection==HOLD_UNSTABLE&&subject->arrival_hold)) d->feedback.motor[0].velocity=.02;
    if (injection==PRETARGET_VELOCITY) d->feedback.motor[0].high_at=subject->started;
    if (injection==STALE||(injection==HOLD_STALE&&subject->arrival_hold)) d->feedback.arm[0].joint_at[2]=now-.6;
    if (injection==FAULT) d->feedback.motor[1].fault=1;
    if (injection==HOLD_FROZEN_TIMES&&subject->arrival_hold) {
        if (!post_hold_timestamp) post_hold_timestamp=now;
        d->feedback.arm[0].status_at=post_hold_timestamp;
        for (unsigned i=0;i<6;++i) { d->feedback.arm[0].joint_at[i]=post_hold_timestamp; d->feedback.motor[i].high_at=post_hold_timestamp; }
    }
}
bool __wrap_can_bus_send(struct can_bus *bus,struct can_device *d,unsigned generation,const struct can_frame *frames,size_t count)
{
    for (size_t i=0;i<count;++i) {
        if (frames[i].can_id==0x150) ++stops;
        if (frames[i].can_id==0x471) ++power;
    }
    return __real_can_bus_send(bus,d,generation,frames,count);
}
struct fixture { struct controller c; char directory[96]; double now; };
static void setup(struct fixture *f,double speed,double target)
{
    injection=NONE; stops=power=0; post_hold_timestamp=0; subject=&f->c;
    strcpy(f->directory,"/tmp/piper-arrival-tolerance-XXXXXX"); assert(mkdtemp(f->directory));
    assert(controller_init(&f->c,true,f->directory,""));
    struct can_device *d=&f->c.bus.devices[0]; f->now=piper_now();
    memset(d->feedback.arm[0].deg,0,sizeof d->feedback.arm[0].deg); memset(f->c.bus.sim_target,0,sizeof f->c.bus.sim_target);
    f->c.bus.sim_enabled=63; controller_tick(&f->c,f->now);
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.8-2");
    for (unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=-180,.max=180,.speed=.3,.at=f->now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=f->now};
    }
    d->stationary_since=f->now-1;
    cJSON *body=cJSON_CreateObject();
    cJSON_AddStringToObject(body,"device_id",d->id); cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddStringToObject(body,"owner","arrival-tolerance-regression"); cJSON_AddStringToObject(body,"action","joint");
    cJSON_AddBoolToObject(body,"standalone",true); cJSON_AddBoolToObject(body,"clear",true);
    cJSON_AddNumberToObject(body,"speed_percent",speed); cJSON_AddNumberToObject(body,"tolerance_deg",.2);
    cJSON_AddNumberToObject(body,"joint",6); cJSON_AddNumberToObject(body,"degrees",target);
    int status; cJSON *reply=controller_action(&f->c,"/api/command",body,&status);
    assert(status==202); cJSON_Delete(reply); cJSON_Delete(body); f->now=f->c.started;
}
static void tick(struct fixture *f)
{
    f->now+=.025; f->c.heartbeat=f->now; controller_tick(&f->c,f->now);
}
static void cleanup(struct fixture *f)
{
    injection=NONE; controller_close(&f->c);
    char path[128]; snprintf(path,sizeof path,"%s/control-state.json",f->directory);
    assert(!unlink(path)); assert(!rmdir(f->directory));
}
static void reach_hold(struct fixture *f)
{
    setup(f,10,.167); injection=BUSY;
    for (unsigned i=0;i<60&&!f->c.arrival_hold;++i) tick(f);
    assert(f->c.arrival_hold&&f->c.op==OP_MOVE&&stops==0);
}
int main(void)
{
    struct fixture f;
    for (unsigned variant=0;variant<2;++variant) {
        setup(&f,10,.167); injection=variant?HOLD_ACK_IDLE:BUSY;
        double start=f.now; bool saw_hold=false;
        for (unsigned i=0;i<100&&f.c.op!=OP_IDLE;++i) {
            tick(&f); if (f.c.arrival_hold) saw_hold=true;
            assert(stops==0&&power==0);
            if (f.c.arrival_hold) assert(f.c.command[5]==0&&f.c.bus.sim_target[5]==0);
        }
        assert(saw_hold&&f.c.op==OP_IDLE&&!strcmp(f.c.phase,"complete"));
        assert(f.now-start>=.8&&f.now-start<2 && f.c.bus.sim_enabled==63);
        assert(f.c.target[5]==.167&&f.c.command[5]==0&&strstr(f.c.message,"measured hold verified"));
        cleanup(&f);
    }
    for (unsigned variant=0;variant<3;++variant) {
        setup(&f,variant==2?.1:10,.167); injection=variant==0?NONSTATIONARY:variant==1?VELOCITY:BUSY;
        for (unsigned i=0;i<60;++i) tick(&f);
        assert(f.c.op==OP_MOVE&&!f.c.arrival_hold&&stops==0&&power==0);
        if (variant==2) assert(fabs(f.c.command[5]-f.c.target[5])>.0005); /* Not final, despite being inside tolerance. */
        cleanup(&f);
    }
    setup(&f,100,.01); injection=PRETARGET_VELOCITY;
    for (unsigned i=0;i<19;++i) tick(&f);
    assert(f.c.op==OP_MOVE&&!f.c.arrival_hold&&stops==0); /* Recent but pre-target velocity is not arrival evidence. */
    cleanup(&f);
    for (unsigned variant=0;variant<5;++variant) {
        setup(&f,10,.167); injection=variant==0?STALE:variant==1?FAULT:variant==2?HOLD_STALE:variant==3?HOLD_UNSTABLE:HOLD_FROZEN_TIMES;
        bool saw_hold=false;
        for (unsigned i=0;i<160&&f.c.op!=OP_IDLE;++i) { tick(&f); if (f.c.arrival_hold) saw_hold=true; }
        assert(f.c.op==OP_IDLE&&!strcmp(f.c.phase,"stopped")&&stops==1&&power==0);
        assert((variant>=2)==saw_hold);
        if (variant==3) assert(strstr(f.c.message,"within two seconds"));
        cleanup(&f);
    }
    setup(&f,100,2); injection=BUSY; double start=f.now;
    for (unsigned i=0;i<40&&f.c.op!=OP_IDLE;++i) tick(&f);
    assert(f.c.op==OP_IDLE&&f.c.obstruction_joint==6&&stops==1&&f.now-start>=.5&&f.now-start<.7);
    cleanup(&f);
    for (unsigned variant=0;variant<4;++variant) {
        reach_hold(&f); struct can_device *d=&f.c.bus.devices[0];
        if (variant==0) controller_stop(&f.c,"Arrival cancellation fixture.");
        if (variant==1) ++d->generation;
        if (variant==2) f.c.heartbeat=f.now-1;
        if (variant==3) { d->feedback.arm[0].control=2; d->feedback.arm[0].teach=1; d->feedback.arm[0].arm_status=11; }
        f.now+=.025; controller_tick(&f.c,f.now);
        assert(f.c.op==OP_IDLE&&!f.c.arrival_hold);
        uint64_t tx=d->tx;
        for (unsigned i=0;i<20;++i) { f.now+=.025; controller_tick(&f.c,f.now); }
        assert(f.c.op==OP_IDLE&&!f.c.arrival_hold&&d->tx==tx);
        cleanup(&f);
    }
    reach_hold(&f);
    /* Bring wall time up to the synthetic fixture time before calling the real
       API (which deliberately reads its own clock and guards sample freshness). */
    double wait=f.now-piper_now()+.01; if (wait>0) usleep((useconds_t)ceil(wait*1000000));
    struct can_device *d=&f.c.bus.devices[0];
    cJSON *hold=cJSON_CreateObject();
    cJSON_AddStringToObject(hold,"device_id",d->id); cJSON_AddNumberToObject(hold,"generation",d->generation);
    cJSON_AddStringToObject(hold,"owner","arrival-tolerance-regression"); cJSON_AddStringToObject(hold,"action","hold");
    cJSON_AddNumberToObject(hold,"request_id",f.c.request_id);
    int status; cJSON *reply=controller_action(&f.c,"/api/command",hold,&status);
    assert(status==202&&f.c.op==OP_MOVE&&!f.c.arrival_hold&&stops==0&&f.c.target[5]==0);
    cJSON_Delete(reply); cJSON_Delete(hold); cleanup(&f);
    puts("Arrival tolerance passed: latched MCU busy completes only after final fresh stationary/velocity checks and a verified measured hold; moving/nonfinal/stale/fault/unstable cases reject and 500ms obstruction remains active.");
}

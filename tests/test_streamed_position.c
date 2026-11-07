#define _GNU_SOURCE
#include "controller.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

/* Exercise admission and the complete wire transaction, not just the encoder.
   No USB device, hardware socket or wall clock is used by this fixture. */
static double clock_now=100;
static unsigned pose_packets,stop_packets,seed_packets;
static uint8_t last_mode[8];
static bool fail_pose,moving,stale;

double __wrap_piper_now(void) { return clock_now; }
void __wrap_can_bus_receive(struct can_bus *bus,double now)
{
    struct can_device *d=&bus->devices[0];
    if (stale) return;
    struct piper_sample *s=&d->feedback.arm[0];
    d->last_rx=now;s->status_seen=true;s->status_at=now;
    if (moving) d->stationary_since=0;
    for (unsigned i=0;i<6;++i) {
        s->joint_seen[i]=true;s->joint_at[i]=now;
        d->feedback.motor[i].seen=d->feedback.motor[i].high_seen=d->feedback.motor[i].enabled=true;
        d->feedback.motor[i].at=d->feedback.motor[i].high_at=now;
    }
}
bool __wrap_can_bus_send(struct can_bus *bus,struct can_device *d,unsigned generation,const struct can_frame *frames,size_t count)
{
    assert(bus->simulated&&generation==d->generation);
    if (count==1) { assert(frames[0].can_id==0x150&&frames[0].data[0]==1);++stop_packets;return true; }
    if(count==3) {
        struct can_frame expected[3];assert(piper_targets(d->feedback.arm[0].deg,expected));
        for(unsigned i=0;i<3;++i) assert(frames[i].can_id==expected[i].can_id&&!memcmp(frames[i].data,expected[i].data,8));
        ++seed_packets;d->tx+=3;return true;
    }
    assert(count==4&&frames[0].can_id==0x151&&frames[0].len==8);
    assert(frames[0].data[0]==1&&frames[0].data[1]==1);
    for (unsigned i=4;i<8;++i) assert(frames[0].data[i]==0);
    if (fail_pose) { strcpy(d->problem,"fixture refused complete position transaction");return false; }
    struct piper_feedback decoded={0};
    for (unsigned i=1;i<4;++i) {
        assert(frames[i].can_id==0x154+i&&frames[i].len==8);
        assert(piper_decode(&decoded,frames[i].can_id+0x150,frames[i].data,8,clock_now));
    }
    memcpy(bus->sim_target,decoded.arm[0].deg,sizeof bus->sim_target);
    memcpy(last_mode,frames[0].data,sizeof last_mode);
    d->tx+=4;++pose_packets;return true;
}
static cJSON *request(struct controller *c,const char *action,bool trajectory,const char *transport)
{
    struct can_device *d=&c->bus.devices[0];cJSON *body=cJSON_CreateObject();
    cJSON_AddStringToObject(body,"device_id",d->id);cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddStringToObject(body,"owner","streamed-position-fixture");cJSON_AddStringToObject(body,"action",action);
    cJSON_AddNumberToObject(body,"request_id",c->request_id);cJSON_AddNumberToObject(body,"speed_percent",90);
    cJSON_AddBoolToObject(body,"standalone",true);cJSON_AddBoolToObject(body,"clear",true);
    if (trajectory) cJSON_AddBoolToObject(body,"trajectory",true);
    if (transport) cJSON_AddStringToObject(body,"trajectory_transport",transport);
    if (!strcmp(action,"pose")) {
        double target[6]={0,0,0,0,0,1};cJSON_AddItemToObject(body,"degrees",cJSON_CreateDoubleArray(target,6));
    }
    return body;
}
static void action(struct controller *c,cJSON *body,int expected)
{
    int status;cJSON *response=controller_action(c,"/api/command",body,&status);
    if (status!=expected) { char *text=cJSON_PrintUnformatted(response);fprintf(stderr,"Expected%d, got%d: %s\n",expected,status,text);free(text); }
    assert(status==expected);cJSON_Delete(response);cJSON_Delete(body);
}
static void fixture(struct controller *c,char directory[96])
{
    strcpy(directory,"/tmp/piper-streamed-position-XXXXXX");assert(mkdtemp(directory));clock_now+=10;
    assert(controller_init(c,true,directory,""));pose_packets=stop_packets=seed_packets=0;fail_pose=moving=stale=false;
    memset(last_mode,0,sizeof last_mode);c->bus.sim_enabled=63;
    struct can_device *d=&c->bus.devices[0];d->stationary_since=clock_now-1;
    d->feedback.arm[0].control=d->feedback.arm[0].motion_mode=1;
    strcpy(d->feedback.hardware,"H-V1.2-1");strcpy(d->feedback.version,"S-V1.9-0");
    memset(d->feedback.arm[0].deg,0,sizeof d->feedback.arm[0].deg);
    for (unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=-150,.max=150,.speed=3,.at=clock_now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=clock_now};
    }
    __wrap_can_bus_receive(&c->bus,clock_now);
}
static void cleanup(struct controller *c,const char *directory)
{
    assert(c->op==OP_IDLE);controller_close(c);char path[128];snprintf(path,sizeof path,"%s/control-state.json",directory);
    assert(!unlink(path));assert(!rmdir(directory));
}
static void tick(struct controller *c)
{
    clock_now+=.025;c->heartbeat=c->follow_updated=clock_now;controller_tick(c,clock_now);
}
static void end(struct controller *c)
{
    action(c,request(c,"follow-end",false,NULL),200);assert(c->op==OP_IDLE);
}
int main(void)
{
    struct can_frame mode=piper_streamed_position_mode(37);
    assert(mode.can_id==0x151&&mode.len==8);
    const uint8_t expected[8]={1,1,37,0xad,0,0,0,0};assert(!memcmp(mode.data,expected,sizeof expected));
    struct piper_feedback firmware={0};strcpy(firmware.hardware,"H-V1.2-1");strcpy(firmware.version,"S-V1.9-0");
    assert(piper_streamed_position_supported(&firmware));
    strcpy(firmware.version,"S-V1.9-1");assert(piper_streamed_position_supported(&firmware));
    const char *versions[]={"S-V1.8-5","S-V1.9-2","S-V2.0-0","unknown"};
    for (unsigned i=0;i<sizeof versions/sizeof *versions;++i) { strcpy(firmware.version,versions[i]);assert(!piper_streamed_position_supported(&firmware)); }
    strcpy(firmware.version,"S-V1.9-0");strcpy(firmware.hardware,"H-V2.0-0");assert(!piper_streamed_position_supported(&firmware));

    struct controller c;char directory[96];
    for (unsigned invalid=0;invalid<6;++invalid) {
        fixture(&c,directory);cJSON *body;
        if (invalid==0) body=request(&c,"follow-start",false,"streamed-position");
        else if (invalid==1) body=request(&c,"pose",false,"streamed-position");
        else if (invalid==2) body=request(&c,"follow-start",true,"unknown-mode");
        else if (invalid==3) { body=request(&c,"follow-start",true,NULL);cJSON_AddNumberToObject(body,"trajectory_transport",1); }
        else if (invalid==4) { body=request(&c,"follow-start",true,"streamed-position");strcpy(c.bus.devices[0].feedback.version,"S-V1.9-2"); }
        else { body=request(&c,"follow-start",true,"streamed-position");strcpy(c.bus.devices[0].feedback.hardware,"H-V1.2-0"); }
        action(&c,body,invalid<4?400:409);
        assert(c.op==OP_IDLE&&pose_packets==0&&stop_packets==0);cleanup(&c,directory);
    }

    /* Default and explicitly selected legacy transport retain the exact old wire
       tuple; neither ordinary following nor ordinary pose can inherit JS. */
    for (unsigned legacy=0;legacy<3;++legacy) {
        fixture(&c,directory);action(&c,request(&c,"follow-start",legacy!=2,legacy==1?"position-velocity":NULL),202);
        tick(&c);assert(pose_packets==1&&last_mode[2]==90&&last_mode[3]==0);end(&c);cleanup(&c,directory);
    }

    fixture(&c,directory);action(&c,request(&c,"follow-start",true,"streamed-position"),202);
    assert(c.trajectory_follow&&pose_packets==0);tick(&c);assert(pose_packets==1&&last_mode[2]==90&&last_mode[3]==0xad);
    cJSON *state=controller_state(&c,"fixture-public-state");
    assert(cJSON_IsTrue(cJSON_GetObjectItem(state,"streamed_position_transport_supported")));
    cJSON *motion=cJSON_GetObjectItem(state,"motion");
    assert(!strcmp(cJSON_GetObjectItem(motion,"trajectory_transport")->valuestring,"streamed-position"));
    assert(cJSON_IsFalse(cJSON_GetObjectItem(motion,"streamed_position_hold")));cJSON_Delete(state);
    unsigned before=pose_packets;tick(&c);assert(pose_packets==before); /* The unchanged complete tuple is still suppressed. */
    /* Test only: alter transport without touching the cached target, speed or
       generation. Production changes this flag only through new admission. */
    c.streamed_position_mode=false;
    tick(&c);assert(pose_packets==before+1&&last_mode[3]==0&&c.pose_tx_mode==0);
    c.streamed_position_mode=true;
    tick(&c);assert(pose_packets==before+2&&last_mode[3]==0xad&&c.pose_tx_mode==0xad);
    c.target[5]=1;
    for (unsigned n=0;n<10;++n) {
        tick(&c);assert(c.op==OP_MOVE&&c.trajectory_follow&&last_mode[3]==0xad);
        for (unsigned i=0;i<6;++i) assert(fabs(c.command[i]-c.bus.sim_target[i])<1e-9);
        memcpy(c.bus.devices[0].feedback.arm[0].deg,c.command,sizeof c.command);
    }
    moving=true;end(&c);assert(!stop_packets&&last_mode[3]==0xad);
    state=controller_state(&c,"fixture-public-state");motion=cJSON_GetObjectItem(state,"motion");
    assert(cJSON_IsTrue(cJSON_GetObjectItem(motion,"streamed_position_hold")));cJSON_Delete(state);
    before=pose_packets;for (unsigned n=0;n<10;++n) tick(&c);assert(pose_packets==before); /* Never silently replan a moving hold as PV. */
    action(&c,request(&c,"pose",false,NULL),409);assert(pose_packets==before); /* Ordinary mode change waits for measured stillness. */
    moving=false;c.bus.devices[0].stationary_since=clock_now-1;
    action(&c,request(&c,"pose",false,NULL),202);tick(&c);
    assert(seed_packets==1&&last_mode[3]==0xad); /* Frozen hold is seeded before changing the existing mode. */
    tick(&c);assert(last_mode[3]==0); /* Explicit new stationary action then restores the default mode. */
    controller_stop(&c,"Fixture operator stop after checking the explicit ordinary mode transition.");
    cleanup(&c,directory);

    for (unsigned reason=0;reason<3;++reason) {
        fixture(&c,directory);action(&c,request(&c,"follow-start",true,"streamed-position"),202);tick(&c);
        moving=true;c.bus.devices[0].feedback.arm[0].deg[5]=.02;
        if (reason==0) { clock_now=c.follow_updated+.251;c.heartbeat=clock_now;controller_tick(&c,clock_now); }
        else if (reason==1) { clock_now+=.053;c.follow_updated=c.heartbeat=clock_now;controller_tick(&c,clock_now); }
        else end(&c);
        assert(c.op==OP_IDLE&&!c.trajectory_follow&&stop_packets==0&&last_mode[3]==0xad);
        assert(c.bus.sim_target[5]==c.bus.devices[0].feedback.arm[0].deg[5]);
        before=pose_packets;for (unsigned n=0;n<10;++n) tick(&c);assert(pose_packets==before);cleanup(&c,directory);
    }

    /* Opting into experimental streamed transport must not weaken independent hard faults,
       tracking, obstruction or stale-feedback guards, or failed-hold handling. */
    for (unsigned fault=0;fault<5;++fault) {
        fixture(&c,directory);action(&c,request(&c,"follow-start",true,"streamed-position"),202);tick(&c);c.target[5]=10;
        if (fault==0) c.bus.devices[0].feedback.arm[0].error=1;
        if (fault==1) c.command[5]=3.1;
        if (fault==2) {
            c.command[5]=1.8;c.trajectory_velocity[5]=15;c.trajectory_previous_dt=.025;
            c.obstruction[5]=(struct obstruction_watch){.progress_position=0,.progress_at=clock_now-.6,.direction=1};
        }
        if (fault==3) { stale=true;clock_now+=.36; }
        if (fault==4) { fail_pose=true;c.bus.devices[0].feedback.arm[0].deg[5]=.02; }
        if (fault==4) action(&c,request(&c,"follow-end",false,NULL),409);
        else tick(&c);
        assert(c.op==OP_IDLE&&!c.trajectory_follow&&stop_packets==1);cleanup(&c,directory);
    }
    puts("Streamed position passed: explicit firmware-gated admission, exact JS/0xAD wire tuple, unchanged six-angle packets, legacy isolation, moving holds/no idle mode flap and unchanged stop guards.");
}

#define _GNU_SOURCE
#include "controller.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static void setup(struct controller *c,char *directory)
{
    assert(mkdtemp(directory)); assert(controller_init(c,true,directory,""));
    struct can_device *d=&c->bus.devices[0];
    memset(c->bus.sim_target,0,sizeof c->bus.sim_target);
    memset(d->feedback.arm[0].deg,0,sizeof d->feedback.arm[0].deg);
    c->bus.sim_enabled=63;
    double now=piper_now(); controller_tick(c,now);
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.9-0");
    for (unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=-180,.max=180,.speed=.3,.at=now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=now};
    }
    d->feedback.arm[0].control=2; d->feedback.arm[0].teach=2;
    d->stationary_since=now-1;
}
static void cleanup(struct controller *c,const char *directory)
{
    controller_close(c); char path[256]; snprintf(path,sizeof path,"%s/control-state.json",directory);
    assert(!unlink(path)); assert(!rmdir(directory));
}
static cJSON *request(struct controller *c,const char *action)
{
    struct can_device *d=&c->bus.devices[0]; cJSON *j=cJSON_CreateObject();
    cJSON_AddStringToObject(j,"action",action); cJSON_AddStringToObject(j,"device_id",d->id);
    cJSON_AddNumberToObject(j,"generation",d->generation); cJSON_AddStringToObject(j,"owner","teaching-regression-client");
    cJSON_AddBoolToObject(j,"standalone",true); cJSON_AddBoolToObject(j,"clear",true);
    cJSON_AddBoolToObject(j,"button_off",true); cJSON_AddBoolToObject(j,"zero_pose_confirmed",true); cJSON_AddBoolToObject(j,"supported",true);
    return j;
}
static void action(struct controller *c,cJSON *body,int expected)
{
    int status; cJSON *result=controller_action(c,"/api/command",body,&status);
    if(status!=expected) { char *s=cJSON_PrintUnformatted(result); fprintf(stderr,"expected %d got %d: %s\n",expected,status,s); free(s); }
    assert(status==expected); cJSON_Delete(body); cJSON_Delete(result);
}
static cJSON *snapshot(struct controller *c)
{
    cJSON *state=controller_state(c,"test"),*device=cJSON_GetArrayItem(cJSON_GetObjectItem(state,"devices"),0);
    cJSON *copy=cJSON_Duplicate(device,true); cJSON_Delete(state); return copy;
}
static bool can_return(struct controller *c)
{
    cJSON *device=snapshot(c);
    bool result=cJSON_IsTrue(cJSON_GetObjectItem(cJSON_GetObjectItem(device,"teaching"),"can_return"));
    cJSON_Delete(device); return result;
}
static void check_phase(struct controller *c,const char *phase,bool active)
{
    cJSON *device=snapshot(c),*teaching=cJSON_GetObjectItem(device,"teaching");
    assert(!strcmp(cJSON_GetObjectItem(teaching,"phase")->valuestring,phase));
    assert(cJSON_IsTrue(cJSON_GetObjectItem(teaching,"active"))==active);
    cJSON_Delete(device);
}
static void enter_own_move(struct controller *c)
{
    struct can_device *d=&c->bus.devices[0]; d->feedback.arm[0].control=1; d->feedback.arm[0].teach=0;
    cJSON *j=request(c,"joint"); cJSON_AddNumberToObject(j,"joint",6); cJSON_AddNumberToObject(j,"degrees",25);
    action(c,j,202); assert(c->op==OP_MOVE);
}
int main(void)
{
    struct controller c; char first[]="/tmp/piper-teaching-state-XXXXXX"; setup(&c,first);
    struct can_device *d=&c.bus.devices[0];
    assert(can_return(&c)); check_phase(&c,"ended",false);
    for (unsigned state=1;state<=7;++state) {
        d->feedback.arm[0].teach=(uint8_t)state;
        d->feedback.arm[0].arm_status=state==1?11:state==4?13:state==3||state==5||state==7?12:0;
        check_phase(&c,state==1?"hand-guiding":state==4?"playback-paused":state==2||state==6?"ended":"playback",state!=2&&state!=6);
        if(state!=2&&state!=6) {
            assert(!can_return(&c));
            const char *why=controller_blocker(&c,d,piper_now()); assert(why&&strstr(why,"teaching is active"));
        }
    }
    d->feedback.arm[0].teach=2; d->feedback.arm[0].arm_status=0;
    uint64_t tx=d->tx;
    const char *flags[]={"button_off","zero_pose_confirmed","supported","clear"};
    for(unsigned i=0;i<4;++i) { cJSON *j=request(&c,"teach-return"); cJSON_DeleteItemFromObjectCaseSensitive(j,flags[i]); action(&c,j,400); }
    assert(d->tx==tx);
    d->feedback.arm[0].deg[5]=1.001; assert(!can_return(&c)); action(&c,request(&c,"teach-return"),409); d->feedback.arm[0].deg[5]=0;
    d->feedback.limit[0].min=.2; assert(!can_return(&c)); action(&c,request(&c,"teach-return"),409); d->feedback.limit[0].min=-180;
    d->stationary_since=piper_now(); assert(!can_return(&c)); action(&c,request(&c,"teach-return"),409); d->stationary_since=piper_now()-1;
    d->feedback.motor[2].fault=1; assert(!can_return(&c)); action(&c,request(&c,"teach-return"),409); d->feedback.motor[2].fault=0;
    d->feedback.arm[0].error=1; assert(!can_return(&c)); action(&c,request(&c,"teach-return"),409); d->feedback.arm[0].error=0;
    cJSON *old=request(&c,"teach-return"); cJSON_SetNumberValue(cJSON_GetObjectItem(old,"generation"),d->generation+1); action(&c,old,409);
    assert(d->tx==tx);
    /* Stale status cannot acknowledge either stage. Exactly two mode packets
       are sent: no position, reset, teaching-start or motor power writes. */
    action(&c,request(&c,"teach-return"),202); assert(c.op==OP_TEACH_RETURN&&c.teach_return_stage==1&&d->tx==tx+1&&d->feedback.arm[0].control==0);
    double sent=c.sent_at; controller_tick(&c,sent); assert(c.teach_return_stage==1&&d->tx==tx+1);
    usleep(2000); controller_tick(&c,piper_now()); assert(c.teach_return_stage==2&&d->tx==tx+2&&d->feedback.arm[0].control==1);
    sent=c.sent_at; controller_tick(&c,sent); assert(c.op==OP_TEACH_RETURN&&d->tx==tx+2);
    usleep(2000); controller_tick(&c,piper_now()); assert(c.op==OP_IDLE&&d->tx==tx+2&&c.bus.sim_enabled==63);
    for(unsigned i=0;i<6;++i) assert(c.bus.sim_target[i]==0&&d->feedback.arm[0].deg[i]==0);
    action(&c,request(&c,"teach-return"),200); assert(d->tx==tx+2); // Already CAN: no replay or repeated writes.
    struct can_frame invalid={.can_id=0x151,.len=8,.data={2,1,1}};
    assert(!can_bus_send(&c.bus,d,d->generation,&invalid,1));
    invalid.data[0]=0; invalid.data[2]=2; assert(!can_bus_send(&c.bus,d,d->generation,&invalid,1));
    invalid.data[2]=1; invalid.data[3]=1; assert(!can_bus_send(&c.bus,d,d->generation,&invalid,1));
    cleanup(&c,first);

    char takeover[]="/tmp/piper-teaching-takeover-XXXXXX"; setup(&c,takeover); d=&c.bus.devices[0]; enter_own_move(&c);
    unsigned prior_request=c.request_id; tx=d->tx;
    d->feedback.arm[0].control=2; d->feedback.arm[0].teach=1; d->feedback.arm[0].arm_status=11;
    c.heartbeat=piper_now()-1; // Physical teaching takes priority over our expired lease.
    cJSON *heartbeat=request(&c,""); cJSON_AddNumberToObject(heartbeat,"request_id",prior_request);
    int status; cJSON *result=controller_action(&c,"/api/heartbeat",heartbeat,&status); cJSON_Delete(result); cJSON_Delete(heartbeat);
    assert(status==409&&c.op==OP_IDLE&&!c.following&&!c.follow_committed&&!*c.owner&&d->tx==tx);
    cJSON *device=snapshot(&c); assert(!cJSON_IsTrue(cJSON_GetObjectItem(device,"recovery_required"))); cJSON_Delete(device);
    controller_tick(&c,piper_now()); assert(d->tx==tx&&c.bus.sim_enabled==63);
    d->feedback.arm[0].teach=2; d->feedback.arm[0].arm_status=0;
    action(&c,request(&c,"follow-update"),409); assert(d->tx==tx); // Old target never resumes.
    cleanup(&c,takeover);

    for(unsigned fault=0;fault<4;++fault) {
        char failed[]="/tmp/piper-teaching-fault-XXXXXX"; setup(&c,failed); d=&c.bus.devices[0]; enter_own_move(&c); tx=d->tx;
        d->feedback.arm[0].control=2; d->feedback.arm[0].teach=1; d->feedback.arm[0].arm_status=11;
        if(fault==0) d->feedback.motor[0].fault=1;
        if(fault==1) { d->simulated=false; d->fd=-1; } // Bus remains simulated; no host transport is opened.
        if(fault==2) { d->feedback.foreign_seen=true; d->feedback.foreign_at=piper_now(); }
        if(fault==3) { d->feedback.arm[1].status_seen=true; d->feedback.arm[1].status_at=piper_now(); }
        c.heartbeat=piper_now()-1;
        heartbeat=request(&c,""); cJSON_AddNumberToObject(heartbeat,"request_id",c.request_id);
        result=controller_action(&c,"/api/heartbeat",heartbeat,&status); cJSON_Delete(result); cJSON_Delete(heartbeat);
        assert(status==409&&c.op==OP_IDLE&&d->tx==tx+1);
        device=snapshot(&c); assert(cJSON_IsTrue(cJSON_GetObjectItem(device,"recovery_required"))); cJSON_Delete(device);
        tx=d->tx; controller_tick(&c,piper_now()); assert(d->tx==tx); // Existing fault cannot be cleared by entering teaching.
        cleanup(&c,failed);
    }

    for(unsigned failure=0;failure<6;++failure) {
        char abandoned[]="/tmp/piper-teaching-abort-XXXXXX"; setup(&c,abandoned); d=&c.bus.devices[0];
        action(&c,request(&c,"teach-return"),202); tx=d->tx;
        if(failure==0) { d->feedback.arm[0].control=2; d->feedback.arm[0].teach=1; }
        if(failure==1) { d->feedback.arm[0].control=2; d->feedback.arm[0].deg[5]=.6; }
        if(failure==2) { c.heartbeat=piper_now()-1; }
        if(failure==3) { c.sent_at=piper_now()-2; }
        if(failure==4) { ++d->generation; }
        if(failure==5) { d->feedback.arm[0].control=2; d->feedback.arm[0].teach=3; d->feedback.arm[0].arm_status=12; }
        controller_tick(&c,piper_now()); assert(c.op==OP_IDLE&&d->tx==tx&&c.bus.sim_enabled==63);
        cleanup(&c,abandoned);
    }
    char unexpected[]="/tmp/piper-teaching-unexpected-XXXXXX"; setup(&c,unexpected); d=&c.bus.devices[0];
    action(&c,request(&c,"teach-return"),202); tx=d->tx;
    d->feedback.arm[0].deg[5]=c.bus.sim_target[5]=.6;
    controller_tick(&c,piper_now()); assert(c.op==OP_IDLE&&d->tx==tx+1&&d->feedback.arm[0].arm_status==1&&c.bus.sim_enabled==63);
    device=snapshot(&c); assert(cJSON_IsTrue(cJSON_GetObjectItem(device,"recovery_required"))); cJSON_Delete(device);
    cleanup(&c,unexpected);
    for(unsigned noise=0;noise<2;++noise) {
        char settling[]="/tmp/piper-teaching-settling-XXXXXX"; setup(&c,settling); d=&c.bus.devices[0];
        action(&c,request(&c,"teach-return"),202); tx=d->tx;
        d->feedback.arm[0].deg[5]=c.bus.sim_target[5]=noise?.2:.06;
        d->stationary_since=piper_now(); // Model a refreshed anchor after small supported settling.
        usleep(2000); controller_tick(&c,piper_now());
        assert(c.op==OP_TEACH_RETURN&&c.teach_return_stage==2&&d->tx==tx+1);
        assert(d->feedback.arm[0].arm_status==0&&c.bus.sim_enabled==63);
        // Both values can reset the normal stationary anchor (>0.03 degrees),
        // but remain inside the supported settling allowance of 0.5 degrees.
        assert(piper_now()-d->stationary_since<.4);
        usleep(2000); controller_tick(&c,piper_now());
        assert(c.op==OP_IDLE&&!strcmp(c.phase,"complete")&&d->tx==tx+1);
        device=snapshot(&c); assert(!cJSON_IsTrue(cJSON_GetObjectItem(device,"recovery_required"))); cJSON_Delete(device);
        cleanup(&c,settling);
    }
    char closing[]="/tmp/piper-teaching-close-XXXXXX"; setup(&c,closing); d=&c.bus.devices[0];
    action(&c,request(&c,"teach-return"),202); tx=d->tx; cleanup(&c,closing); assert(d->tx==tx);
    puts("Teaching: feedback phases, benign takeover without STOP, zero-only standby/CAN fresh acknowledgements, rejected stale targets, bounded aborts and protective unexpected-motion STOP passed.");
}

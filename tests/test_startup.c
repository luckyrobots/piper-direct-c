#define _GNU_SOURCE
#include "controller.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static bool slow_first_enable=true;

static void tick(struct controller *c)
{
    usleep(10000);
    c->heartbeat=piper_now(); controller_tick(c,piper_now());
}
static void settle(struct controller *c)
{
    for (unsigned i=0;i<50;++i) tick(c);
}
static void action(struct controller *c,const char *name,int expected)
{
    struct can_device *d=&c->bus.devices[0];
    cJSON *j=cJSON_CreateObject();
    cJSON_AddStringToObject(j,"device_id",d->id); cJSON_AddNumberToObject(j,"generation",d->generation);
    cJSON_AddStringToObject(j,"owner","startup-regression-client"); cJSON_AddStringToObject(j,"action",name);
    cJSON_AddBoolToObject(j,"standalone",true); cJSON_AddBoolToObject(j,"supported",true); cJSON_AddBoolToObject(j,"clear",true);
    int status; cJSON *r=controller_action(c,"/api/command",j,&status);
    if (status!=expected) { char *s=cJSON_Print(r); fprintf(stderr,"%s: %s\n",name,s); free(s); }
    assert(status==expected); cJSON_Delete(r); cJSON_Delete(j);
    if (expected==202) {
        unsigned ticks=0;
        while (c->op!=OP_IDLE&&ticks++<1000) {
            if (!strcmp(name,"reset")) d->feedback.arm[0].arm_status=ticks<20?2:0;
            uint64_t previous_tx=d->tx;
            tick(c);
            if (!strcmp(name,"enable")) {
                assert(d->tx-previous_tx<=1); // Same-ID power commands are paced, never burst.
                if (c->enable_count<3) c->bus.sim_enabled&=~1u; // First driver misses two wake-up requests.
                if (slow_first_enable&&piper_now()-c->started<3.2) c->bus.sim_enabled&=~1u;
            }
            if (!strcmp(name,"reset")&&ticks<20) assert(c->op==OP_RESET);
        }
        assert(c->op==OP_IDLE); assert(!strcmp(c->phase,"complete"));
        if (!strcmp(name,"enable")) slow_first_enable=false;
    }
}
static bool recovery(struct controller *c)
{
    cJSON *s=controller_state(c,"test"),*d=cJSON_GetArrayItem(cJSON_GetObjectItem(s,"devices"),0);
    bool result=cJSON_IsTrue(cJSON_GetObjectItem(d,"recovery_required")); cJSON_Delete(s); return result;
}
int main(void)
{
    char directory[]="/tmp/piper-startup-XXXXXX"; assert(mkdtemp(directory));
    struct controller c; assert(controller_init(&c,true,directory,""));
    struct can_device *d=&c.bus.devices[0]; double now=piper_now();
    const double rest[6]={-9.805,-1.983,-.463,-3.049,31.002,78.301};
    memcpy(d->feedback.arm[0].deg,rest,sizeof rest); memcpy(c.bus.sim_target,rest,sizeof rest);
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.8-2");
    const double limits[6][2]={{-150,150},{0,180},{-170,0},{-100,100},{-70,70},{-180,180}};
    for (unsigned i=0;i<6;++i) d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=limits[i][0],.max=limits[i][1],.speed=.3,.at=now};
    for (unsigned i=0;i<6;++i) d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=now};
    settle(&c);
    assert(!controller_blocker(&c,d,piper_now()));
    uint64_t tx=d->tx;
    d->feedback.arm[0].motion_status=1; // Old target not reached is not current movement.
    action(&c,"enable",202);
    assert(d->tx>tx+6); assert(c.bus.sim_enabled==63);
    assert(d->feedback.arm[0].control==0); // No mode/pose command was sent.
    for (unsigned i=0;i<6;++i) assert(fabs(d->feedback.arm[0].deg[i]-rest[i])<.001);
    action(&c,"align",202);
    assert(fabs(d->feedback.arm[0].deg[1]-.5)<.001);
    controller_stop(&c,"Interrupted client fixture.");
    action(&c,"verify-current",409); // An actual stop/fault cannot be acknowledged away.
    d->feedback.arm[0].arm_status=0; settle(&c);
    d->feedback.motor[0].fault=1; action(&c,"verify-current",409); d->feedback.motor[0].fault=0;
    d->feedback.motor[0].enabled=false; action(&c,"verify-current",409); d->feedback.motor[0].enabled=true;
    tx=d->tx; action(&c,"verify-current",200);
    assert(!recovery(&c) && d->tx==tx && c.bus.sim_enabled==63);
    // Real transport feedback must agree with independently reported motor
    // encoder positions before an old software stop can be acknowledged.
    bool was_simulated=d->simulated; int old_fd=d->fd;
    d->simulated=false; d->fd=9;
    for (unsigned i=0;i<6;++i) {
        d->feedback.motor[i].high_seen=true; d->feedback.motor[i].high_at=piper_now();
        d->feedback.motor[i].position_raw=(int32_t)lrint(d->feedback.arm[0].deg[i]*1000*3.141592653589793/180);
    }
    action(&c,"verify-current",200);
    d->feedback.motor[4].position_raw+=1000; action(&c,"verify-current",409);
    d->feedback.motor[4].position_raw-=1000;
    d->feedback.motor[4].high_at=piper_now()-1; action(&c,"verify-current",409);
    assert(d->tx==tx); d->simulated=was_simulated; d->fd=old_fd;
    for (unsigned i=0;i<6;++i) if (i!=1) assert(fabs(d->feedback.arm[0].deg[i]-rest[i])<.001);
    assert(d->feedback.arm[0].control==1);
    action(&c,"disable",202); assert(!recovery(&c));
    action(&c,"enable",202); action(&c,"disable",202); assert(!recovery(&c));
    // Disabling after a real stop cannot erase the earlier recovery record.
    controller_stop(&c,"Operator stop."); action(&c,"disable",202); assert(recovery(&c));
    tx=d->tx;
    action(&c,"verify-disabled",409); // A hardware stop still requires reset.
    d->feedback.arm[0].arm_status=0; // Model an externally completed reset.
    d->feedback.motor[0].fault=1; action(&c,"verify-disabled",409); d->feedback.motor[0].fault=0;
    d->feedback.motor[0].enabled=true; action(&c,"verify-disabled",409); d->feedback.motor[0].enabled=false;
    d->stationary_since=piper_now(); action(&c,"verify-disabled",409); settle(&c);
    cJSON *r=cJSON_GetObjectItem(cJSON_GetObjectItem(c.saved,"devices"),d->id);
    cJSON_ReplaceItemInObject(r,"reset_pending",cJSON_CreateBool(true));
    action(&c,"verify-disabled",409);
    cJSON_ReplaceItemInObject(r,"reset_pending",cJSON_CreateBool(false));
    action(&c,"verify-disabled",200); assert(!recovery(&c)); assert(d->tx==tx);
    d->feedback.arm[0].arm_status=5; d->feedback.arm[0].error=63;
    d->feedback.arm[0].status_at=piper_now();
    controller_stop(&c,"Joint-drive communication fixture.");
    cJSON *fault=cJSON_GetObjectItem(r,"last_fault");
    assert(cJSON_GetObjectItem(fault,"arm_status")->valueint==5);
    assert(cJSON_GetObjectItem(fault,"error")->valueint==63);
    // Expire the five-second information query while preserving the fault record.
    c.query=true; c.query_generation=d->generation; c.query_start=piper_now()-6;
    controller_tick(&c,piper_now());
    assert(!c.query && !strcmp(c.phase,"blocked"));
    assert(strstr(c.message,"Joint-drive communication fixture."));
    d->feedback.arm[0].arm_status=0; d->feedback.arm[0].error=0;
    d->feedback.arm[0].control=2; d->feedback.arm[0].teach=3;
    action(&c,"reset",409); // Active onboard playback cannot be reset by this path.
    d->feedback.arm[0].teach=6; // Documented terminated playback state.
    d->stationary_since=piper_now(); action(&c,"reset",409);
    settle(&c);
    tx=d->tx; action(&c,"reset",202); assert(!recovery(&c)); assert(d->tx==tx+1);
    assert(cJSON_GetObjectItem(cJSON_GetObjectItem(r,"last_fault"),"error")->valueint==63);
    // A larger correction is rejected, with no position frames sent.
    d->feedback.arm[0].deg[1]=c.bus.sim_target[1]=-10; settle(&c); action(&c,"enable",202);
    tx=d->tx; action(&c,"align",409); assert(d->tx==tx);
    action(&c,"disable",202);
    // Slow measured motion accumulates against a stationary anchor.
    now=piper_now(); double angles[6]; memcpy(angles,d->feedback.arm[0].deg,sizeof angles);
    d->stationary_since=now; memcpy(d->last_angles,angles,sizeof angles);
    for (unsigned sample=1;sample<=20;++sample) {
        double t=now+sample*.05; angles[0]+=.01;
        struct can_frame f[3],status_frame={.can_id=0x2a1,.len=8,.data={1,0,1,0,1}};
        assert(piper_targets(angles,f));
        for (unsigned i=0;i<3;++i) { f[i].can_id=0x2a5+i; can_bus_ingest(d,&f[i],false,t); }
        can_bus_ingest(d,&status_frame,false,t);
        if (sample>6) assert(t-d->stationary_since<.3);
    }
    controller_close(&c);
    char path[256]; snprintf(path,sizeof path,"%s/control-state.json",directory); assert(!unlink(path)); assert(!rmdir(directory));
    puts("Startup regression passed: measured resting pose, power-only enable, explicit bounded range entry, retained joints, normal disable/re-enable, verified recovery without CAN writes, pending-reset/fault/movement rejection.");
}

#define _GNU_SOURCE
#include "controller.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static cJSON *body(struct can_device *d)
{
    cJSON *j=cJSON_CreateObject();
    cJSON_AddStringToObject(j,"device_id",d->id); cJSON_AddNumberToObject(j,"generation",d->generation);
    cJSON_AddStringToObject(j,"owner","native-fixture-client"); cJSON_AddStringToObject(j,"action","joint");
    cJSON_AddNumberToObject(j,"joint",1); cJSON_AddNumberToObject(j,"degrees",1);
    cJSON_AddBoolToObject(j,"standalone",true); cJSON_AddBoolToObject(j,"clear",true);
    return j;
}
static void reject(struct controller *c,struct can_device *d,cJSON *j)
{
    uint64_t tx=d->tx; int status=0; cJSON *r=controller_action(c,"/api/command",j,&status);
    if (status<400) { char *s=cJSON_Print(r); fprintf(stderr,"Unexpected acceptance: %s\n",s); free(s); }
    assert(status>=400); assert(d->tx==tx); assert(c->op==OP_IDLE); cJSON_Delete(r);
}
int main(void)
{
    char directory[]="/tmp/piper-control-unit-XXXXXX"; assert(mkdtemp(directory));
    struct controller c; assert(controller_init(&c,true,directory,""));
    double now=piper_now(); controller_tick(&c,now);
    struct can_device *d=&c.bus.devices[0]; cJSON *j=body(d);
    assert(strstr(controller_blocker(&c,d,now),"has not been read"));
    reject(&c,d,j); /* No firmware/limits. */
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.8-2");
    for (unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=-180,.max=180,.speed=.3,.at=now};
        d->feedback.motor[i].enabled=true;
    }
    for (unsigned i=0;i<6;++i) d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=now};
    d->stationary_since=now-1;
    assert(!controller_blocker(&c,d,now));
    struct piper_feedback healthy=d->feedback;
    d->feedback.acceleration[5].seen=false; reject(&c,d,j); d->feedback=healthy;
    d->configuration_since=now-.1; d->feedback.acceleration[5].at=now-.2; reject(&c,d,j); d->feedback=healthy; d->configuration_since=0;
    d->feedback.acceleration[5].at=now+1; reject(&c,d,j); d->feedback=healthy;
    d->feedback.acceleration[5].max=0; reject(&c,d,j); d->feedback=healthy;
    d->feedback.acceleration[5].max=5.001; reject(&c,d,j); d->feedback=healthy;
    d->feedback.acceleration[5].max=NAN; reject(&c,d,j); d->feedback=healthy;
    d->feedback.arm[0].teach=6; d->feedback.arm[0].error=0x023f;
    cJSON *diag=controller_state(&c,"test"),*ds=cJSON_GetObjectItem(cJSON_GetArrayItem(cJSON_GetObjectItem(diag,"devices"),0),"status");
    assert(cJSON_GetObjectItem(ds,"teach_state")->valueint==6);
    assert(cJSON_GetObjectItem(ds,"joint_communication_fault_mask")->valueint==63);
    assert(cJSON_GetObjectItem(ds,"joint_angle_limit_fault_mask")->valueint==2);
    cJSON_Delete(diag); d->feedback=healthy;
    d->feedback.motor[1].enabled=false; d->feedback.limit[1].min=0; d->feedback.arm[0].deg[1]=-1.986;
    assert(!controller_blocker(&c,d,now)); reject(&c,d,j); d->feedback=healthy; /* Enable is separate from a valid motion target. */
    d->feedback.arm[0].joint_at[5]=now-1; reject(&c,d,j); d->feedback=healthy;
    d->feedback.motor[3].fault=1; reject(&c,d,j); d->feedback=healthy;
    d->feedback.motor[3].at=now-1; reject(&c,d,j); d->feedback=healthy;
    d->feedback.motor[3].enabled=false; reject(&c,d,j); d->feedback=healthy;
    d->feedback.foreign_seen=true; d->feedback.foreign_at=now; reject(&c,d,j); d->feedback=healthy;
    d->feedback.arm[1].status_seen=true; d->feedback.arm[1].status_at=now; reject(&c,d,j); d->feedback=healthy;
    d->feedback.arm[0].control=2; reject(&c,d,j); d->feedback=healthy;
    d->feedback.arm[0].motion_mode=4; reject(&c,d,j); d->feedback=healthy;
    d->configuration_since=now-.1; d->feedback.limit[0].at=now-.2; reject(&c,d,j); d->feedback=healthy; d->configuration_since=0;
    strcpy(d->feedback.version,"S-V9.0-0");
    assert(strstr(controller_blocker(&c,d,now),"not supported"));
    reject(&c,d,j); d->feedback=healthy;
    cJSON_SetNumberValue(cJSON_GetObjectItem(j,"degrees"),190); reject(&c,d,j);
    cJSON_SetNumberValue(cJSON_GetObjectItem(j,"degrees"),1);
    cJSON_SetNumberValue(cJSON_GetObjectItem(j,"generation"),d->generation+1); reject(&c,d,j);
    cJSON_SetNumberValue(cJSON_GetObjectItem(j,"generation"),d->generation);
    c.storage_failed=true; reject(&c,d,j); c.storage_failed=false;
    d->present=false; reject(&c,d,j); d->present=true;
    struct can_frame bad={.can_id=0x471,.len=8,.data={7,2}};
    uint64_t tx=d->tx; assert(!can_bus_send(&c.bus,d,d->generation,&bad,1)); assert(d->tx==tx);
    bad.can_id=0x123; assert(!can_bus_send(&c.bus,d,d->generation,&bad,1));
    bad=(struct can_frame){.can_id=0x472,.len=8,.data={1,3}};
    assert(!can_bus_send(&c.bus,d,d->generation,&bad,1));
    bad.data[1]=2; bad.data[2]=1; assert(!can_bus_send(&c.bus,d,d->generation,&bad,1));
    struct can_frame reset=piper_reset_frame(); assert(!can_bus_send(&c.bus,d,d->generation+1,&reset,1));
    struct can_frame local={.can_id=0x2a5,.len=8};
    can_bus_ingest(d,&local,true,now+1); assert(d->feedback.arm[0].joint_at[0]==healthy.arm[0].joint_at[0]);
    /* A persisted in-flight intent survives process shutdown and blocks a new action. */
    int status; cJSON *r=controller_action(&c,"/api/command",j,&status); assert(status==202); cJSON_Delete(r);
    c.op=OP_IDLE; controller_close(&c); assert(controller_init(&c,true,directory,""));
    cJSON *snapshot=controller_state(&c,"test-token");
    cJSON *first=cJSON_GetArrayItem(cJSON_GetObjectItem(snapshot,"devices"),0);
    assert(cJSON_IsTrue(cJSON_GetObjectItem(first,"recovery_required")));
    cJSON_Delete(snapshot); cJSON_Delete(j);
    /* Reconnect re-reads information without clearing a persisted stop or
       sending power/position commands. Simulation returns firmware immediately. */
    d=&c.bus.devices[0]; tx=d->tx; unsigned old_generation=d->generation;
    j=cJSON_CreateObject(); r=controller_action(&c,"/api/reconnect",j,&status);
    assert(status==200&&c.query&&c.op==OP_IDLE); cJSON_Delete(r); cJSON_Delete(j);
    assert(d->generation>old_generation&&d->tx==tx+2);
    assert(!strcmp(d->feedback.version,"S-V1.8-2")&&c.bus.sim_enabled==0);
    assert(d->feedback.gripper_config_seen && d->feedback.gripper_max_mm==100);
    snapshot=controller_state(&c,"test-token");
    first=cJSON_GetArrayItem(cJSON_GetObjectItem(snapshot,"devices"),0);
    assert(cJSON_IsTrue(cJSON_GetObjectItem(first,"recovery_required"))); cJSON_Delete(snapshot);
    /* A lost information reply is retried without motor commands, and the
       existing query deadline still bounds all retries. */
    c.query_joint=12; c.query_next=0; d->feedback.limit[0].seen=false;
    tx=d->tx; controller_tick(&c,piper_now());
    assert(d->tx==tx+1&&d->feedback.limit[0].seen&&c.bus.sim_enabled==0);
    now=piper_now(); c.query_start=now; c.query_joint=12; c.query_next=0;
    for (unsigned i=0;i<6;i++) { d->feedback.limit[i].seen=true; d->feedback.limit[i].at=now; d->feedback.acceleration[i].seen=true; d->feedback.acceleration[i].at=now; }
    d->feedback.version[0]=0; d->feedback.gripper_config_at=now;
    tx=d->tx; controller_tick(&c,piper_now());
    assert(d->tx==tx+1&&*d->feedback.version&&c.bus.sim_enabled==0); /* skipped replies do not contaminate firmware request bytes */
    c.query_start=piper_now()-6; tx=d->tx;
    controller_tick(&c,piper_now()); assert(!c.query&&d->tx==tx);
    /* Missing selected arm survives restart even when another arm is present. */
    struct can_device *missing=&c.bus.devices[1];
    strcpy(missing->id,"simulation:missing"); strcpy(missing->label,"Absent arm");
    j=cJSON_CreateObject(); cJSON_AddStringToObject(j,"device_id",missing->id);
    r=controller_action(&c,"/api/select",j,&status); assert(status==200); cJSON_Delete(r); cJSON_Delete(j);
    controller_close(&c); assert(controller_init(&c,true,directory,""));
    assert(!strcmp(c.selected,"simulation:missing"));
    missing=can_bus_find(&c.bus,c.selected); assert(missing&&!missing->present);
    assert(!strcmp(missing->label,"Absent arm"));
    assert(c.bus.devices[0].present); /* Another available device is not substituted. */
    assert(!strcmp(controller_blocker(&c,missing,piper_now()),"USB adapter is missing."));
    assert(c.bus.devices[0].tx==0&&missing->tx==0);
    j=cJSON_CreateObject(); r=controller_action(&c,"/api/reconnect",j,&status);
    assert(status==200&&!c.query&&c.op==OP_IDLE); cJSON_Delete(r); cJSON_Delete(j);
    assert(c.bus.devices[0].tx==0&&missing->tx==0);
    controller_close(&c);
    char path[256]; snprintf(path,sizeof path,"%s/control-state.json",directory); assert(!unlink(path)); assert(!rmdir(directory));
    puts("Control guards passed: stale feedback, faults, mode, limits, foreign sender, identity, storage, raw-command rejection, local echo, durable interruption.");
    return 0;
}

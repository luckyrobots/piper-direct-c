#define _GNU_SOURCE
#include "controller.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

int main(void)
{
    char directory[]="/tmp/piper-configuration-cache-XXXXXX";
    assert(mkdtemp(directory));
    struct controller c; assert(controller_init(&c,true,directory,""));
    struct can_device *d=&c.bus.devices[0];
    double now=piper_now();
    c.bus.sim_enabled=63; controller_tick(&c,now);
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.8-2");
    d->configuration_since=now-7200;
    for (unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=-180,.max=180,.speed=.3,.at=now-3600};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=now-3600};
    }
    d->feedback.gripper_config_seen=true; d->feedback.gripper_max_mm=100; d->feedback.gripper_config_at=now-3600;
    d->stationary_since=now-1;
    assert(!controller_blocker(&c,d,now));
    cJSON *state=controller_state(&c,"test");
    cJSON *device=cJSON_GetArrayItem(cJSON_GetObjectItem(state,"devices"),0);
    cJSON *joints=cJSON_GetObjectItem(device,"joints");
    for (unsigned i=0;i<6;++i) {
        cJSON *joint=cJSON_GetArrayItem(joints,i);
        assert(cJSON_IsTrue(cJSON_GetObjectItem(joint,"limits_fresh")));
        assert(cJSON_IsTrue(cJSON_GetObjectItem(joint,"acceleration_fresh")));
    }
    assert(cJSON_IsTrue(cJSON_GetObjectItem(cJSON_GetObjectItem(device,"gripper"),"configuration_fresh")));
    cJSON_Delete(state);
    /* Exercise the hardware scheduling branch with every transport fd closed.
       No host device is opened. Missing optional CAN-gripper settings must
       not restart a full arm query either. */
    d->feedback.gripper_config_seen=false;
    c.bus.simulated=false; c.auto_query_at=now-120;
    uint64_t tx=d->tx;
    controller_tick(&c,now);
    assert(!c.query && d->tx==tx && *d->feedback.version && d->feedback.limit[0].seen);
    c.bus.simulated=true;

    /* A fresh live stream with old configuration still permits a command. */
    cJSON *body=cJSON_CreateObject();
    cJSON_AddStringToObject(body,"device_id",d->id); cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddStringToObject(body,"owner","configuration-cache-client"); cJSON_AddStringToObject(body,"action","joint");
    cJSON_AddNumberToObject(body,"joint",1); cJSON_AddNumberToObject(body,"degrees",1);
    cJSON_AddBoolToObject(body,"clear",true); cJSON_AddBoolToObject(body,"standalone",true);
    int status; cJSON *reply=controller_action(&c,"/api/command",body,&status);
    assert(status==202); cJSON_Delete(reply);
    while(c.op!=OP_IDLE) { usleep(30000); c.heartbeat=piper_now(); controller_tick(&c,piper_now()); }
    assert(!strcmp(c.phase,"complete"));

    now=piper_now();
    struct can_frame frame={.can_id=0x2a1,.len=8,.data={1,0,1,0,0}};
    can_bus_ingest(d,&frame,false,now+.1);
    assert(d->feedback.limit[0].seen && *d->feedback.version);
    can_bus_ingest(d,&frame,true,now+1); /* Local echo is not robot reconnection. */
    assert(d->feedback.limit[0].seen);
    can_bus_ingest(d,&frame,false,now+1);
    assert(!*d->feedback.version && !*d->feedback.hardware);
    for (unsigned i=0;i<6;++i) assert(!d->feedback.limit[i].seen && !d->feedback.acceleration[i].seen);
    assert(!d->feedback.gripper_config_seen && d->configuration_since==now+1);
    tx=d->tx; reply=controller_action(&c,"/api/command",body,&status);
    assert(status==409 && d->tx==tx); cJSON_Delete(reply);

    /* Reconnection clears the generation and reads settings again. A missing
       optional gripper reply must not hold up the completed arm query. */
    reply=controller_action(&c,"/api/reconnect",body,&status);
    assert(status==200 && c.query); cJSON_Delete(reply);
    for(unsigned tick=0;tick<70 && c.query;++tick) {
        usleep(30000); controller_tick(&c,piper_now());
        d->feedback.gripper_config_seen=false;
    }
    assert(!c.query && !controller_blocker(&c,d,piper_now()));
    assert(strstr(c.message,"Standard CAN gripper configuration unavailable"));
    assert(c.bus.sim_enabled==63 && c.op==OP_IDLE);
    cJSON_Delete(body); controller_close(&c);
    char path[256]; snprintf(path,sizeof path,"%s/control-state.json",directory);
    assert(!unlink(path)); assert(!rmdir(directory));
    puts("Configuration cache passed: no timed refresh or expiry, optional gripper independence, status-gap invalidation, reconnect rereads, no motion replay.");
}

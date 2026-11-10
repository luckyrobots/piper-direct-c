#define _GNU_SOURCE
#include "fixture.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

int main(void)
{
    char directory[]="/tmp/piper-configuration-cache-XXXXXX";
    CHECK(mkdtemp(directory));
    struct controller c; CHECK(controller_init(&c,true,directory,""));
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
    fixture_still(d,now-1);
    CHECK(!controller_blocker(&c,d,now));
    cJSON *state=controller_state(&c,"test");
    cJSON *device=cJSON_GetArrayItem(cJSON_GetObjectItem(state,"devices"),0);
    cJSON *joints=cJSON_GetObjectItem(device,"joints");
    for (unsigned i=0;i<6;++i) {
        cJSON *joint=cJSON_GetArrayItem(joints,i);
        CHECK(cJSON_IsTrue(cJSON_GetObjectItem(joint,"limits_fresh")));
        CHECK(cJSON_IsTrue(cJSON_GetObjectItem(joint,"acceleration_fresh")));
    }
    CHECK(cJSON_IsTrue(cJSON_GetObjectItem(cJSON_GetObjectItem(device,"gripper"),"configuration_fresh")));
    cJSON_Delete(state);
    /* Exercise the hardware scheduling branch with every transport fd closed.
       No host device is opened. Missing optional CAN-gripper settings must
       not restart a full arm query either. */
    d->feedback.gripper_config_seen=false;
    c.bus.simulated=false; LANE(&c)->auto_query_at=now-120;
    uint64_t tx=d->tx;
    controller_tick(&c,now);
    CHECK(!LANE(&c)->query && d->tx==tx && *d->feedback.version && d->feedback.limit[0].seen);
    c.bus.simulated=true;

    /* A fresh live stream with old configuration still permits a command. */
    cJSON *body=cJSON_CreateObject();
    cJSON_AddStringToObject(body,"device_id",d->id); cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddStringToObject(body,"owner","configuration-cache-client"); cJSON_AddStringToObject(body,"action","joint");
    cJSON_AddNumberToObject(body,"joint",1); cJSON_AddNumberToObject(body,"degrees",1);
    cJSON_AddBoolToObject(body,"clear",true); cJSON_AddBoolToObject(body,"standalone",true);
    int status; cJSON *reply=controller_action(&c,"/api/command",body,&status);
    CHECK(status==202); cJSON_Delete(reply);
    while(LANE(&c)->op!=OP_IDLE) { usleep(30000); LANE(&c)->heartbeat=piper_now(); controller_tick(&c,piper_now()); }
    CHECK(!strcmp(LANE(&c)->phase,"complete"));

    now=piper_now();
    struct can_frame frame={.can_id=0x2a1,.len=8,.data={1,0,1,0,0}};
    can_bus_ingest(d,&frame,false,now+.1);
    CHECK(d->feedback.limit[0].seen && *d->feedback.version);
    can_bus_ingest(d,&frame,true,now+1); /* Local echo is not robot reconnection. */
    CHECK(d->feedback.limit[0].seen);
    can_bus_ingest(d,&frame,false,now+1);
    CHECK(!*d->feedback.version && !*d->feedback.hardware);
    for (unsigned i=0;i<6;++i) CHECK(!d->feedback.limit[i].seen && !d->feedback.acceleration[i].seen);
    CHECK(!d->feedback.gripper_config_seen && d->configuration_since==now+1);
    tx=d->tx; reply=controller_action(&c,"/api/command",body,&status);
    CHECK(status==409 && d->tx==tx); cJSON_Delete(reply);

    /* Reconnection clears the generation and reads settings again. A missing
       optional gripper reply must not hold up the completed arm query. */
    reply=controller_action(&c,"/api/reconnect",body,&status);
    CHECK(status==200 && LANE(&c)->query); cJSON_Delete(reply);
    for(unsigned tick=0;tick<70 && LANE(&c)->query;++tick) {
        usleep(30000); controller_tick(&c,piper_now());
        d->feedback.gripper_config_seen=false;
    }
    CHECK(!LANE(&c)->query && !controller_blocker(&c,d,piper_now()));
    CHECK(strstr(LANE(&c)->message,"Standard CAN gripper configuration unavailable"));
    CHECK(c.bus.sim_enabled==63 && LANE(&c)->op==OP_IDLE);
    cJSON_Delete(body); fixture_cleanup(&c,directory);
    puts("Configuration cache passed: no timed refresh or expiry, optional gripper independence, status-gap invalidation, reconnect rereads, no motion replay.");
}

#define _GNU_SOURCE
#include "controller.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static void stop(struct controller *c,cJSON *body,int expected)
{
    int status=0;
    cJSON *result=controller_action(c,"/api/stop",body,&status);
    assert(status==expected); cJSON_Delete(result); cJSON_Delete(body);
}
static cJSON *identity(struct can_device *d)
{
    cJSON *body=cJSON_CreateObject(); cJSON_AddStringToObject(body,"device_id",d->id);
    return body;
}
int main(void)
{
    char directory[]="/tmp/piper-stop-generation-XXXXXX"; assert(mkdtemp(directory));
    struct controller c; assert(controller_init(&c,true,directory,""));
    double now=piper_now(); controller_tick(&c,now);
    struct can_device *d=&c.bus.devices[0];
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.9-0");
    c.bus.sim_enabled=63; controller_tick(&c,piper_now());
    for(unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=-180,.max=180,.speed=.3,.at=now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=now};
    }
    d->stationary_since=now-1;
    cJSON *body=identity(d); cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddStringToObject(body,"action","joint"); cJSON_AddStringToObject(body,"owner","stop-generation-fixture");
    cJSON_AddNumberToObject(body,"joint",6); cJSON_AddNumberToObject(body,"degrees",1);
    cJSON_AddBoolToObject(body,"standalone",true); cJSON_AddBoolToObject(body,"clear",true);
    int status; cJSON *result=controller_action(&c,"/api/command",body,&status);
    assert(status==202&&c.op==OP_MOVE); cJSON_Delete(body); cJSON_Delete(result);
    uint64_t tx=d->tx; unsigned original_generation=d->generation;
    // A reconnect can preserve the serial but must invalidate queued cleanup stops.
    ++d->generation;
    body=identity(d); cJSON_AddNumberToObject(body,"generation",original_generation);
    stop(&c,body,409); assert(d->tx==tx&&c.op==OP_MOVE&&!strcmp(c.owner,"stop-generation-fixture"));
    const char *invalid[]={"null","0","-1","1.5","4294967296","\"2\"","true"};
    for(unsigned i=0;i<sizeof invalid/sizeof invalid[0];++i) {
        body=identity(d); cJSON_AddItemToObject(body,"generation",cJSON_Parse(invalid[i]));
        stop(&c,body,409); assert(d->tx==tx&&c.op==OP_MOVE);
    }
    // Wrong adapter identity must not relinquish an active action to teaching.
    d->generation=original_generation;
    d->feedback.arm[0].control=2; d->feedback.arm[0].teach=1;
    body=cJSON_CreateObject(); cJSON_AddStringToObject(body,"device_id","simulation:other");
    cJSON_AddNumberToObject(body,"generation",d->generation);
    stop(&c,body,409); assert(d->tx==tx&&c.op==OP_MOVE&&!strcmp(c.owner,"stop-generation-fixture"));
    d->feedback.arm[0].control=1; d->feedback.arm[0].teach=0;
    // Matching the active action's current adapter and generation permits STOP.
    body=identity(d); cJSON_AddNumberToObject(body,"generation",d->generation);
    stop(&c,body,200); assert(d->tx==tx+1&&c.op==OP_IDLE);
    // Cleanup stops use the published identity, even during a short feedback gap.
    ++d->generation; d->feedback.arm[0].status_at=now-2; tx=d->tx;
    body=identity(d); cJSON_AddNumberToObject(body,"generation",d->generation);
    stop(&c,body,200); assert(d->tx==tx+1);
    // Existing manual STOP callers need no generation or feedback-health check.
    tx=d->tx; stop(&c,cJSON_CreateObject(),200); assert(d->tx==tx+1);
    controller_close(&c);
    char path[256]; snprintf(path,sizeof path,"%s/control-state.json",directory);
    assert(!unlink(path)); assert(!rmdir(directory));
    puts("STOP generation: stale same-serial requests and malformed generations rejected before TX; current-generation cleanup and legacy manual STOP preserved.");
}

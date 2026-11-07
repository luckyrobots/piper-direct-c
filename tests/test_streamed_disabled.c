#define _GNU_SOURCE
#include "controller.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#if PIPER_ENABLE_EXPERIMENTAL_STREAMED_POSITION
#error This regression must compile the shipping default, never the laboratory opt-in.
#endif
static double clock_now=100;
double __wrap_piper_now(void) { return clock_now; }
static cJSON *request(struct controller *c,const char *action,bool js)
{
    struct can_device *d=&c->bus.devices[0];cJSON *body=cJSON_CreateObject();
    cJSON_AddStringToObject(body,"action",action);cJSON_AddStringToObject(body,"owner","shipping-transport-test");
    cJSON_AddStringToObject(body,"device_id",d->id);cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddNumberToObject(body,"request_id",c->request_id);cJSON_AddNumberToObject(body,"speed_percent",100);
    cJSON_AddBoolToObject(body,"standalone",true);cJSON_AddBoolToObject(body,"clear",true);
    if (!strcmp(action,"follow-start")) cJSON_AddBoolToObject(body,"trajectory",true);
    if (js) cJSON_AddStringToObject(body,"trajectory_transport","streamed-position");
    return body;
}
static void action(struct controller *c,cJSON *body,int expected)
{
    int status;cJSON *response=controller_action(c,"/api/command",body,&status);assert(status==expected);
    cJSON_Delete(response);cJSON_Delete(body);
}
int main(void)
{
    char directory[]="/tmp/piper-streamed-disabled-XXXXXX";assert(mkdtemp(directory));
    struct controller c;assert(controller_init(&c,true,directory,""));struct can_device *d=&c.bus.devices[0];
    memset(c.bus.sim_target,0,sizeof c.bus.sim_target);memset(d->feedback.arm[0].deg,0,sizeof d->feedback.arm[0].deg);
    c.bus.sim_enabled=63;controller_tick(&c,clock_now);
    strcpy(d->feedback.hardware,"H-V1.2-1");strcpy(d->feedback.version,"S-V1.9-0");d->feedback.arm[0].control=1;
    for (unsigned joint=0;joint<6;++joint) {
        d->feedback.limit[joint]=(struct piper_limit){.seen=true,.min=-30,.max=30,.speed=3,.at=clock_now};
        d->feedback.acceleration[joint]=(struct piper_acceleration){.seen=true,.max=5,.at=clock_now};
    }
    d->stationary_since=clock_now-1;assert(!piper_streamed_position_supported(&d->feedback));
    unsigned long long before=d->tx;unsigned request_before=c.request_id;
    action(&c,request(&c,"follow-start",true),409);
    assert(c.op==OP_IDLE&&c.request_id==request_before&&!c.streamed_position_mode&&d->tx==before);
    assert(c.bus.sim_enabled==63&&d->feedback.arm[0].arm_status==0);
    cJSON *state=controller_state(&c,"redacted-test"),*device=cJSON_GetArrayItem(cJSON_GetObjectItem(state,"devices"),0);
    assert(cJSON_IsFalse(cJSON_GetObjectItem(state,"streamed_position_transport_supported")));
    assert(cJSON_IsFalse(cJSON_GetObjectItem(state,"streamed_position_wire_validation_supported")));
    assert(cJSON_IsFalse(cJSON_GetObjectItem(device,"streamed_position_supported")));
    assert(cJSON_IsFalse(cJSON_GetObjectItem(device,"recovery_required")));cJSON_Delete(state);
    struct can_frame frames[4];frames[0]=piper_streamed_position_mode(100);double zero[6]={0};assert(piper_targets(zero,frames+1));
    assert(!can_bus_batch_allowed(d,frames,4)&&!can_bus_send(&c.bus,d,d->generation,frames,4)&&d->tx==before);
    action(&c,request(&c,"follow-start",false),202);clock_now+=.025;controller_tick(&c,clock_now);
    assert(c.op==OP_MOVE&&c.following&&c.trajectory_follow&&!c.streamed_position_mode&&c.pose_tx_mode==0&&d->tx==before+4);
    action(&c,request(&c,"follow-end",false),200);assert(c.op==OP_IDLE&&c.bus.sim_enabled==63&&d->feedback.arm[0].arm_status==0);
    controller_close(&c);char path[160];snprintf(path,sizeof path,"%s/control-state.json",directory);assert(!unlink(path));assert(!rmdir(directory));
    puts("Shipping transport passed: JS capability false, API/wire reject before intent with no TX/STOP/recovery; normal PV admission/hold remains operational.");
}

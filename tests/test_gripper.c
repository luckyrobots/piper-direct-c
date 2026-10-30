#define _GNU_SOURCE
#include "controller.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static cJSON *request(struct can_device *d,double width,bool offset)
{
    cJSON *b=cJSON_CreateObject();
    cJSON_AddStringToObject(b,"device_id",d->id);
    cJSON_AddNumberToObject(b,"generation",d->generation);
    cJSON_AddStringToObject(b,"owner","gripper-regression-client");
    cJSON_AddStringToObject(b,"action","gripper");
    cJSON_AddBoolToObject(b,"standalone",true);
    cJSON_AddBoolToObject(b,"clear",true);
    cJSON_AddBoolToObject(b,"allow_closed_offset",offset);
    cJSON_AddNumberToObject(b,"width_mm",width);
    cJSON_AddNumberToObject(b,"effort_nm",.1);
    return b;
}
static void act(struct controller *c,double width,bool offset,int expected)
{
    struct can_device *d=&c->bus.devices[0];
    cJSON *b=request(d,width,offset); int status;
    uint64_t tx=d->tx;
    cJSON *r=controller_action(c,"/api/command",b,&status);
    if (status!=expected) { char *s=cJSON_Print(r); fprintf(stderr,"%s\n",s); free(s); }
    assert(status==expected); assert(d->tx==tx);
    cJSON_Delete(r); cJSON_Delete(b);
}
int main(void)
{
    char directory[]="/tmp/piper-gripper-XXXXXX"; assert(mkdtemp(directory));
    struct controller c; assert(controller_init(&c,true,directory,""));
    struct can_device *d=&c.bus.devices[0]; double now=piper_now();
    c.bus.sim_enabled=63; controller_tick(&c,now);
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.8-2");
    for (unsigned i=0;i<6;++i) d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=-180,.max=180,.speed=.3,.at=now};
    for (unsigned i=0;i<6;++i) d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=now};
    d->stationary_since=now-1;
    act(&c,95,false,409); /* Unknown hardware range. */
    d->feedback.gripper_config_seen=true; d->feedback.gripper_max_mm=70; d->feedback.gripper_config_at=now;
    act(&c,95,false,400);
    struct can_frame frame; assert(piper_gripper(95,.1,true,&frame));
    assert(!can_bus_send(&c.bus,d,d->generation,&frame,1)); /* Independent CAN guard. */
    d->configuration_since=now-.1;
    d->feedback.gripper_max_mm=100; d->feedback.gripper_config_at=now-.2;
    act(&c,95,false,409); assert(!can_bus_send(&c.bus,d,d->generation,&frame,1));
    d->feedback.gripper_config_at=now;
    act(&c,101,false,400);
    d->feedback.gripper_mm=-1.3; c.bus.sim_gripper=-1.3;
    act(&c,5,false,409); act(&c,0,true,409);
    d->feedback.gripper_mm=-2.001; act(&c,5,true,409);
    d->feedback.gripper_mm=-1.3;
    cJSON *s=controller_state(&c,"test");
    cJSON *g=cJSON_GetObjectItem(cJSON_GetArrayItem(cJSON_GetObjectItem(s,"devices"),0),"gripper");
    assert(cJSON_GetObjectItem(g,"width_mm")->valuedouble==-1.3);
    assert(cJSON_GetObjectItem(g,"max_width_mm")->valuedouble==100); cJSON_Delete(s);
    act(&c,5,true,202);
    usleep(25000); c.heartbeat=piper_now(); controller_tick(&c,piper_now());
    assert(c.op==OP_GRIPPER&&c.gripper_command==1&&c.gripper_effort==.1);
    unsigned ticks=0;
    while (c.op!=OP_IDLE&&ticks++<250) { usleep(10000); c.heartbeat=piper_now(); controller_tick(&c,piper_now()); }
    assert(c.op==OP_IDLE&&!strcmp(c.phase,"complete"));
    assert(d->feedback.gripper_mm>=4.5&&!c.gripper_entry);
    act(&c,95,false,202);
    cJSON *hold=request(d,95,false); cJSON_ReplaceItemInObject(hold,"action",cJSON_CreateString("hold"));
    int status; cJSON *reply=controller_action(&c,"/api/command",hold,&status); assert(status==409); cJSON_Delete(reply);
    usleep(25000); c.heartbeat=piper_now(); controller_tick(&c,piper_now());
    cJSON_ReplaceItemInObject(hold,"owner",cJSON_CreateString("different-client-owner"));
    reply=controller_action(&c,"/api/command",hold,&status); assert(status==409); cJSON_Delete(reply);
    cJSON_ReplaceItemInObject(hold,"owner",cJSON_CreateString("gripper-regression-client"));
    uint64_t holding_tx=d->tx; double held=c.bus.sim_gripper;
    reply=controller_action(&c,"/api/command",hold,&status); assert(status==200); cJSON_Delete(reply); cJSON_Delete(hold);
    assert(c.op==OP_IDLE&&d->tx==holding_tx&&c.bus.sim_gripper==held&&d->feedback.gripper_enabled);
    act(&c,95,false,202);
    d->feedback.gripper_max_mm=70; usleep(25000); c.heartbeat=piper_now(); controller_tick(&c,piper_now());
    assert(c.op==OP_IDLE&&!strcmp(c.phase,"stopped"));
    assert(strstr(c.message,"configuration changed"));
    d->feedback.gripper_config_seen=false;
    assert(piper_gripper(0,.1,false,&frame)); assert(can_bus_send(&c.bus,d,d->generation,&frame,1));
    frame=(struct can_frame){.can_id=0x477,.len=8,.data={4,0,0,0,3}};
    assert(can_bus_send(&c.bus,d,d->generation,&frame,1));
    frame.data[1]=1; uint64_t tx=d->tx;
    assert(!can_bus_send(&c.bus,d,d->generation,&frame,1)&&d->tx==tx); /* No parameter writes. */
    controller_close(&c);
    char path[256]; snprintf(path,sizeof path,"%s/control-state.json",directory); assert(!unlink(path)); assert(!rmdir(directory));
    puts("Gripper guards passed: verified range, low effort, raw negative feedback, bounded entry, range change, disable and read-only query.");
    return 0;
}

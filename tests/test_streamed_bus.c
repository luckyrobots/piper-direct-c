#define _GNU_SOURCE
#include "controller.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

/* No can_bus_send/receive wrapper: this must traverse the production batch
   allowlist before the hardware-disabled simulator consumes any frame. */
static double clock_now=100;
double __wrap_piper_now(void) { return clock_now; }
static void rejected(struct can_bus *bus,struct can_device *device,const struct can_frame *frames,size_t count)
{
    unsigned long long tx=device->tx;double before[6];memcpy(before,bus->sim_target,sizeof before);
    assert(!can_bus_send(bus,device,device->generation,frames,count));
    assert(device->tx==tx&&!memcmp(before,bus->sim_target,sizeof before));
}
static cJSON *request(struct controller *c,const char *action,const char *transport)
{
    struct can_device *d=&c->bus.devices[0];cJSON *body=cJSON_CreateObject();
    cJSON_AddStringToObject(body,"action",action);cJSON_AddStringToObject(body,"owner","real-bus-qualification");
    cJSON_AddStringToObject(body,"device_id",d->id);cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddNumberToObject(body,"request_id",c->request_id);cJSON_AddNumberToObject(body,"speed_percent",100);
    cJSON_AddBoolToObject(body,"standalone",true);cJSON_AddBoolToObject(body,"clear",true);
    if (!strcmp(action,"follow-start")) cJSON_AddBoolToObject(body,"trajectory",true);
    if (transport) cJSON_AddStringToObject(body,"trajectory_transport",transport);
    return body;
}
static void action(struct controller *c,cJSON *body)
{
    int status;cJSON *response=controller_action(c,"/api/command",body,&status);
    assert(status>=200&&status<300);cJSON_Delete(response);cJSON_Delete(body);
}
int main(void)
{
    struct can_bus bus;can_bus_init(&bus,true);struct can_device *d=&bus.devices[0];
    strcpy(d->feedback.hardware,"H-V1.2-1");strcpy(d->feedback.version,"S-V1.9-0");
    struct can_frame frames[5];frames[0]=piper_streamed_position_mode(100);
    const double pose[6]={.125,.5,-.75,1,-1,12};assert(piper_targets(pose,frames+1));
    if (!can_bus_send(&bus,d,d->generation,frames,4)) {
        fputs("Production can_bus_send rejected the qualified complete JS position batch.\n",stderr);
        return 1;
    }
    assert(d->tx==4);for (unsigned joint=0;joint<6;++joint) assert(fabs(bus.sim_target[joint]-pose[joint])<1e-9);
    strcpy(d->feedback.version,"S-V1.9-1");assert(can_bus_send(&bus,d,d->generation,frames,4));
    strcpy(d->feedback.version,"S-V1.9-0");
    for (unsigned invalid=0;invalid<17;++invalid) {
        struct can_frame bad[5];memcpy(bad,frames,4*sizeof *bad);size_t count=4;
        switch (invalid) {
            case 0:count=1;break;
            case 1:count=3;break;
            case 2:bad[1].can_id=0x156;bad[2].can_id=0x155;break;
            case 3:bad[2].can_id=0x155;break;
            case 4:bad[0].data[0]=2;break;
            case 5:bad[0].data[0]=0;break;
            case 6:bad[0].data[1]=4;break;
            case 7:bad[0].data[2]=0;break;
            case 8:bad[0].data[2]=101;break;
            case 9:bad[0].data[3]=0xae;break;
            case 10:bad[0].data[4]=1;break;
            case 11:bad[0].data[7]=1;break;
            case 12:bad[3].len=7;break;
            case 13:bad[4]=piper_mode(100);count=5;break;
            case 14:bad[3].can_id=0x15a;break;
            case 15:strcpy(d->feedback.version,"S-V1.9-2");break;
            default:d->feedback.hardware[0]=0;break;
        }
        rejected(&bus,d,bad,count);
        strcpy(d->feedback.hardware,"H-V1.2-1");strcpy(d->feedback.version,"S-V1.9-0");
    }
    frames[0]=piper_mode(100);assert(can_bus_send(&bus,d,d->generation,frames,4));
    can_bus_close(&bus);

    char directory[]="/tmp/piper-streamed-bus-XXXXXX";assert(mkdtemp(directory));
    struct controller c;assert(controller_init(&c,true,directory,""));d=&c.bus.devices[0];
    memset(c.bus.sim_target,0,sizeof c.bus.sim_target);memset(d->feedback.arm[0].deg,0,sizeof d->feedback.arm[0].deg);
    c.bus.sim_enabled=63;controller_tick(&c,clock_now);
    strcpy(d->feedback.hardware,"H-V1.2-1");strcpy(d->feedback.version,"S-V1.9-0");d->feedback.arm[0].control=1;
    for (unsigned joint=0;joint<6;++joint) {
        d->feedback.limit[joint]=(struct piper_limit){.seen=true,.min=-30,.max=30,.speed=3,.at=clock_now};
        d->feedback.acceleration[joint]=(struct piper_acceleration){.seen=true,.max=5,.at=clock_now};
    }
    d->stationary_since=clock_now-1;
    action(&c,request(&c,"follow-start","streamed-position"));
    unsigned sequence=0;
    for (unsigned step=0;step<200;++step) {
        cJSON *body=request(&c,"follow-update",NULL);double target[6]={0,0,0,0,0,step<100?1:0};
        cJSON_AddNumberToObject(body,"sequence",++sequence);cJSON_AddItemToObject(body,"degrees",cJSON_CreateDoubleArray(target,6));action(&c,body);
        clock_now+=.025;controller_tick(&c,clock_now);
        assert(c.op==OP_MOVE&&c.following&&c.trajectory_follow&&c.pose_tx_valid&&c.pose_tx_mode==0xad);
        assert(c.bus.sim_enabled==63&&d->feedback.arm[0].arm_status==0);
        for (unsigned joint=0;joint<6;++joint) assert(fabs(c.command[joint]-c.bus.sim_target[joint])<1e-9);
    }
    if (fabs(d->feedback.arm[0].deg[5])>=.01) fprintf(stderr,"End wrist actual %.6f reference %.6f target %.6f\n",d->feedback.arm[0].deg[5],c.command[5],c.target[5]);
    assert(fabs(d->feedback.arm[0].deg[5])<.01);action(&c,request(&c,"follow-end",NULL));
    assert(c.op==OP_IDLE&&c.streamed_position_mode&&c.bus.sim_enabled==63&&d->feedback.arm[0].arm_status==0);
    d->stationary_since=clock_now-1;action(&c,request(&c,"follow-start",NULL));
    clock_now+=.025;controller_tick(&c,clock_now);assert(c.op==OP_MOVE&&c.pose_tx_mode==0);
    action(&c,request(&c,"follow-end",NULL));controller_close(&c);
    char path[160];snprintf(path,sizeof path,"%s/control-state.json",directory);assert(!unlink(path));assert(!rmdir(directory));
    puts("Streamed production-bus integration passed: real batch allowlist, no partial writes on malformed JS, full controller ticks/hold and ordinary PV return.");
}

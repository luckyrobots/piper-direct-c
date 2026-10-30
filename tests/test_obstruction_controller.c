#define _GNU_SOURCE
#include "controller.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
static cJSON *event(struct controller *c) {
    cJSON *s=controller_state(c,"test"), *d=cJSON_GetArrayItem(cJSON_GetObjectItem(s,"devices"),0);
    cJSON *e=cJSON_Duplicate(cJSON_GetObjectItem(d,"stop_event"),true); cJSON_Delete(s); return e;
}
int main(void) {
    char dir[]="/tmp/piper-obstruction-XXXXXX"; assert(mkdtemp(dir));
    struct controller c; assert(controller_init(&c,true,dir,""));
    double now=piper_now(); struct can_device *d=&c.bus.devices[0];
    c.bus.sim_enabled=63; controller_tick(&c,now);
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.8-2");
    for(unsigned i=0;i<6;i++) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=-180,.max=180,.speed=.3,.at=now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=now};
    }
    d->stationary_since=now-1;
    cJSON *j=cJSON_CreateObject();
    cJSON_AddStringToObject(j,"device_id",d->id);cJSON_AddNumberToObject(j,"generation",d->generation);
    cJSON_AddStringToObject(j,"owner","obstruction-regression");cJSON_AddStringToObject(j,"action","joint");
    cJSON_AddNumberToObject(j,"joint",1);cJSON_AddNumberToObject(j,"degrees",20);
    cJSON_AddBoolToObject(j,"standalone",true);cJSON_AddBoolToObject(j,"clear",true);
    int status; unsigned long long tx=d->tx;
    cJSON_AddNumberToObject(j,"speed_percent",0);
    cJSON *reply=controller_action(&c,"/api/command",j,&status);assert(status==409&&c.op==OP_IDLE&&d->tx==tx);cJSON_Delete(reply);
    cJSON_SetNumberValue(cJSON_GetObjectItem(j,"speed_percent"),101);
    reply=controller_action(&c,"/api/command",j,&status);assert(status==400&&d->tx==tx);cJSON_Delete(reply);
    cJSON_SetNumberValue(cJSON_GetObjectItem(j,"speed_percent"),100);
    d->feedback.limit[4].speed=.2; /* The slowest reported joint sets the shared ceiling. */
    reply=controller_action(&c,"/api/command",j,&status);assert(status==202);
    assert(fabs(c.speed-.2*180/3.141592653589793)<.000001&&c.speed_percent==100);
    cJSON_Delete(reply);cJSON_Delete(j);
    double start=now;
    /* Inject a fixed measured joint, with healthy fresh CAN feedback. */
    for(unsigned n=0;n<80&&c.op!=OP_IDLE;n++) {
        c.bus.sim_target[0]=d->feedback.arm[0].deg[0];
        now+=.01; c.heartbeat=now; controller_tick(&c,now);
    }
    assert(c.op==OP_IDLE&&c.obstruction_joint==1);
    assert(now-start>=.5&&now-start<.65);
    cJSON *first=event(&c);assert(cJSON_GetObjectItem(first,"joint")->valueint==1);
    char *original=cJSON_PrintUnformatted(first);cJSON_Delete(first);
    controller_stop(&c,"Operator stop.");first=event(&c);char *again=cJSON_PrintUnformatted(first);
    assert(!strcmp(original,again));free(again);cJSON_Delete(first);
    controller_close(&c);assert(controller_init(&c,true,dir,""));
    first=event(&c);again=cJSON_PrintUnformatted(first);assert(!strcmp(original,again));
    free(original);free(again);cJSON_Delete(first);assert(c.op==OP_IDLE);
    controller_close(&c);char path[256];snprintf(path,sizeof path,"%s/control-state.json",dir);unlink(path);rmdir(dir);
    puts("Controller obstruction passed: frozen feedback stops in 500–650 ms, first evidence survives repeated STOP and restart, no replay.");
}

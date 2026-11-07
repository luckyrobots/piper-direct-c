#define _GNU_SOURCE
#include "controller.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static cJSON *command(struct controller *c,const char *action,unsigned sequence,double wrist)
{
    struct can_device *d=&c->bus.devices[0];
    cJSON *j=cJSON_CreateObject();
    cJSON_AddStringToObject(j,"device_id",d->id);
    cJSON_AddNumberToObject(j,"generation",d->generation);
    cJSON_AddStringToObject(j,"owner","follow-regression-client");
    cJSON_AddStringToObject(j,"action",action);
    cJSON_AddNumberToObject(j,"request_id",c->request_id);
    cJSON_AddNumberToObject(j,"sequence",sequence);
    cJSON_AddNumberToObject(j,"speed_percent",100);
    cJSON_AddBoolToObject(j,"standalone",true);
    cJSON_AddBoolToObject(j,"clear",true);
    double angles[6]={0,0,0,0,0,wrist};
    cJSON_AddItemToObject(j,"degrees",cJSON_CreateDoubleArray(angles,6));
    return j;
}
static void send_command(struct controller *c,cJSON *j,int expected)
{
    int status;
    cJSON *reply=controller_action(c,"/api/command",j,&status);
    if(status!=expected) {
        char *json=cJSON_PrintUnformatted(reply);
        fprintf(stderr,"status %d expected %d: %s\n",status,expected,json);
        cJSON_free(json);
    }
    assert(status==expected);
    cJSON_Delete(reply); cJSON_Delete(j);
}
static void setup(struct controller *c,char *directory)
{
    assert(mkdtemp(directory)); assert(controller_init(c,true,directory,""));
    memset(c->bus.sim_target,0,sizeof c->bus.sim_target);
    memset(c->bus.devices[0].feedback.arm[0].deg,0,sizeof c->bus.devices[0].feedback.arm[0].deg);
    c->bus.sim_enabled=63;
    double now=piper_now(); controller_tick(c,now);
    struct can_device *d=&c->bus.devices[0];
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.8-2");
    for(unsigned i=0;i<6;i++) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=-180,.max=180,.speed=.3,.at=now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=now};
    }
    d->stationary_since=now-1;
    send_command(c,command(c,"follow-start",0,0),202);
    assert(c->following&&c->op==OP_MOVE);
}
static void cleanup(struct controller *c,const char *directory)
{
    controller_close(c); char path[256]; snprintf(path,sizeof path,"%s/control-state.json",directory);
    assert(!unlink(path)); assert(!rmdir(directory));
}
int main(void)
{
    struct controller c; char first[]="/tmp/piper-follow-XXXXXX"; setup(&c,first);
    struct can_device *d=&c.bus.devices[0];
    unsigned long long tx=d->tx;
    send_command(&c,command(&c,"follow-update",1,3),202);
    assert(c.target[5]==3&&d->tx==tx); // Updates replace a target; only tick transmits.
    send_command(&c,command(&c,"follow-update",1,-3),409); assert(c.target[5]==3);
    cJSON *wrong=command(&c,"follow-update",2,-3);
    cJSON_SetValuestring(cJSON_GetObjectItem(wrong,"owner"),"wrong-owner");
    send_command(&c,wrong,409); assert(c.target[5]==3);
    send_command(&c,command(&c,"follow-update",2,181),400); assert(c.target[5]==3);
    send_command(&c,command(&c,"follow-update",2,-3),202); assert(c.target[5]==-3);
    usleep(21000);
    double previous=c.command[5], tick_at=piper_now(), elapsed=fmin(tick_at-c.last_tick,.05);
    controller_tick(&c,tick_at);
    assert(fabs(c.command[5]-previous)<=c.speed*(elapsed+.000001)&&fabs(c.command[5]-d->feedback.arm[0].deg[5])<=1);
    for(unsigned i=0;i<5;i++) assert(c.command[i]==0);
    send_command(&c,command(&c,"follow-end",3,0),200);
    assert(c.op==OP_IDLE&&!c.following&&c.bus.sim_enabled==63);
    tx=d->tx; send_command(&c,command(&c,"follow-update",4,5),400); assert(d->tx==tx);
    cleanup(&c,first);

    char second[]="/tmp/piper-follow-expiry-XXXXXX"; setup(&c,second);
    send_command(&c,command(&c,"follow-update",1,8),202);
    double expired=c.follow_updated+.251;
    controller_tick(&c,expired);
    assert(c.op==OP_IDLE&&!c.following&&c.bus.sim_enabled==63);
    assert(strstr(c.message,"expired"));
    assert(fabs(c.target[5]-c.bus.devices[0].feedback.arm[0].deg[5])<.0001);
    cleanup(&c,second);

    /* Releasing commits the LAST target, including a final cursor change that
       arrived between streamed updates. It keeps the lease, not the mouse. */
    char committed[]="/tmp/piper-follow-commit-XXXXXX"; setup(&c,committed);
    send_command(&c,command(&c,"follow-update",1,1),202);
    send_command(&c,command(&c,"follow-commit",2,3),202);
    assert(!c.following&&c.follow_committed&&c.op==OP_MOVE&&c.target[5]==3);
    send_command(&c,command(&c,"follow-update",3,-3),409);
    double completion_started=piper_now();
    while(c.op!=OP_IDLE&&piper_now()-completion_started<4) {
        int status; cJSON *j=command(&c,"",0,0);
        cJSON *response=controller_action(&c,"/api/heartbeat",j,&status);
        assert(status==200); cJSON_Delete(j); cJSON_Delete(response);
        usleep(20000); controller_tick(&c,piper_now());
    }
    assert(c.op==OP_IDLE&&c.obstruction_joint==0&&c.bus.sim_enabled==63);
    assert(fabs(c.bus.devices[0].feedback.arm[0].deg[5]-3)<.1);
    cleanup(&c,committed);

    char cancelled[]="/tmp/piper-follow-cancel-tail-XXXXXX"; setup(&c,cancelled);
    send_command(&c,command(&c,"follow-commit",1,30),202);
    usleep(21000); controller_tick(&c,piper_now());
    send_command(&c,command(&c,"follow-end",2,0),200);
    assert(c.op==OP_IDLE&&!c.follow_committed&&c.bus.sim_enabled==63);
    assert(fabs(c.target[5]-c.bus.devices[0].feedback.arm[0].deg[5])<.001);
    cleanup(&c,cancelled);

    char third[]="/tmp/piper-follow-obstruction-XXXXXX"; setup(&c,third);
    double started=piper_now(); unsigned sequence=0;
    while(c.op!=OP_IDLE&&piper_now()-started<1) {
        ++sequence; send_command(&c,command(&c,"follow-update",sequence,10+(sequence%3)),202);
        c.bus.sim_target[5]=0;
        usleep(20000); controller_tick(&c,piper_now());
    }
    assert(c.op==OP_IDLE&&c.obstruction_joint==6);
    assert(piper_now()-started<.75);
    cleanup(&c,third);
    puts("Following: final-target release/completion, cancel/expiry hold, owner/sequence/limits and obstruction across retargets passed.");
}

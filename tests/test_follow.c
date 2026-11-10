#define _GNU_SOURCE
#include "fixture.h"
#include "fake_bus.h"
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
    cJSON_AddNumberToObject(j,"request_id",LANE(c)->request_id);
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
    CHECK(status==expected);
    cJSON_Delete(reply); cJSON_Delete(j);
}
static void setup(struct controller *c,char *directory)
{
    CHECK(controller_init(c,true,directory,""));
    memset(c->bus.sim_target,0,sizeof c->bus.sim_target);
    memset(c->bus.devices[0].feedback.arm[0].deg,0,sizeof c->bus.devices[0].feedback.arm[0].deg);
    c->bus.sim_enabled=63;
    double now=piper_now(); controller_tick(c,now);
    struct can_device *d=&c->bus.devices[0];
    fixture_configure(d,"S-V1.8-2",-180,180,.3,now);
    fixture_still(d,now-1);
    send_command(c,command(c,"follow-start",0,0),202);
    CHECK(LANE(c)->follow==FOLLOW_LIVE&&LANE(c)->op==OP_MOVE);
}
static void scenarios(void)
{
    struct controller c; char first[FIXTURE_DIRECTORY]; fixture_directory(first,sizeof first,"piper-follow"); setup(&c,first);
    struct can_device *d=&c.bus.devices[0];
    unsigned long long tx=d->tx;
    send_command(&c,command(&c,"follow-update",1,3),202);
    CHECK(LANE(&c)->target[5]==3&&d->tx==tx); // Updates replace a target; only tick transmits.
    send_command(&c,command(&c,"follow-update",1,-3),409); CHECK(LANE(&c)->target[5]==3);
    cJSON *wrong=command(&c,"follow-update",2,-3);
    cJSON_SetValuestring(cJSON_GetObjectItem(wrong,"owner"),"wrong-owner");
    send_command(&c,wrong,409); CHECK(LANE(&c)->target[5]==3);
    send_command(&c,command(&c,"follow-update",2,181),400); CHECK(LANE(&c)->target[5]==3);
    send_command(&c,command(&c,"follow-update",2,-3),202); CHECK(LANE(&c)->target[5]==-3);
    fake_bus_advance(0.021);
    double previous=LANE(&c)->command[5], tick_at=piper_now(), elapsed=fmin(tick_at-LANE(&c)->last_tick,.05);
    controller_tick(&c,tick_at);
    CHECK(fabs(LANE(&c)->command[5]-previous)<=LANE(&c)->speed*(elapsed+.000001)&&fabs(LANE(&c)->command[5]-d->feedback.arm[0].deg[5])<=1);
    for(unsigned i=0;i<5;i++) CHECK(LANE(&c)->command[i]==0);
    send_command(&c,command(&c,"follow-end",3,0),200);
    CHECK(LANE(&c)->op==OP_IDLE&&LANE(&c)->follow!=FOLLOW_LIVE&&c.bus.sim_enabled==63);
    tx=d->tx; send_command(&c,command(&c,"follow-update",4,5),400); CHECK(d->tx==tx);
    fixture_cleanup(&c,first);

    char second[FIXTURE_DIRECTORY]; fixture_directory(second,sizeof second,"piper-follow-expiry"); setup(&c,second);
    send_command(&c,command(&c,"follow-update",1,8),202);
    double expired=LANE(&c)->follow_updated+.251;
    controller_tick(&c,expired);
    CHECK(LANE(&c)->op==OP_IDLE&&LANE(&c)->follow!=FOLLOW_LIVE&&c.bus.sim_enabled==63);
    CHECK(strstr(LANE(&c)->message,"expired"));
    CHECK(fabs(LANE(&c)->target[5]-c.bus.devices[0].feedback.arm[0].deg[5])<.0001);
    fixture_cleanup(&c,second);

    /* Releasing commits the LAST target, including a final cursor change that
       arrived between streamed updates. It keeps the lease, not the mouse. */
    char committed[FIXTURE_DIRECTORY]; fixture_directory(committed,sizeof committed,"piper-follow-commit"); setup(&c,committed);
    send_command(&c,command(&c,"follow-update",1,1),202);
    send_command(&c,command(&c,"follow-commit",2,3),202);
    CHECK(LANE(&c)->follow!=FOLLOW_LIVE&&LANE(&c)->follow==FOLLOW_COMMITTED&&LANE(&c)->op==OP_MOVE&&LANE(&c)->target[5]==3);
    send_command(&c,command(&c,"follow-update",3,-3),409);
    double completion_started=piper_now();
    while(LANE(&c)->op!=OP_IDLE&&piper_now()-completion_started<4) {
        int status; cJSON *j=command(&c,"",0,0);
        cJSON *response=controller_action(&c,"/api/heartbeat",j,&status);
        CHECK(status==200); cJSON_Delete(j); cJSON_Delete(response);
        fake_bus_advance(0.02); controller_tick(&c,piper_now());
    }
    CHECK(LANE(&c)->op==OP_IDLE&&LANE(&c)->obstruction_joint==0&&c.bus.sim_enabled==63);
    CHECK(fabs(c.bus.devices[0].feedback.arm[0].deg[5]-3)<.1);
    fixture_cleanup(&c,committed);

    char cancelled[FIXTURE_DIRECTORY]; fixture_directory(cancelled,sizeof cancelled,"piper-follow-cancel-tail"); setup(&c,cancelled);
    send_command(&c,command(&c,"follow-commit",1,30),202);
    fake_bus_advance(0.021); controller_tick(&c,piper_now());
    send_command(&c,command(&c,"follow-end",2,0),200);
    CHECK(LANE(&c)->op==OP_IDLE&&LANE(&c)->follow!=FOLLOW_COMMITTED&&c.bus.sim_enabled==63);
    CHECK(fabs(LANE(&c)->target[5]-c.bus.devices[0].feedback.arm[0].deg[5])<.001);
    fixture_cleanup(&c,cancelled);

    char third[FIXTURE_DIRECTORY]; fixture_directory(third,sizeof third,"piper-follow-obstruction"); setup(&c,third);
    double started=piper_now(); unsigned sequence=0;
    while(LANE(&c)->op!=OP_IDLE&&piper_now()-started<1) {
        ++sequence; send_command(&c,command(&c,"follow-update",sequence,10+(sequence%3)),202);
        c.bus.sim_target[5]=0;
        fake_bus_advance(0.02); controller_tick(&c,piper_now());
    }
    CHECK(LANE(&c)->op==OP_IDLE&&LANE(&c)->obstruction_joint==6);
    CHECK(piper_now()-started<.75);
    /* B9: the STOP recorded the live session, then retired every follow flag,
       so a later STOP cannot report a stale following session. */
    cJSON *record=cJSON_GetObjectItem(cJSON_GetObjectItem(c.saved,"devices"),c.bus.devices[0].id);
    CHECK(cJSON_IsTrue(cJSON_GetObjectItem(cJSON_GetObjectItem(record,"stop_event"),"following")));
    CHECK(LANE(&c)->follow!=FOLLOW_LIVE&&LANE(&c)->follow!=FOLLOW_COMMITTED&&!(LANE(&c)->stage==STAGE_MOVE_TRAJECTORY)&&!(LANE(&c)->stage==STAGE_MOVE_SYNCHRONIZED));
    cJSON_ReplaceItemInObject(record,"stop_event_active",cJSON_CreateFalse());
    controller_stop(&c,"Second stop after recovery.");
    CHECK(cJSON_IsFalse(cJSON_GetObjectItem(cJSON_GetObjectItem(record,"stop_event"),"following")));
    fixture_cleanup(&c,third);
    puts("Following: final-target release/completion, cancel/expiry hold, owner/sequence/limits and obstruction across retargets passed.");
}
static const struct check_test tests[]={{"follow","every scenario of this file",scenarios,NULL}};
int main(void) { return check_main("follow",tests,1); }

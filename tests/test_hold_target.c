#define _GNU_SOURCE
#include "fixture.h"
#include "fake_bus.h"

/* A measured joint a hair past its limit (encoder noise at the stop) must be
   held on the limit, the same way a following hold does it. */
static cJSON *command(struct can_device *d,const char *action)
{
    cJSON *body=cJSON_CreateObject(); cJSON_AddStringToObject(body,"device_id",d->id);
    cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddStringToObject(body,"action",action); cJSON_AddStringToObject(body,"owner","hold-target-fixture");
    cJSON_AddBoolToObject(body,"standalone",true); cJSON_AddBoolToObject(body,"clear",true);
    return body;
}
static int post(struct controller *c,cJSON *body)
{
    int status=0; cJSON_Delete(controller_action(c,"/api/command",body,&status)); cJSON_Delete(body); return status;
}
static void start_move(struct controller *c,struct can_device *d,const char *directory)
{
    CHECK(controller_init(c,true,directory,""));
    double now=piper_now(); controller_tick(c,now);
    c->bus.sim_enabled=63; controller_tick(c,piper_now());
    fixture_configure(d,"S-V1.9-0",-180,180,.3,now); fixture_still(d,now-1);
    cJSON *body=command(d,"joint"); cJSON_AddNumberToObject(body,"joint",6); cJSON_AddNumberToObject(body,"degrees",1);
    CHECK(post(c,body)==202&&LANE(c)->op==OP_MOVE);
}
static void scenarios(void)
{
    char directory[96]; struct controller c; struct can_device *d=&c.bus.devices[0];

    /* Inside the feedback slop: the hold target is the limit and the move survives its next tick. */
    fixture_directory(directory,sizeof directory,"piper-hold-target");
    start_move(&c,d,directory);
    double measured=d->feedback.arm[0].deg[5],limit=measured+.05;
    d->feedback.limit[5].min=limit;
    CHECK(post(&c,command(d,"hold"))==202);
    CHECK(LANE(&c)->op==OP_MOVE&&LANE(&c)->target[5]==limit&&LANE(&c)->command[5]==limit);
    controller_tick(&c,piper_now());
    CHECK(LANE(&c)->op==OP_MOVE&&LANE(&c)->target[5]==limit);
    fixture_cleanup(&c,directory);

    /* Beyond the slop there is no legal hold target: refuse the hold and STOP at once. */
    fixture_directory(directory,sizeof directory,"piper-hold-target");
    start_move(&c,d,directory);
    uint64_t tx=d->tx;
    d->feedback.limit[5].min=d->feedback.arm[0].deg[5]+.5;
    CHECK(post(&c,command(d,"hold"))==409);
    CHECK(LANE(&c)->op==OP_IDLE&&d->tx==tx+1&&!strcmp(LANE(&c)->phase,"stopped"));
    fixture_cleanup(&c,directory);
    puts("Hold target: a measured pose within the limit slop is held on the limit; beyond it the hold stops instead of commanding an illegal target.");
}
static const struct check_test tests[]={{"hold-target","every scenario of this file",scenarios,NULL}};
int main(void) { return check_main("hold-target",tests,1); }

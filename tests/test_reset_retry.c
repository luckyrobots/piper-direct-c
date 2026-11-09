#define _GNU_SOURCE
#include "fixture.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

/* Deterministic feedback/clock fixture. No real CAN transport is opened and
   every transmitted packet is checked: recovery must never enable or move. */
static double clock_now=100;
static unsigned resets,stops;
double __wrap_piper_now(void) { return clock_now; }
void __wrap_can_bus_receive(struct can_bus *bus,double now) { (void)bus; (void)now; }
bool __wrap_can_bus_send(struct can_bus *bus,struct can_device *d,unsigned generation,const struct can_frame *f,size_t count)
{
    CHECK(bus->simulated && d && generation==d->generation && count==1);
    CHECK(f[0].can_id==0x150 && f[0].len==8);
    for (unsigned i=1;i<8;++i) CHECK(f[0].data[i]==0);
    if (f[0].data[0]==2) ++resets;
    else { CHECK(f[0].data[0]==1); ++stops; }
    ++d->tx;
    return true;
}
static void fresh(struct controller *c)
{
    struct can_device *d=&c->bus.devices[0];
    struct piper_sample *s=&d->feedback.arm[0];
    s->status_seen=true; s->status_at=clock_now;
    for (unsigned i=0;i<6;++i) {
        s->joint_seen[i]=true; s->joint_at[i]=clock_now;
        d->feedback.motor[i].seen=true; d->feedback.motor[i].at=clock_now;
        d->feedback.motor[i].high_seen=true; d->feedback.motor[i].high_at=clock_now;
    }
}
static cJSON *request(struct controller *c)
{
    struct can_device *d=&c->bus.devices[0]; cJSON *body=cJSON_CreateObject();
    cJSON_AddStringToObject(body,"device_id",d->id); cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddStringToObject(body,"owner","reset-retry-regression-client"); cJSON_AddStringToObject(body,"action","reset");
    cJSON_AddBoolToObject(body,"standalone",true); cJSON_AddBoolToObject(body,"supported",true); cJSON_AddBoolToObject(body,"clear",true);
    return body;
}
static void action(struct controller *c,cJSON *body,int expected)
{
    int status; unsigned before=resets+stops;
    cJSON *reply=controller_action(c,"/api/command",body,&status);
    if (status!=expected) { char *text=cJSON_Print(reply); fprintf(stderr,"reset reply %d, expected %d: %s\n",status,expected,text); free(text); }
    CHECK(status==expected);
    if (expected!=202) CHECK(resets+stops==before);
    cJSON_Delete(reply); cJSON_Delete(body);
}
static bool pending(struct controller *c)
{
    const cJSON *device=cJSON_GetObjectItem(cJSON_GetObjectItem(c->saved,"devices"),c->selected);
    return cJSON_IsTrue(cJSON_GetObjectItem(device,"reset_pending"));
}
static void fixture(struct controller *c,char directory[96])
{
    strcpy(directory,"/tmp/piper-reset-retry-XXXXXX"); CHECK(mkdtemp(directory));
    clock_now+=10; CHECK(controller_init(c,true,directory,""));
    struct can_device *d=&c->bus.devices[0];
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.8-2");
    d->feedback.arm[0].control=0; d->feedback.arm[0].arm_status=1;
    d->feedback.arm[0].motion_mode=1; d->feedback.arm[0].teach=1;
    fixture_still(d,clock_now-1);
    for (unsigned i=0;i<6;++i) d->feedback.motor[i].enabled=false;
    fresh(c);
}
static void close_fixture(struct controller *c,const char *directory)
{
    CHECK(LANE(c)->op==OP_IDLE); controller_close(c);
    char path[128]; snprintf(path,sizeof path,"%s/control-state.json",directory); CHECK(!unlink(path)); CHECK(!rmdir(directory));
}
int main(void)
{
    struct controller c; char directory[96]; fixture(&c,directory);
    struct can_device *d=&c.bus.devices[0]; unsigned initial_resets=resets;
    /* Physical light is off: STOP can retain teach=1. Reset is a single
       explicit packet, and an in-flight repeat cannot send another one. */
    action(&c,request(&c),202); CHECK(LANE(&c)->op==OP_RESET && pending(&c) && resets==initial_resets+1);
    unsigned first_request=LANE(&c)->request_id; action(&c,request(&c),409);
    /* No completion acknowledgement: the bounded operation fails stopped.
       The retained pending intent must no longer create an unrecoverable lock. */
    clock_now+=3.01; fresh(&c); LANE(&c)->heartbeat=clock_now;
    controller_tick(&c,clock_now);
    CHECK(LANE(&c)->op==OP_IDLE && pending(&c) && strstr(LANE(&c)->message,"Control action timed out") && stops==1);
    clock_now+=.5; fresh(&c); fixture_still(d,clock_now-1);
    action(&c,request(&c),202); CHECK(resets==initial_resets+2 && LANE(&c)->request_id==first_request+1 && pending(&c));
    action(&c,request(&c),409);
    /* A transient stale teach flag without an actual hand-guiding status
       cannot silently abandon this retry before reset acknowledgement. */
    clock_now+=.1; d->feedback.arm[0].arm_status=0; fresh(&c); LANE(&c)->heartbeat=clock_now;
    controller_tick(&c,clock_now); CHECK(LANE(&c)->op==OP_RESET && pending(&c));
    d->feedback.arm[0].teach=0;
    clock_now+=.5; fresh(&c); LANE(&c)->heartbeat=clock_now; controller_tick(&c,clock_now);
    CHECK(LANE(&c)->op==OP_IDLE && !pending(&c) && !strcmp(LANE(&c)->phase,"complete"));
    CHECK(resets==initial_resets+2 && stops==1);
    close_fixture(&c,directory);

    /* Each rejection begins with a real timed-out reset and its saved pending
       intent. Invalid retries must not emit any packet or clear that intent. */
    for (unsigned failure=0;failure<14;++failure) {
        fixture(&c,directory); d=&c.bus.devices[0];
        action(&c,request(&c),202);
        clock_now+=3.01; fresh(&c); LANE(&c)->heartbeat=clock_now; controller_tick(&c,clock_now);
        CHECK(LANE(&c)->op==OP_IDLE && pending(&c));
        clock_now+=.5; fresh(&c); fixture_still(d,clock_now-1);
        cJSON *body=request(&c); int expected=409;
        if (failure==0) { cJSON_DeleteItemFromObject(body,"supported"); expected=400; }
        if (failure==1) { cJSON_DeleteItemFromObject(body,"clear"); expected=400; }
        if (failure==2) d->feedback.arm[0].status_at=clock_now-1;
        if (failure==3) d->feedback.arm[0].joint_at[4]=clock_now-1;
        if (failure==4) d->feedback.motor[4].at=clock_now-1;
        if (failure==5) d->feedback.motor[4].enabled=true;
        if (failure==6) fixture_still(d,clock_now-.1);
        if (failure==7) { d->feedback.arm[0].arm_status=11; d->feedback.arm[0].control=2; }
        if (failure==8) { d->feedback.arm[0].arm_status=12; d->feedback.arm[0].teach=3; d->feedback.arm[0].control=2; }
        if (failure==9) d->feedback.arm[0].error=1;
        if (failure==10) d->feedback.arm[0].arm_status=0; /* Verify completion instead of resending. */
        if (failure==11) d->feedback.arm[0].control=3;
        if (failure==12) d->feedback.arm[0].motion_mode=2;
        if (failure==13) d->feedback.motor[2].fault=1;
        unsigned before=resets+stops; action(&c,body,expected);
        CHECK(pending(&c) && LANE(&c)->op==OP_IDLE && resets+stops==before);
        close_fixture(&c,directory);
    }
    puts("Reset retry passed: explicit supported stopped/all-off retry after timeout, one-packet in-flight guard, residual teach transition, verified completion and unsafe/stale/missing-confirmation rejection.");
}

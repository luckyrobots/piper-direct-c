#define _GNU_SOURCE
#include "fixture.h"
#include "fake_bus.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static unsigned pose_batches,stop_packets;
static bool stale_high;
static double frozen[6];
static void mcu_receive(struct can_bus *bus,double now)
{
    struct can_device *d=&bus->devices[0]; struct piper_sample *s=&d->feedback.arm[0];
    s->status_seen=true; s->status_at=now;
    for (unsigned i=0;i<6;++i) {
        s->joint_seen[i]=true; s->joint_at[i]=now;
        struct piper_motor *m=&d->feedback.motor[i]; m->seen=m->high_seen=true; m->at=now;
        if (!stale_high||i!=5) m->high_at=now;
        m->position_raw=(int32_t)llround(s->deg[i]*1000*3.141592653589793/180);
    }
}
static bool mcu_send(struct can_bus *bus,struct can_device *d,unsigned generation,const struct can_frame *frames,size_t count)
{
    CHECK(bus->simulated&&d->generation==generation);
    if (count==1&&frames[0].can_id==0x150) { CHECK(frames[0].data[0]==1); ++stop_packets; ++d->tx; return true; }
    /* Every other transmitted byte must be the current frozen pose. No power,
       reset, zeroing, replay or saved destination is permitted by this flow. */
    CHECK(pose_batches<2); ++pose_batches;
    CHECK(count==(pose_batches==1?3:4));
    struct can_frame expected[4]; unsigned offset=pose_batches==1?0:1;
    if (offset) expected[0]=piper_mode(1);
    CHECK(piper_targets(frozen,expected+offset));
    for (unsigned i=0;i<count;++i) {
        CHECK(frames[i].can_id==expected[i].can_id&&frames[i].len==8&&!memcmp(frames[i].data,expected[i].data,8));
        if (frames[i].can_id==0x151) d->feedback.arm[0].control=1;
        ++d->tx;
    }
    return true;
}
static cJSON *record(struct controller *c)
{
    return cJSON_GetObjectItem(cJSON_GetObjectItem(c->saved,"devices"),c->bus.devices[0].id);
}
static bool blocked(struct controller *c) { return cJSON_IsTrue(cJSON_GetObjectItem(record(c),"blocked")); }
static void tick(struct controller *c,double dt) { fake_bus.now+=dt; LANE(c)->heartbeat=fake_bus.now; controller_tick(c,fake_bus.now); }
static void fixture(struct controller *c,char directory[96])
{
    fixture_directory(directory,96,"piper-recover-at-pose"); fake_bus.now+=10;
    CHECK(controller_init(c,true,directory,"")); struct can_device *d=&c->bus.devices[0];
    stale_high=false; pose_batches=stop_packets=0;
    d->simulated=false; d->fd=FIXTURE_FD; fixture_still(d,fake_bus.now-1);
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.9-0");
    d->feedback.arm[0].control=1; d->feedback.arm[0].motion_mode=1;
    const double pose[6]={2,25,-25,-20,60,8}; memcpy(frozen,pose,sizeof frozen); memcpy(d->feedback.arm[0].deg,pose,sizeof pose);
    fixture_vendor_limits(d,.3,fake_bus.now);
    for (unsigned i=0;i<6;++i) d->feedback.motor[i].enabled=true;
    can_bus_receive(&c->bus,fake_bus.now);
    LANE(c)->obstruction_joint=1;
    controller_stop_obstruction(c,"Possible obstruction at J1: no measured progress for 0.5 seconds.");
    CHECK(blocked(c)&&stop_packets==1); stop_packets=0;
}
static void physical_cycle(struct controller *c)
{
    struct piper_sample *s=&c->bus.devices[0].feedback.arm[0];
    s->control=2; s->teach=1; s->arm_status=11; tick(c,.05);
    CHECK(LANE(c)->teaching_cycle==TEACH_CYCLE_SEEN);
    s->teach=2; s->arm_status=0; tick(c,.05);
    CHECK(LANE(c)->teaching_cycle==TEACH_CYCLE_COMPLETED);
}
static void action(struct controller *c,const char *name,int expected)
{
    struct can_device *d=&c->bus.devices[0]; cJSON *j=cJSON_CreateObject();
    cJSON_AddStringToObject(j,"device_id",d->id); cJSON_AddNumberToObject(j,"generation",d->generation);
    cJSON_AddStringToObject(j,"owner","recover-at-pose-test-client"); cJSON_AddStringToObject(j,"action",name);
    cJSON_AddBoolToObject(j,"standalone",true); cJSON_AddBoolToObject(j,"supported",true); cJSON_AddBoolToObject(j,"clear",true);
    cJSON_AddBoolToObject(j,"button_off",true); cJSON_AddBoolToObject(j,"same_pose_confirmed",true);
    uint64_t before=d->tx; int status; cJSON *r=controller_action(c,"/api/command",j,&status);
    if (status!=expected) { char *text=cJSON_Print(r); fprintf(stderr,"%s expected %d, got %d: %s\n",name,expected,status,text); free(text); }
    CHECK(status==expected); if (expected>=400) CHECK(d->tx==before&&pose_batches==0&&stop_packets==0);
    cJSON_Delete(r); cJSON_Delete(j);
}
static void close_fixture(struct controller *c,const char *directory)
{
    CHECK(LANE(c)->op==OP_IDLE); c->bus.devices[0].fd=-1; fixture_cleanup(c,directory);
}
static void scenarios(void)
{
    fake_bus_model(mcu_receive,mcu_send);
    struct controller c; char directory[96]; fixture(&c,directory);
    c.bus.devices[0].feedback.arm[0].control=2; c.bus.devices[0].feedback.arm[0].teach=2;
    action(&c,"recover-at-pose",409); action(&c,"teach-return",409); /* Ended bit alone is not proof. */
    physical_cycle(&c);
    cJSON *state=controller_state(&c,"recovery-test"),*device=cJSON_GetArrayItem(cJSON_GetObjectItem(state,"devices"),0);
    CHECK(cJSON_IsTrue(cJSON_GetObjectItem(cJSON_GetObjectItem(device,"teaching"),"can_recover_at_pose"))); cJSON_Delete(state);
    action(&c,"recover-at-pose",202); CHECK(LANE(&c)->op==OP_TEACH_RETURN&&blocked(&c)&&pose_batches==1);
    CHECK(LANE(&c)->teaching_cycle!=TEACH_CYCLE_COMPLETED&&LANE(&c)->recovering_at_pose);
    for (unsigned i=0;i<6;++i) CHECK(LANE(&c)->target[i]==frozen[i]&&LANE(&c)->command[i]==frozen[i]);
    tick(&c,.025); CHECK(blocked(&c)&&pose_batches==2);
    tick(&c,.025); CHECK(LANE(&c)->op==OP_IDLE&&!blocked(&c)&&!LANE(&c)->recovering_at_pose&&stop_packets==0);
    CHECK(!cJSON_IsTrue(cJSON_GetObjectItem(record(&c),"stop_event_active"))); close_fixture(&c,directory);

    for (unsigned failure=0;failure<10;++failure) {
        fixture(&c,directory); physical_cycle(&c); struct can_device *d=&c.bus.devices[0];
        if (failure==0) ++d->generation;
        if (failure==1) d->feedback.motor[0].fault=1;
        if (failure==2) d->feedback.motor[1].high_at=fake_bus.now-1;
        if (failure==3) d->feedback.motor[2].enabled=false;
        if (failure==4) d->feedback.arm[0].deg[1]=-.2;
        if (failure==5) cJSON_ReplaceItemInObject(record(&c),"state",cJSON_CreateString("reset_pending"));
        if (failure==6) { fake_bus.now+=121; can_bus_receive(&c.bus,fake_bus.now); }
        if (failure==7) cJSON_ReplaceItemInObject(record(&c),"kind",cJSON_CreateString("stop"));
        if (failure==8) cJSON_AddItemToObject(record(&c),"last_fault",cJSON_CreateObject());
        if (failure==9) strcpy(d->feedback.version,"S-V1.8-2");
        action(&c,"recover-at-pose",409); CHECK(blocked(&c)); close_fixture(&c,directory);
    }
    /* B10: eligibility follows the persisted kind, so a record whose message
       was reworded by this build (reason and kind_reason together) still
       recovers. */
    fixture(&c,directory); physical_cycle(&c);
    cJSON_ReplaceItemInObject(record(&c),"reason",cJSON_CreateString("Reworded obstruction message."));
    cJSON_ReplaceItemInObject(record(&c),"kind_reason",cJSON_CreateString("Reworded obstruction message."));
    cJSON_ReplaceItemInObject(cJSON_GetObjectItem(record(&c),"stop_event"),"reason",cJSON_CreateString("Reworded."));
    CHECK(!strcmp(cJSON_GetStringValue(cJSON_GetObjectItem(cJSON_GetObjectItem(record(&c),"stop_event"),"kind")),"obstruction"));
    action(&c,"recover-at-pose",202); tick(&c,.025); tick(&c,.025); CHECK(LANE(&c)->op==OP_IDLE&&!blocked(&c)); close_fixture(&c,directory);
    /* Rollback: an older build re-latched a different stop and rewrote only
       the reason, leaving kind "obstruction". kind is derived again. */
    fixture(&c,directory); physical_cycle(&c);
    cJSON_ReplaceItemInObject(record(&c),"reason",cJSON_CreateString("Control heartbeat expired."));
    action(&c,"recover-at-pose",409); CHECK(blocked(&c));
    CHECK(!strcmp(cJSON_GetStringValue(cJSON_GetObjectItem(record(&c),"kind")),"stop")); close_fixture(&c,directory);
    /* Rollback: an older build latched (blocked:true) a record this build had
       cleared, leaving state "clear". The stricter reading wins. */
    fixture(&c,directory); physical_cycle(&c);
    cJSON_ReplaceItemInObject(record(&c),"state",cJSON_CreateString("clear"));
    cJSON_Delete(controller_state(&c,"rollback-test")); /* any controller read normalizes the record */
    CHECK(blocked(&c)&&!strcmp(cJSON_GetStringValue(cJSON_GetObjectItem(record(&c),"state")),"latched")); close_fixture(&c,directory);
    fixture(&c,directory); physical_cycle(&c);
    cJSON_ReplaceItemInObject(record(&c),"state",cJSON_CreateString("clear"));
    cJSON_ReplaceItemInObject(record(&c),"blocked",cJSON_CreateFalse());
    cJSON_ReplaceItemInObject(record(&c),"reset_pending",cJSON_CreateTrue());
    cJSON_Delete(controller_state(&c,"rollback-test"));
    CHECK(blocked(&c)&&!strcmp(cJSON_GetStringValue(cJSON_GetObjectItem(record(&c),"state")),"reset_pending")); close_fixture(&c,directory);
    /* C-C: active STOP evidence implies a latch. A record cleared by an older
       build that left stop_event_active set normalizes to latched, with kind
       derived again from its text (here none, so the stricter "stop"). */
    fixture(&c,directory); physical_cycle(&c);
    cJSON_ReplaceItemInObject(record(&c),"state",cJSON_CreateString("clear"));
    cJSON_ReplaceItemInObject(record(&c),"blocked",cJSON_CreateFalse());
    cJSON_ReplaceItemInObject(record(&c),"reset_pending",cJSON_CreateFalse());
    cJSON_ReplaceItemInObject(record(&c),"reason",cJSON_CreateString(""));
    CHECK(cJSON_IsTrue(cJSON_GetObjectItem(record(&c),"stop_event_active")));
    cJSON_Delete(controller_state(&c,"rollback-test"));
    CHECK(blocked(&c)&&!strcmp(cJSON_GetStringValue(cJSON_GetObjectItem(record(&c),"state")),"latched"));
    CHECK(!strcmp(cJSON_GetStringValue(cJSON_GetObjectItem(record(&c),"kind")),"stop"));
    action(&c,"recover-at-pose",409); CHECK(blocked(&c)); close_fixture(&c,directory);
    /* A record written before "kind" existed derives it once from its text. */
    fixture(&c,directory); physical_cycle(&c);
    cJSON_DeleteItemFromObject(record(&c),"kind"); cJSON_DeleteItemFromObject(record(&c),"state");
    cJSON_DeleteItemFromObject(cJSON_GetObjectItem(record(&c),"stop_event"),"kind");
    action(&c,"recover-at-pose",202); tick(&c,.025); tick(&c,.025); CHECK(LANE(&c)->op==OP_IDLE&&!blocked(&c)); close_fixture(&c,directory);
    fixture(&c,directory); physical_cycle(&c);
    cJSON_DeleteItemFromObject(record(&c),"kind"); cJSON_DeleteItemFromObject(record(&c),"state");
    cJSON_ReplaceItemInObject(record(&c),"reason",cJSON_CreateString("Operator stop."));
    action(&c,"recover-at-pose",409); close_fixture(&c,directory);
    /* New STOP and an intervening fault destroy previously observed proof. */
    fixture(&c,directory); physical_cycle(&c); c.bus.devices[0].feedback.motor[0].fault=1; tick(&c,.01);
    c.bus.devices[0].feedback.motor[0].fault=0; action(&c,"recover-at-pose",409); close_fixture(&c,directory);
    fixture(&c,directory); physical_cycle(&c); c.bus.devices[0].feedback.arm[0].control=1;
    controller_stop(&c,"Possible obstruction at J1: second incident."); stop_packets=0;
    c.bus.devices[0].feedback.arm[0].control=2; action(&c,"recover-at-pose",409); close_fixture(&c,directory);

    fixture(&c,directory); physical_cycle(&c); action(&c,"recover-at-pose",202); tick(&c,.025);
    stale_high=true; tick(&c,.4); CHECK(LANE(&c)->op==OP_IDLE&&blocked(&c)&&stop_packets==0); close_fixture(&c,directory);
    /* Persist failure after the physical ACK must retain the obstruction,
       while leaving the verified current hold intact instead of dropping it. */
    fixture(&c,directory); physical_cycle(&c); action(&c,"recover-at-pose",202); tick(&c,.025);
    strcpy(c.state_dir,"/this-directory-does-not-exist/piper"); tick(&c,.025);
    CHECK(LANE(&c)->op==OP_IDLE&&blocked(&c)&&c.storage_failed&&stop_packets==0&&!strcmp(LANE(&c)->phase,"blocked"));
    strcpy(c.state_dir,directory); close_fixture(&c,directory);
    puts("Recover-at-pose passed: observed physical cycle, frozen-pose-only 3+4 handoff, ACK-before-clear, no power/reset/home/replay, identity/freshness/fault/range/incident/storage rejection.");
}
static const struct check_test tests[]={{"recover-at-pose","every scenario of this file",scenarios,NULL}};
int main(void) { return check_main("recover-at-pose",tests,1); }

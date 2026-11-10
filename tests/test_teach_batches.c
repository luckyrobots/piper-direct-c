#define _GNU_SOURCE
#include "fixture.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static double clock_now=100,batch_at[2],frozen[6];
static unsigned batches,stops,fail_batch,fail_frame;
static int stale_joint=-1,stale_high=-1;
static double stale_at;
FIXTURE_WRAP_PIPER_NOW(clock_now)
void __wrap_can_bus_receive(struct can_bus *bus,double now)
{
    struct can_device *d=&bus->devices[0]; struct piper_sample *s=&d->feedback.arm[0];
    s->status_at=now; s->status_seen=true;
    for (unsigned i=0;i<6;++i) {
        s->joint_seen[i]=true; s->joint_at[i]=(int)i==stale_joint?stale_at:now;
        d->feedback.motor[i].seen=d->feedback.motor[i].high_seen=true;
        d->feedback.motor[i].at=now; d->feedback.motor[i].high_at=(int)i==stale_high?stale_at:now;
        d->feedback.motor[i].position_raw=(int32_t)llround(s->deg[i]*1000*3.141592653589793/180);
    }
}
bool __wrap_can_bus_send(struct can_bus *bus,struct can_device *d,unsigned generation,const struct can_frame *frames,size_t count)
{
    CHECK(bus->simulated&&generation==d->generation);
    if (count==1&&frames[0].can_id==0x150) {
        CHECK(frames[0].data[0]==1); ++stops;
        /* A later failed STOP must not replace the original persisted cause. */
        strcpy(d->problem,"Later STOP transfer failed"); can_bus_record_tx_failure(d,generation,1,1,0x150,31,14);
        d->tx_failure.at=clock_now;
        return false;
    }
    CHECK(batches<2); batch_at[batches]=clock_now; ++batches;
    CHECK(count==(batches==1?3:4)&&d->feedback.arm[0].control==2);
    struct can_frame expected[4]; unsigned offset=batches==1?0:1;
    if (offset) { CHECK(batch_at[1]-batch_at[0]>=.02); expected[0]=piper_mode(1); }
    CHECK(piper_targets(frozen,expected+offset));
    for (unsigned i=0;i<count;++i) {
        CHECK(frames[i].can_id==expected[i].can_id&&frames[i].len==8&&!memcmp(frames[i].data,expected[i].data,8));
        if (batches==fail_batch&&i+1==fail_frame) {
            strcpy(d->problem,"Windows USB-CAN send failed (transport 14, WinUSB 121)");
            can_bus_record_tx_failure(d,generation,i+1,(unsigned)count,frames[i].can_id,121,14);
            /* piper_now in the recorder shares its translation unit and is not linker-wrapped. */
            d->tx_failure.at=clock_now;
            return false;
        }
        if (frames[i].can_id==0x151) d->feedback.arm[0].control=1;
        ++d->tx;
    }
    return true;
}
static void fixture(struct controller *c,char directory[96])
{
    fixture_directory(directory,96,"piper-teach-batches");
    clock_now+=10; CHECK(controller_init(c,true,directory,""));
    batches=stops=fail_batch=fail_frame=0; stale_joint=stale_high=-1;
    struct can_device *d=&c->bus.devices[0];
    /* Exercise the real encoder/freshness guards on a wholly mocked transport. */
    d->simulated=false; d->fd=FIXTURE_FD; fixture_still(d,clock_now-1);
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.9-0");
    d->feedback.arm[0].control=2; d->feedback.arm[0].teach=2; d->feedback.arm[0].motion_mode=1;
    const double pose[6]={2,25,-25,-20,60,8}; memcpy(frozen,pose,sizeof frozen); memcpy(d->feedback.arm[0].deg,pose,sizeof pose);
    fixture_vendor_limits(d,.3,clock_now);
    for (unsigned i=0;i<6;++i) d->feedback.motor[i].enabled=true;
    d->rx=123; d->local=45; __wrap_can_bus_receive(&c->bus,clock_now);
}
static void start(struct controller *c,int expected)
{
    struct can_device *d=&c->bus.devices[0]; cJSON *j=cJSON_CreateObject();
    cJSON_AddStringToObject(j,"device_id",d->id); cJSON_AddNumberToObject(j,"generation",d->generation);
    cJSON_AddStringToObject(j,"owner","teach-batches-regression-client"); cJSON_AddStringToObject(j,"action","teach-return");
    cJSON_AddBoolToObject(j,"standalone",true); cJSON_AddBoolToObject(j,"supported",true); cJSON_AddBoolToObject(j,"clear",true);
    cJSON_AddBoolToObject(j,"button_off",true); cJSON_AddBoolToObject(j,"same_pose_confirmed",true);
    int status; cJSON *r=controller_action(c,"/api/command",j,&status); CHECK(status==expected); cJSON_Delete(r); cJSON_Delete(j);
}
static void tick(struct controller *c,double dt) { clock_now+=dt; LANE(c)->heartbeat=clock_now; controller_tick(c,clock_now); }
static void close_fixture(struct controller *c,const char *directory)
{
    CHECK(LANE(c)->op==OP_IDLE); c->bus.devices[0].fd=-1; fixture_cleanup(c,directory);
}
static void check_persisted_failure(struct controller *c,const char *directory,unsigned count,unsigned index,unsigned id)
{
    struct can_device *d=&c->bus.devices[0]; ++d->generation; d->problem[0]=0;
    char path[128]; snprintf(path,sizeof path,"%s/control-state.json",directory);
    cJSON *saved=fixture_read_json(path),*device=cJSON_GetObjectItem(cJSON_GetObjectItem(saved,"devices"),d->id);
    cJSON *transport=cJSON_GetObjectItem(cJSON_GetObjectItem(device,"stop_event"),"transport");
    CHECK(cJSON_IsObject(transport));
    CHECK(cJSON_GetNumberValue(cJSON_GetObjectItem(transport,"failed_frame_index"))==index);
    CHECK(cJSON_GetNumberValue(cJSON_GetObjectItem(transport,"batch_frames"))==count);
    CHECK(cJSON_GetNumberValue(cJSON_GetObjectItem(transport,"failed_can_id"))==id);
    CHECK(cJSON_GetNumberValue(cJSON_GetObjectItem(transport,"os_error"))==121&&cJSON_GetNumberValue(cJSON_GetObjectItem(transport,"transport_error"))==14);
    CHECK(cJSON_GetNumberValue(cJSON_GetObjectItem(transport,"failed_generation"))==d->generation-1);
    CHECK(cJSON_GetNumberValue(cJSON_GetObjectItem(transport,"rx_at_failure"))==123&&cJSON_GetNumberValue(cJSON_GetObjectItem(transport,"local_at_failure"))==45);
    const char *problem=cJSON_GetStringValue(cJSON_GetObjectItem(transport,"failure"));
    CHECK(problem&&strstr(problem,"WinUSB 121")); cJSON_Delete(saved);
}
int main(void)
{
    struct controller c; char directory[96]; fixture(&c,directory); start(&c,202);
    CHECK(batches==1&&LANE(&c)->stage==STAGE_TEACH_SEEDED&&c.bus.devices[0].feedback.arm[0].control==2);
    tick(&c,.01); CHECK(batches==1); tick(&c,.015); CHECK(batches==2&&LANE(&c)->stage==STAGE_TEACH_MODE&&LANE(&c)->op==OP_TEACH_RETURN);
    stale_at=LANE(&c)->sent_at; stale_joint=3; tick(&c,.01); CHECK(LANE(&c)->op==OP_TEACH_RETURN);
    stale_joint=-1; stale_high=4; tick(&c,.01); CHECK(LANE(&c)->op==OP_TEACH_RETURN);
    stale_high=-1; tick(&c,.01); CHECK(LANE(&c)->op==OP_IDLE&&!strcmp(LANE(&c)->phase,"complete")&&stops==0);
    close_fixture(&c,directory);

    for (unsigned failure=0;failure<6;++failure) {
        fixture(&c,directory); start(&c,202); struct can_device *d=&c.bus.devices[0];
        if (failure==0) {
            cJSON *j=cJSON_CreateObject(); int status; cJSON *r=controller_action(&c,"/api/stop",j,&status);
            /* A global request still attempts every STOP, but cannot report
               success when this fixture's physical transfer failed. */
            CHECK(status==409&&!cJSON_IsTrue(cJSON_GetObjectItem(r,"ok")));
            CHECK(cJSON_GetArraySize(cJSON_GetObjectItem(r,"stops"))==1);
            cJSON_Delete(r); cJSON_Delete(j);
        }
        if (failure==1) ++d->generation;
        if (failure==2) { d->feedback.arm[0].teach=1; d->feedback.arm[0].arm_status=11; }
        if (failure==3) d->feedback.arm[0].deg[5]+=.501;
        if (failure==4) d->feedback.motor[3].enabled=false;
        if (failure==5) { clock_now+=1; LANE(&c)->heartbeat=clock_now-1; }
        if (failure==5) controller_tick(&c,clock_now); else tick(&c,.025);
        CHECK(LANE(&c)->op==OP_IDLE&&batches==1); close_fixture(&c,directory);
    }
    fixture(&c,directory); start(&c,202); tick(&c,.025); c.bus.devices[0].feedback.arm[0].deg[5]+=.501; tick(&c,.01);
    CHECK(LANE(&c)->op==OP_IDLE&&batches==2&&stops==1&&LANE(&c)->obstruction_joint==6); close_fixture(&c,directory);

    fixture(&c,directory); fail_batch=1; fail_frame=2; start(&c,409);
    CHECK(LANE(&c)->op==OP_IDLE&&batches==1&&stops==1); check_persisted_failure(&c,directory,3,2,0x156); close_fixture(&c,directory);
    fixture(&c,directory); start(&c,202); fail_batch=2; fail_frame=4; tick(&c,.025);
    CHECK(LANE(&c)->op==OP_IDLE&&batches==2&&stops==1); check_persisted_failure(&c,directory,4,4,0x157); close_fixture(&c,directory);
    puts("Teach batches passed: frozen 3+4 sequence with receive gap, final fresh ACKs, pre-mode cancellation, drift and partial-transfer STOP with persistent original transport evidence.");
}

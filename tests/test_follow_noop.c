#define _GNU_SOURCE
#include "fixture.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
static double clock_now=100;
static unsigned full_poses,stops,sequence;
static bool fail_pose;
double __wrap_piper_now(void) { return clock_now; }
void __wrap_can_bus_receive(struct can_bus *b,double now)
{
    struct can_device *d=&b->devices[0]; struct piper_sample *s=&d->feedback.arm[0];
    s->status_seen=true; s->status_at=now;
    for (unsigned i=0;i<6;++i) {
        s->joint_seen[i]=true; s->joint_at[i]=now;
        d->feedback.motor[i].seen=d->feedback.motor[i].high_seen=d->feedback.motor[i].enabled=true;
        d->feedback.motor[i].at=d->feedback.motor[i].high_at=now;
    }
}
bool __wrap_can_bus_send(struct can_bus *b,struct can_device *d,unsigned generation,const struct can_frame *f,size_t count)
{
    CHECK(b->simulated&&generation==d->generation);
    if (count==1) { CHECK(f->can_id==0x150&&f->data[0]==1); ++stops; return true; }
    CHECK(count==4&&f[0].can_id==0x151&&f[1].can_id==0x155&&f[2].can_id==0x156&&f[3].can_id==0x157);
    if (fail_pose) {
        strcpy(d->problem,"Windows USB-CAN send failed (transport 14, WinUSB 121)");
        can_bus_record_tx_failure(d,generation,2,4,0x155,121,14); return false;
    }
    ++full_poses; d->tx+=4; d->feedback.arm[0].control=1; return true;
}
static void command(struct controller *c,const char *action,double wrist,int expected)
{
    struct can_device *d=&c->bus.devices[0]; cJSON *j=cJSON_CreateObject();
    cJSON_AddStringToObject(j,"device_id",d->id); cJSON_AddNumberToObject(j,"generation",d->generation);
    cJSON_AddStringToObject(j,"owner","follow-noop-regression-client"); cJSON_AddStringToObject(j,"action",action);
    cJSON_AddNumberToObject(j,"request_id",LANE(c)->request_id); cJSON_AddNumberToObject(j,"sequence",++sequence);
    cJSON_AddNumberToObject(j,"speed_percent",100); cJSON_AddBoolToObject(j,"standalone",true); cJSON_AddBoolToObject(j,"clear",true);
    double pose[6]={0,0,0,0,0,wrist}; cJSON_AddItemToObject(j,"degrees",cJSON_CreateDoubleArray(pose,6));
    int status; cJSON *reply=controller_action(c,"/api/command",j,&status); CHECK(status==expected);
    cJSON_Delete(j); cJSON_Delete(reply);
}
static void fixture(struct controller *c,char directory[96])
{
    strcpy(directory,"/tmp/piper-follow-noop-XXXXXX"); CHECK(mkdtemp(directory)); clock_now+=10;
    CHECK(controller_init(c,true,directory,"")); full_poses=stops=sequence=0; fail_pose=false;
    struct can_device *d=&c->bus.devices[0]; fixture_still(d,clock_now-1); d->feedback.arm[0].motion_mode=1;
    memset(d->feedback.arm[0].deg,0,sizeof d->feedback.arm[0].deg);
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.8-2");
    for (unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=-180,.max=180,.speed=.3,.at=clock_now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=clock_now};
    }
    __wrap_can_bus_receive(&c->bus,clock_now); command(c,"follow-start",0,202);
}
static void tick(struct controller *c) { clock_now+=.025; LANE(c)->heartbeat=clock_now; controller_tick(c,clock_now); }
static void close_fixture(struct controller *c,const char *directory)
{
    CHECK(LANE(c)->op==OP_IDLE); controller_close(c); char path[128]; snprintf(path,sizeof path,"%s/control-state.json",directory); CHECK(!unlink(path)); CHECK(!rmdir(directory));
}
int main(void)
{
    struct controller c; char directory[96]; fixture(&c,directory); tick(&c); CHECK(full_poses==1);
    double sent=LANE(&c)->sent_at,wire=LANE(&c)->pose_tx_at;
    for (unsigned n=0;n<25;++n) { command(&c,"follow-update",0,202); tick(&c); }
    CHECK(full_poses==1&&LANE(&c)->sent_at==sent&&LANE(&c)->pose_tx_at==wire&&LANE(&c)->op==OP_MOVE&&LANE(&c)->follow==FOLLOW_LIVE);
    command(&c,"follow-commit",0,202); tick(&c);
    CHECK(LANE(&c)->final_sent_at==wire&&LANE(&c)->pose_tx_at==wire&&full_poses==1);
    for (unsigned n=0;n<30&&LANE(&c)->op!=OP_IDLE;++n) tick(&c);
    CHECK(LANE(&c)->op==OP_IDLE&&!stops&&full_poses==1); close_fixture(&c,directory);

    fixture(&c,directory); tick(&c); command(&c,"follow-update",.003,202); tick(&c); CHECK(full_poses==2);
    LANE(&c)->speed_percent=50; tick(&c); CHECK(full_poses==3); /* Different mode tuple is never suppressed. */
    c.bus.devices[0].feedback.arm[0].control=0; tick(&c); CHECK(full_poses==4);
    command(&c,"follow-end",0,200); CHECK(LANE(&c)->op==OP_IDLE); unsigned previous=full_poses;
    command(&c,"follow-start",0,202); tick(&c); CHECK(full_poses==previous+1); /* New session always starts with a real write. */
    command(&c,"follow-end",0,200); close_fixture(&c,directory);

    for (unsigned expiry=0;expiry<2;++expiry) {
        fixture(&c,directory); tick(&c); struct can_device *d=&c.bus.devices[0];
        d->feedback.arm[0].deg[5]=.02; fail_pose=true;
        if (expiry) { clock_now=LANE(&c)->follow_updated+.251; LANE(&c)->heartbeat=clock_now; controller_tick(&c,clock_now); }
        else command(&c,"follow-end",0,409);
        CHECK(LANE(&c)->op==OP_IDLE&&stops==1&&!LANE(&c)->pose_tx_valid);
        cJSON *record=cJSON_GetObjectItem(cJSON_GetObjectItem(c.saved,"devices"),d->id),*event=cJSON_GetObjectItem(record,"stop_event");
        const char *reason=cJSON_GetObjectItem(event,"reason")->valuestring;
        CHECK(strstr(reason,expiry?"Following input expired after 250 ms":"Movement stopped. Holding measured pose"));
        CHECK(strstr(reason,"WinUSB 121")&&cJSON_GetObjectItem(event,"follow_input_age_s"));
        close_fixture(&c,directory);
    }
    puts("Following no-op passed: unchanged full tuple suppresses only redundant writes, actual TX timestamps persist, new targets/speed/mode/session transmit and failed hold preserves its first trigger.");
}

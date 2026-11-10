#define _GNU_SOURCE
/* Offline firmware accepts one power request before a receive/ACK tick and
   ignores a burst's later requests. Recovery must use individual OFF requests,
   never a reset, enable, mode, gripper or position packet. */
#include "fixture.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#include <windows.h>
#include <direct.h>
#define remove_directory _rmdir
#else
#include <unistd.h>
#define remove_directory rmdir
#endif

static double clock_now=100;
static const char *owner="disable-sequence-test";
struct fake_mcu {
    unsigned count[6],stops,blocked_mask,freeze_low_mask;
    double first[6],last[6],delay[6],last_request,ready;
    int pending;
};
static struct fake_mcu mcu[2];
static int failed_device=-1,failed_joint=-1;
FIXTURE_WRAP_PIPER_NOW(clock_now)
static struct controller_lane *lane(struct controller *c,unsigned n)
{
    for(unsigned i=0;i<CANBUS_MAX;++i)if(!strcmp(c->lanes[i].lane_device,c->bus.devices[n].id))return &c->lanes[i];
    CHECK(!"Missing lane");return NULL;
}
static bool firmware_accept(struct fake_mcu *m,unsigned joint,double now)
{
    if(m->pending>=0||(m->blocked_mask&(1u<<joint)))return false;
    m->pending=(int)joint;m->ready=now+m->delay[joint];return true;
}
void __wrap_can_bus_receive(struct can_bus *bus,double now)
{
    for(unsigned n=0;n<2;++n) {
        struct can_device *d=&bus->devices[n];struct piper_sample *s=&d->feedback.arm[0];struct fake_mcu *m=&mcu[n];
        if(m->pending>=0&&now>=m->ready) { d->feedback.motor[m->pending].enabled=false;m->pending=-1; }
        s->status_seen=true;s->status_at=now;
        for(unsigned i=0;i<6;++i) {
            s->joint_seen[i]=true;s->joint_at[i]=now;
            struct piper_motor *motor=&d->feedback.motor[i];motor->seen=motor->high_seen=true;
            motor->at=(m->freeze_low_mask&(1u<<i))&&m->first[i]?m->first[i]:now;motor->high_at=now;
        }
    }
}
bool __wrap_can_bus_send(struct can_bus *bus,struct can_device *d,unsigned generation,const struct can_frame *frames,size_t count)
{
    CHECK(bus->simulated&&d>=bus->devices&&d<bus->devices+2);
    if(generation!=d->generation)return false;
    unsigned n=(unsigned)(d-bus->devices);struct fake_mcu *m=&mcu[n];
    if(count==1&&frames[0].can_id==0x150) {
        CHECK(frames[0].data[0]==1);for(unsigned i=1;i<8;++i)CHECK(!frames[0].data[i]);
        ++m->stops;++d->tx;return true;
    }
    for(unsigned f=0;f<count;++f) {
        CHECK(frames[f].can_id==0x471&&frames[f].len==8&&frames[f].data[0]>=1&&frames[f].data[0]<=6&&frames[f].data[1]==1);
        for(unsigned i=2;i<8;++i)CHECK(!frames[f].data[i]);
        unsigned joint=frames[f].data[0]-1;
        if(count==1&&m->last_request)CHECK(clock_now-m->last_request>=.02-1e-9);
        if(m->count[joint])CHECK(clock_now-m->last[joint]>=.15-1e-9);
        else m->first[joint]=clock_now;
        ++m->count[joint];m->last[joint]=m->last_request=clock_now;
        if(failed_device==(int)n&&failed_joint==(int)joint)return false;
        (void)firmware_accept(m,joint,clock_now);++d->tx;
    }
    return true;
}
static cJSON *identity(struct controller *c,unsigned n)
{
    struct can_device *d=&c->bus.devices[n];cJSON *body=cJSON_CreateObject();
    cJSON_AddStringToObject(body,"device_id",d->id);cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddStringToObject(body,"owner",owner);cJSON_AddBoolToObject(body,"standalone",true);
    cJSON_AddBoolToObject(body,"supported",true);cJSON_AddBoolToObject(body,"clear",true);return body;
}
static void request(struct controller *c,const char *route,cJSON *body,int expected)
{
    int status;cJSON *result=controller_action(c,route,body,&status);
    if(status!=expected){char *text=cJSON_PrintUnformatted(result);fprintf(stderr,"%s expected%d got%d %s\n",route,expected,status,text);free(text);}
    CHECK(status==expected);cJSON_Delete(body);cJSON_Delete(result);
}
static void start(struct controller *c,unsigned n)
{
    cJSON *body=identity(c,n);cJSON_AddStringToObject(body,"action","disable");
    unsigned before=0;for(unsigned i=0;i<6;++i)before+=mcu[n].count[i];
    request(c,"/api/command",body,202);CHECK(lane(c,n)->op==OP_DISABLE);
    unsigned after=0;for(unsigned i=0;i<6;++i)after+=mcu[n].count[i];CHECK(after==before);
}
static void heartbeat(struct controller *c,unsigned n)
{
    if(lane(c,n)->op==OP_IDLE)return;
    cJSON *body=identity(c,n);cJSON_AddNumberToObject(body,"request_id",lane(c,n)->request_id);request(c,"/api/heartbeat",body,200);
}
static void tick(struct controller *c,bool first,bool second)
{
    clock_now+=.025;if(first)heartbeat(c,0);if(second)heartbeat(c,1);controller_tick(c,clock_now);
}
static cJSON *saved(struct controller *c,unsigned n)
{
    return cJSON_GetObjectItemCaseSensitive(cJSON_GetObjectItemCaseSensitive(c->saved,"devices"),c->bus.devices[n].id);
}
static void fixture(struct controller *c,char *directory,size_t capacity,unsigned scenario,bool incident)
{
#ifdef _WIN32
    char temp[MAX_PATH];CHECK(GetTempPathA(sizeof temp,temp)>0);
    snprintf(directory,capacity,"%spiper-disable-sequence-%lu-%u",temp,(unsigned long)GetCurrentProcessId(),scenario);
#else
    snprintf(directory,capacity,"/tmp/piper-disable-sequence-%ld-%u",(long)getpid(),scenario);
#endif
    clock_now+=10;CHECK(controller_init_simulated(c,2,directory,""));memset(mcu,0,sizeof mcu);failed_device=failed_joint=-1;
    for(unsigned n=0;n<2;++n) {
        struct can_device *d=&c->bus.devices[n];mcu[n].pending=-1;fixture_still(d,clock_now-1);
        strcpy(d->feedback.hardware,"H-V1.2-1");strcpy(d->feedback.version,"S-V1.9-0");
        d->feedback.arm[0].control=d->feedback.arm[0].motion_mode=1;
        for(unsigned i=0;i<6;++i){d->feedback.motor[i].enabled=true;mcu[n].delay[i]=.025;}
    }
    __wrap_can_bus_receive(&c->bus,clock_now);
    if(incident) {
        cJSON *r=cJSON_CreateObject(),*event=cJSON_CreateObject();
        cJSON_AddBoolToObject(r,"blocked",true);cJSON_AddBoolToObject(r,"stop_event_active",true);
        cJSON_AddStringToObject(r,"reason","Original range-entry incident");
        cJSON_AddStringToObject(event,"reason","Original range-entry incident");cJSON_AddNumberToObject(event,"unix_time",12345);
        cJSON_AddNumberToObject(event,"joint",5);cJSON_AddItemToObject(r,"stop_event",event);
        cJSON *records=cJSON_GetObjectItemCaseSensitive(c->saved,"devices");
        if(cJSON_HasObjectItem(records,c->bus.devices[0].id))cJSON_ReplaceItemInObjectCaseSensitive(records,c->bus.devices[0].id,r);
        else cJSON_AddItemToObject(records,c->bus.devices[0].id,r);
    }
    /* Publish/create both per-device lanes before tests access their state. */
    cJSON *snapshot=controller_state(c,"offline-fixture");cJSON_Delete(snapshot);
}
static void complete(struct controller *c)
{
    for(unsigned i=0;i<360&&controller_any_motion_active(c);++i)tick(c,true,true);
    CHECK(!controller_any_motion_active(c));
}
static void cleanup(struct controller *c,const char *directory)
{
    controller_close(c);char path[600];snprintf(path,sizeof path,"%s/control-state.json",directory);
    CHECK(!remove(path));CHECK(!remove_directory(directory));
}
int main(void)
{
    struct fake_mcu burst={.pending=-1};unsigned accepted=0;
    for(unsigned i=0;i<6;++i)if(firmware_accept(&burst,i,clock_now))++accepted;
    CHECK(accepted==1&&burst.pending==0); /* The old six-frame burst loses J2–J6. */
    struct controller *c=calloc(1,sizeof *c);CHECK(c);char directory[512];
    fixture(c,directory,sizeof directory,1,false);mcu[0].delay[0]=.5;start(c,0);start(c,1);
    cJSON *select=cJSON_CreateObject();cJSON_AddStringToObject(select,"device_id",c->bus.devices[1].id);request(c,"/api/select",select,200);
    complete(c);
    for(unsigned n=0;n<2;++n)for(unsigned i=0;i<6;++i)CHECK(!c->bus.devices[n].feedback.motor[i].enabled&&lane(c,n)->power_acknowledged[i]&&mcu[n].count[i]>=1);
    CHECK(!mcu[0].stops&&!mcu[1].stops&&!strcmp(lane(c,0)->phase,"complete")&&!strcmp(lane(c,1)->phase,"complete"));
    for(unsigned n=0;n<2;++n){unsigned before[6];memcpy(before,mcu[n].count,sizeof before);for(unsigned t=0;t<8;++t)tick(c,true,true);CHECK(!memcmp(before,mcu[n].count,sizeof before));}
    cleanup(c,directory);

    fixture(c,directory,sizeof directory,2,true);cJSON *primary=cJSON_Duplicate(cJSON_GetObjectItemCaseSensitive(saved(c,0),"stop_event"),true);
    mcu[0].blocked_mask=1u<<4;start(c,0);start(c,1);complete(c);
    CHECK(strstr(lane(c,0)->message,"Disable timed out")&&!strcmp(lane(c,1)->phase,"complete")&&mcu[0].stops==1&&!mcu[1].stops);
    CHECK(cJSON_Compare(primary,cJSON_GetObjectItemCaseSensitive(saved(c,0),"stop_event"),true));
    CHECK(!strcmp(cJSON_GetObjectItemCaseSensitive(saved(c,0),"reason")->valuestring,"Original range-entry incident"));
    CHECK(strstr(cJSON_GetObjectItemCaseSensitive(saved(c,0),"last_cleanup_error")->valuestring,"Disable timed out"));
    cJSON_Delete(primary);cleanup(c,directory);

    fixture(c,directory,sizeof directory,3,true);failed_device=0;failed_joint=2;start(c,0);start(c,1);complete(c);
    CHECK(lane(c,0)->op==OP_IDLE&&strstr(lane(c,0)->message,"not fully transmitted")&&mcu[0].stops==1&&!mcu[1].stops&&!strcmp(lane(c,1)->phase,"complete"));
    CHECK(!strcmp(cJSON_GetObjectItemCaseSensitive(saved(c,0),"reason")->valuestring,"Original range-entry incident"));cleanup(c,directory);

    fixture(c,directory,sizeof directory,4,false);start(c,0);start(c,1);++c->bus.devices[0].generation;tick(c,false,true);complete(c);
    CHECK(strstr(lane(c,0)->message,"Adapter connection changed")&&!strcmp(lane(c,1)->phase,"complete"));
    for(unsigned i=0;i<6;++i)CHECK(!mcu[0].count[i]);
    cleanup(c,directory);

    fixture(c,directory,sizeof directory,5,false);mcu[0].delay[0]=2;start(c,0);start(c,1);
    cJSON *wrong=identity(c,0);cJSON_ReplaceItemInObjectCaseSensitive(wrong,"owner",cJSON_CreateString("wrong-owner"));
    cJSON_AddNumberToObject(wrong,"request_id",lane(c,0)->request_id);request(c,"/api/heartbeat",wrong,409);
    for(unsigned i=0;i<100&&controller_any_motion_active(c);++i)tick(c,false,true);
    CHECK(strstr(lane(c,0)->message,"heartbeat expired")&&mcu[0].stops==1&&!mcu[1].stops&&!strcmp(lane(c,1)->phase,"complete"));cleanup(c,directory);

    fixture(c,directory,sizeof directory,6,false);start(c,0);start(c,1);tick(c,true,true);
    request(c,"/api/stop",cJSON_CreateObject(),200);unsigned before[2][6];memcpy(before[0],mcu[0].count,sizeof before[0]);memcpy(before[1],mcu[1].count,sizeof before[1]);
    for(unsigned i=0;i<10;++i)tick(c,false,false);
    CHECK(mcu[0].stops==1&&mcu[1].stops==1&&!memcmp(before[0],mcu[0].count,sizeof before[0])&&!memcmp(before[1],mcu[1].count,sizeof before[1]));cleanup(c,directory);

    fixture(c,directory,sizeof directory,7,false);mcu[0].freeze_low_mask=1u<<5;start(c,0);
    for(unsigned i=0;i<10;++i)tick(c,true,false);
    CHECK(lane(c,0)->op==OP_DISABLE&&!lane(c,0)->power_acknowledged[5]);
    mcu[0].freeze_low_mask=0;complete(c);CHECK(!strcmp(lane(c,0)->phase,"complete")&&!mcu[0].stops);cleanup(c,directory);

    /* An operator STOP after an obstruction latches the stricter kind. Power
       cleanup, completed or failed, keeps the first evidence but must not turn
       the record back into a recoverable obstruction. */
    for(unsigned failing=0;failing<2;++failing) {
        fixture(c,directory,sizeof directory,8+failing,false);lane(c,0)->obstruction_joint=1;
        controller_stop_obstruction(c,"Possible obstruction at J1: no measured progress for 0.5 seconds.");
        CHECK(!strcmp(cJSON_GetObjectItemCaseSensitive(saved(c,0),"kind")->valuestring,"obstruction"));
        cJSON *operator_stop=cJSON_CreateObject();cJSON_AddStringToObject(operator_stop,"device_id",c->bus.devices[0].id);
        request(c,"/api/stop",operator_stop,200);
        CHECK(!strcmp(cJSON_GetObjectItemCaseSensitive(saved(c,0),"kind")->valuestring,"stop"));
        if(failing)mcu[0].blocked_mask=1u<<4;
        start(c,0);
        CHECK(!strcmp(cJSON_GetObjectItemCaseSensitive(saved(c,0),"kind")->valuestring,"stop"));
        complete(c);CHECK(failing?strstr(lane(c,0)->message,"Disable timed out")!=NULL:!strcmp(lane(c,0)->phase,"complete"));
        CHECK(!strcmp(cJSON_GetObjectItemCaseSensitive(saved(c,0),"kind")->valuestring,"stop"));
        cJSON *event=cJSON_GetObjectItemCaseSensitive(saved(c,0),"stop_event");
        CHECK(!strcmp(cJSON_GetObjectItemCaseSensitive(event,"kind")->valuestring,"obstruction")&&cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(saved(c,0),"stop_event_active")));
        /* The on-disk record a restart would load says the same. */
        char path[600];snprintf(path,sizeof path,"%s/control-state.json",directory);cJSON *disk=fixture_read_json(path);
        CHECK(!strcmp(cJSON_GetObjectItemCaseSensitive(cJSON_GetObjectItemCaseSensitive(cJSON_GetObjectItemCaseSensitive(disk,"devices"),c->bus.devices[0].id),"kind")->valuestring,"stop"));
        cJSON_Delete(disk);cleanup(c,directory);
    }
    free(c);puts("PASS staged disable: per-driver paced OFF/ACK and bounded missing-ACK retries, fresh all-off settlement, independent lanes, timeout/TX failure/lease/generation/STOP isolation and original incident preservation; no enable/reset/mode/pose packets.");
}

#define _GNU_SOURCE
/* Production controller and two independent native CAN simulators. All time
   and sensor-fault injection is deterministic; no USB/socket/hardware opens. */
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
static unsigned receives;
static int injected_fault=-1;
static int failed_stop=-1;
static const char *owner="two-lane-native-test";
double __wrap_piper_now(void) { return clock_now; }
void __real_can_bus_receive(struct can_bus *bus,double now);
void __wrap_can_bus_receive(struct can_bus *bus,double now)
{
    ++receives;__real_can_bus_receive(bus,now);
    if (injected_fault>=0) bus->devices[injected_fault].feedback.motor[0].fault=1;
}
bool __real_can_bus_send(struct can_bus *bus,struct can_device *device,unsigned generation,const struct can_frame *frames,size_t count);
bool __wrap_can_bus_send(struct can_bus *bus,struct can_device *device,unsigned generation,const struct can_frame *frames,size_t count)
{
    CHECK(bus->simulated);
    if (failed_stop>=0&&device==&bus->devices[failed_stop]&&frames[0].can_id==0x150) return false;
    bool sent=__real_can_bus_send(bus,device,generation,frames,count);
    /* The simulator's same-object piper_now calls cannot be linker-wrapped.
       Put its actual decoded configuration replies in the injected clock's
       domain; actuator targets and sensor decoding still run production code. */
    if (sent) for (size_t i=0;i<count;++i) {
        unsigned joint=frames[i].data[0];
        if (frames[i].can_id==0x472&&joint>=1&&joint<=6) {
            if (frames[i].data[1]==1) device->feedback.limit[joint-1].at=clock_now;
            if (frames[i].data[1]==2) device->feedback.acceleration[joint-1].at=clock_now;
        }
        if (frames[i].can_id==0x477) device->feedback.gripper_config_at=clock_now;
    }
    return sent;
}
static cJSON *field(const cJSON *json,const char *key) { return cJSON_GetObjectItemCaseSensitive(json,key); }
static struct controller_lane *lane(struct controller *c,unsigned index)
{
    for (unsigned i=0;i<CANBUS_MAX;++i) if (!strcmp(c->lanes[i].lane_device,c->bus.devices[index].id)) return &c->lanes[i];
    CHECK(!"Device lane is missing");return NULL;
}
static cJSON *identity(struct controller *c,unsigned index)
{
    struct can_device *device=&c->bus.devices[index];cJSON *body=cJSON_CreateObject();
    cJSON_AddStringToObject(body,"device_id",device->id);cJSON_AddNumberToObject(body,"generation",device->generation);
    cJSON_AddStringToObject(body,"owner",owner);cJSON_AddBoolToObject(body,"standalone",true);cJSON_AddBoolToObject(body,"clear",true);
    cJSON_AddBoolToObject(body,"supported",true);
    return body;
}
static cJSON *request(struct controller *c,const char *route,cJSON *body,int expected)
{
    int status=0;cJSON *result=controller_action(c,route,body,&status);
    if (status!=expected) { char *text=cJSON_PrintUnformatted(result);fprintf(stderr,"%s got %d expected %d: %s\n",route,status,expected,text);free(text); }
    CHECK(status==expected);cJSON_Delete(body);return result;
}
static void heartbeat(struct controller *c,unsigned index)
{
    struct controller_lane *state=lane(c,index);
    if (state->op==OP_IDLE) return;
    cJSON *body=identity(c,index);cJSON_AddNumberToObject(body,"request_id",state->request_id);
    cJSON_Delete(request(c,"/api/heartbeat",body,200));
}
static void tick(struct controller *c,bool renew_first,bool renew_second)
{
    clock_now+=.01;unsigned before=receives;controller_tick(c,clock_now);CHECK(receives==before+1);
    if (renew_first) heartbeat(c,0);
    if (renew_second) heartbeat(c,1);
}
static void settle(struct controller *c,unsigned ticks)
{
    for (unsigned i=0;i<ticks;++i) tick(c,true,true);
}
static unsigned command(struct controller *c,unsigned index,const char *action,double target)
{
    cJSON *body=identity(c,index);cJSON_AddStringToObject(body,"action",action);
    cJSON_AddNumberToObject(body,"speed_percent",20);cJSON_AddNumberToObject(body,"tolerance_deg",.2);
    if (!strcmp(action,"joint")) { cJSON_AddNumberToObject(body,"joint",6);cJSON_AddNumberToObject(body,"degrees",target); }
    if (!strcmp(action,"follow-start")) cJSON_AddBoolToObject(body,"trajectory",true);
    if (!strcmp(action,"gripper")) cJSON_AddNumberToObject(body,"width_mm",target);
    cJSON *result=request(c,"/api/command",body,202);unsigned id=(unsigned)field(result,"request_id")->valuedouble;cJSON_Delete(result);return id;
}
static void follow(struct controller *c,unsigned index,const char *action,unsigned sequence,double wrist,int expected)
{
    cJSON *body=identity(c,index);cJSON_AddStringToObject(body,"action",action);
    cJSON_AddNumberToObject(body,"request_id",lane(c,index)->request_id);cJSON_AddNumberToObject(body,"sequence",sequence);
    double degrees[6];memcpy(degrees,c->bus.devices[index].feedback.arm[0].deg,sizeof degrees);degrees[5]=wrist;
    cJSON_AddItemToObject(body,"degrees",cJSON_CreateDoubleArray(degrees,6));
    cJSON_Delete(request(c,"/api/command",body,expected));
}
static void setup(struct controller *c,const char *directory)
{
    injected_fault=failed_stop=-1;clock_now+=10;CHECK(controller_init_simulated(c,2,directory,""));
    CHECK(!strcmp(c->bus.devices[0].id,"simulation:one")&&!strcmp(c->bus.devices[1].id,"simulation:2"));
    for (unsigned i=0;i<60;++i) tick(c,false,false);
    for (unsigned i=0;i<2;++i) cJSON_Delete(request(c,"/api/query",identity(c,i),202));
    for (unsigned i=0;i<300&&controller_any_query_active(c);++i) tick(c,false,false);
    if (controller_any_query_active(c)) for (unsigned i=0;i<2;++i) fprintf(stderr,"query %u active %d joint %u start %.3f next %.3f now %.3f gen %u/%u op %d firmware %s hardware %s limit %d at %.3f speed%.3f acc%d at %.3f since%.3f\n",i,lane(c,i)->query,lane(c,i)->query_joint,lane(c,i)->query_start,lane(c,i)->query_next,clock_now,lane(c,i)->query_generation,c->bus.devices[i].generation,lane(c,i)->op,c->bus.devices[i].feedback.version,c->bus.devices[i].feedback.hardware,c->bus.devices[i].feedback.limit[0].seen,c->bus.devices[i].feedback.limit[0].at,c->bus.devices[i].feedback.limit[0].speed,c->bus.devices[i].feedback.acceleration[0].seen,c->bus.devices[i].feedback.acceleration[0].at,c->bus.devices[i].configuration_since);
    CHECK(!controller_any_query_active(c));
    CHECK(!controller_blocker(c,&c->bus.devices[0],clock_now)&&!controller_blocker(c,&c->bus.devices[1],clock_now));
    unsigned first=command(c,0,"enable",0),second=command(c,1,"enable",0);CHECK(first&&second>first);
    for (unsigned i=0;i<500&&controller_any_motion_active(c);++i) tick(c,true,true);
    CHECK(!controller_any_motion_active(c)&&c->bus.sim_enabled==63&&c->bus.devices[1].sim_enabled==63);
    settle(c,60);
}
static void cleanup(struct controller *c,const char *directory)
{
    controller_close(c);char path[512];snprintf(path,sizeof path,"%s/control-state.json",directory);
    CHECK(!remove(path));CHECK(!remove_directory(directory));
}
static void path_for(char *path,size_t capacity,unsigned scenario)
{
#ifdef _WIN32
    char temp[MAX_PATH];CHECK(GetTempPathA(sizeof temp,temp)>0);
    snprintf(path,capacity,"%spiper-two-lanes-%lu-%u",temp,(unsigned long)GetCurrentProcessId(),scenario);
#else
    snprintf(path,capacity,"/tmp/piper-two-lanes-%ld-%u",(long)getpid(),scenario);
#endif
}
int main(void)
{
    struct controller *c=calloc(1,sizeof *c);CHECK(c);char directory[512];
    path_for(directory,sizeof directory,1);setup(c,directory);
    unsigned a=command(c,0,"joint",2),b=command(c,1,"joint",-3);CHECK(b>a);
    CHECK(lane(c,0)->request_id==a&&lane(c,1)->request_id==b);
    cJSON *select=cJSON_CreateObject();cJSON_AddStringToObject(select,"device_id",c->bus.devices[1].id);
    cJSON_Delete(request(c,"/api/select",select,200));
    CHECK(!strcmp(c->selected,"simulation:2")&&lane(c,0)->request_id==a&&lane(c,1)->request_id==b);
    cJSON *snapshot=controller_state(c,"fixture");
    CHECK(cJSON_IsTrue(field(snapshot,"multi_device_lanes_supported"))&&cJSON_IsTrue(field(snapshot,"any_motion_active")));
    CHECK(cJSON_GetArraySize(field(snapshot,"motions"))==2);
    CHECK(!strcmp(field(field(snapshot,"motion"),"device_id")->valuestring,"simulation:2"));
    for (unsigned i=0;i<2;++i) {
        cJSON *device=cJSON_GetArrayItem(field(snapshot,"devices"),i),*motion=cJSON_GetArrayItem(field(snapshot,"motions"),i);
        CHECK(cJSON_Compare(field(device,"motion"),motion,true));
        CHECK(cJSON_IsTrue(field(motion,"active"))&&field(motion,"request_id")->valuedouble==(i?b:a));
    }
    cJSON_Delete(snapshot);
    double previous_a=lane(c,0)->heartbeat,previous_b=lane(c,1)->heartbeat;
    unsigned long long before_a=c->bus.devices[0].tx,before_b=c->bus.devices[1].tx;
    cJSON *stale_stop=cJSON_CreateObject();cJSON_AddStringToObject(stale_stop,"owner",owner);cJSON_AddNumberToObject(stale_stop,"request_id",a);
    cJSON_Delete(request(c,"/api/stop",stale_stop,409));
    stale_stop=cJSON_CreateObject();cJSON_AddNumberToObject(stale_stop,"generation",c->bus.devices[0].generation);
    cJSON_Delete(request(c,"/api/stop",stale_stop,409));
    CHECK(c->bus.devices[0].tx==before_a&&c->bus.devices[1].tx==before_b&&lane(c,0)->request_id==a&&lane(c,1)->request_id==b&&lane(c,0)->op==OP_MOVE&&lane(c,1)->op==OP_MOVE);
    cJSON *wrong=identity(c,1);cJSON_AddNumberToObject(wrong,"request_id",a);
    cJSON_Delete(request(c,"/api/heartbeat",wrong,409));
    CHECK(lane(c,0)->heartbeat==previous_a&&lane(c,1)->heartbeat==previous_b);
    wrong=identity(c,1);cJSON_ReplaceItemInObjectCaseSensitive(wrong,"generation",cJSON_CreateNumber(c->bus.devices[1].generation+1));cJSON_AddNumberToObject(wrong,"request_id",b);
    cJSON_Delete(request(c,"/api/heartbeat",wrong,409));
    wrong=identity(c,1);cJSON_AddNumberToObject(wrong,"request_id",a);cJSON_AddStringToObject(wrong,"action","hold");
    cJSON_Delete(request(c,"/api/command",wrong,409));
    cJSON_Delete(request(c,"/api/reconnect",identity(c,0),409));
    for (unsigned i=0;i<700&&controller_any_motion_active(c);++i) tick(c,true,true);
    CHECK(!controller_any_motion_active(c));
    CHECK(fabs(c->bus.devices[0].feedback.arm[0].deg[5]-2)<=.2&&fabs(c->bus.devices[1].feedback.arm[0].deg[5]+3)<=.2);
    CHECK(fabs(c->bus.sim_target[5]-c->bus.devices[1].sim_target[5])>4);
    settle(c,60);

    a=command(c,0,"follow-start",0);b=command(c,1,"follow-start",0);CHECK(b>a);
    for (unsigned i=1;i<=100;++i) {
        follow(c,0,"follow-update",i,1.5,202);follow(c,1,"follow-update",i,-2,202);
        tick(c,true,true);
        CHECK(lane(c,0)->following&&lane(c,1)->following&&lane(c,0)->request_id==a&&lane(c,1)->request_id==b);
    }
    CHECK(fabs(c->bus.sim_target[5]-c->bus.devices[1].sim_target[5])>3);
    follow(c,0,"follow-end",101,0,200);
    CHECK(!lane(c,0)->following&&lane(c,1)->following);
    follow(c,1,"follow-end",101,0,200);
    settle(c,60);CHECK(!controller_any_motion_active(c));

    a=command(c,0,"joint",20);b=command(c,1,"joint",-20);CHECK(b>a);
    for (unsigned i=0;i<90;++i) tick(c,false,true);
    CHECK(lane(c,0)->op==OP_IDLE&&lane(c,1)->op==OP_MOVE&&lane(c,1)->request_id==b);
    snapshot=controller_state(c,"fixture");
    CHECK(cJSON_IsTrue(field(cJSON_GetArrayItem(field(snapshot,"devices"),0),"recovery_required")));
    CHECK(!cJSON_IsTrue(field(cJSON_GetArrayItem(field(snapshot,"devices"),1),"recovery_required")));
    cJSON_Delete(snapshot);
    cJSON_Delete(request(c,"/api/stop",identity(c,1),200));CHECK(!controller_any_motion_active(c));
    cleanup(c,directory);

    path_for(directory,sizeof directory,2);setup(c,directory);
    a=command(c,0,"joint",20);b=command(c,1,"joint",-20);CHECK(b>a);
    injected_fault=0;tick(c,true,true);injected_fault=-1;
    CHECK(lane(c,0)->op==OP_IDLE&&lane(c,1)->op==OP_MOVE&&lane(c,1)->request_id==b);
    unsigned long long tx0=c->bus.devices[0].tx,tx1=c->bus.devices[1].tx;
    snapshot=request(c,"/api/stop",cJSON_CreateObject(),200);
    CHECK(cJSON_GetArraySize(field(snapshot,"stops"))==2);cJSON_Delete(snapshot);
    CHECK(c->bus.devices[0].tx>tx0&&c->bus.devices[1].tx>tx1&&!controller_any_motion_active(c));
    controller_close(c);
    CHECK(controller_init_simulated(c,2,directory,""));
    snapshot=controller_state(c,"fixture");CHECK(!cJSON_IsTrue(field(snapshot,"any_motion_active")));
    for (unsigned i=0;i<2;++i) CHECK(cJSON_IsTrue(field(cJSON_GetArrayItem(field(snapshot,"devices"),i),"recovery_required")));
    cJSON_Delete(snapshot);cleanup(c,directory);

    path_for(directory,sizeof directory,3);setup(c,directory);
    command(c,0,"joint",20);command(c,1,"joint",-20);
    tx0=c->bus.devices[0].tx;tx1=c->bus.devices[1].tx;
    controller_close(c);
    CHECK(c->bus.devices[0].tx>tx0&&c->bus.devices[1].tx>tx1);
    char state[560];snprintf(state,sizeof state,"%s/control-state.json",directory);CHECK(!remove(state));CHECK(!remove_directory(directory));

    path_for(directory,sizeof directory,4);setup(c,directory);
    command(c,0,"gripper",40);failed_stop=0;
    tx0=c->bus.devices[0].tx;tx1=c->bus.devices[1].tx;
    cJSON *global=cJSON_CreateObject();cJSON_AddStringToObject(global,"owner",owner);
    snapshot=request(c,"/api/stop",global,409);
    CHECK(!cJSON_IsTrue(field(cJSON_GetArrayItem(field(snapshot,"stops"),0),"ok"))&&cJSON_IsTrue(field(cJSON_GetArrayItem(field(snapshot,"stops"),1),"ok")));
    /* A successful extra gripper-disable frame cannot turn a failed arm STOP
       into a successful global result, or suppress the partner's attempt. */
    CHECK(c->bus.devices[0].tx>tx0&&c->bus.devices[1].tx>tx1);
    cJSON_Delete(snapshot);failed_stop=-1;cleanup(c,directory);
    free(c);
    puts("PASS multi-device lanes: two independent simulators and simultaneous motions; selection-independent routing; unique requests; heartbeat/generation/hold isolation; local fault isolation; one receive per tick; global STOP, persisted incidents and shutdown cover every arm.");
    return 0;
}

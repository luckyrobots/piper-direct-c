#define _GNU_SOURCE
#include "controller.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static double clock_now=100;
double __wrap_piper_now(void) { return clock_now; }
static void feed_at(struct can_device *d,const double pose[6],double elapsed)
{
    clock_now+=elapsed;
    for (unsigned i=0;i<6;++i) {
        d->feedback.motor[i].seen=d->feedback.motor[i].high_seen=d->feedback.motor[i].enabled=true;
        d->feedback.motor[i].at=d->feedback.motor[i].high_at=clock_now;
    }
    struct can_frame frames[3],status={.can_id=0x2a1,.len=8,.data={1,0,1,0,0}};
    assert(piper_targets(pose,frames));
    for (unsigned i=0;i<3;++i) { frames[i].can_id=0x2a5+i; can_bus_ingest(d,&frames[i],false,clock_now); }
    can_bus_ingest(d,&status,false,clock_now);
}
static void feed(struct can_device *d,const double pose[6]) { feed_at(d,pose,.05); }
static bool ready(struct controller *c)
{
    cJSON *state=controller_state(c,"stationary-test");
    cJSON *device=cJSON_GetArrayItem(cJSON_GetObjectItem(state,"devices"),0);
    cJSON *joints=cJSON_GetObjectItem(device,"joints");
    bool result=cJSON_IsTrue(cJSON_GetObjectItem(cJSON_GetArrayItem(joints,0),"can_move"));
    for (unsigned i=1;i<6;++i)
        assert(result==cJSON_IsTrue(cJSON_GetObjectItem(cJSON_GetArrayItem(joints,i),"can_move")));
    cJSON_Delete(state); return result;
}
int main(void)
{
    char directory[]="/tmp/piper-stationary-XXXXXX"; assert(mkdtemp(directory));
    struct controller c; assert(controller_init(&c,true,directory,""));
    struct can_device *d=&c.bus.devices[0];
    const double fixed[6]={2,25,-25,-20,60,8}; double pose[6]; memcpy(pose,fixed,sizeof pose);
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,"S-V1.9-0");
    for (unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=-180,.max=180,.speed=.3,.at=clock_now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=clock_now};
    }
    for (unsigned n=0;n<25;++n) feed(d,pose);
    assert(ready(&c)); double qualified=d->stationary_since;
    /* Independent encoder quantization phases used to reset the common timer
       and disable every row. All six controls now remain continuously ready. */
    for (unsigned n=0;n<200;++n) {
        for (unsigned i=0;i<6;++i) pose[i]=fixed[i]+((n+i)%2?.02:-.02);
        feed(d,pose); assert(ready(&c)); assert(d->stationary_since==qualified);
    }
    /* Filtering must not turn a genuine slow ramp into repeated fresh holds. */
    memcpy(pose,fixed,sizeof pose);
    for (unsigned n=0;n<35;++n) {
        pose[0]+=.01; feed(d,pose);
        if (n>=6) { assert(!ready(&c)); assert(!d->stationary_since||clock_now-d->stationary_since<.3); }
    }
    for (unsigned n=0;n<25;++n) feed(d,pose);
    assert(ready(&c));
    /* One differentiated encoder-speed pulse at a fixed pose must not revoke
       readiness. Sustained motor movement still blocks independently. */
    d->feedback.motor[5].velocity=.16; feed(d,pose); assert(ready(&c));
    d->feedback.motor[5].velocity=0;
    for (unsigned n=0;n<10;++n) feed(d,pose);
    d->feedback.motor[5].velocity=.02;
    for (unsigned n=0;n<5;++n) { feed(d,pose); if (n>=2) assert(ready(&c)&&!d->stationary_since); }
    d->feedback.motor[5].velocity=0;
    for (unsigned n=0;n<25;++n) feed(d,pose);
    assert(ready(&c));
    d->feedback.motor[2].fault=1; assert(!ready(&c)); d->feedback.motor[2].fault=0;
    d->feedback.arm[0].error=1; assert(!ready(&c)); d->feedback.arm[0].error=0;
    /* Status-only traffic cannot qualify a frozen set of joint packets. */
    ++d->generation; feed(d,pose); assert(!ready(&c));
    struct can_frame status={.can_id=0x2a1,.len=8,.data={1,0,1,0,0}};
    for (unsigned n=0;n<6;++n) { clock_now+=.05; can_bus_ingest(d,&status,false,clock_now); assert(!ready(&c)); }
    clock_now+=.1; can_bus_ingest(d,&status,false,clock_now);
    assert(!ready(&c)&&!d->stationary_since&&!d->stationary_count);
    /* Restore static information invalidated by the intentional status gap. */
    for (unsigned n=0;n<25;++n) feed(d,pose);
    assert(d->stationary_since&&clock_now-d->stationary_since>=.3);
    /* Regression from 106 actual idle observations after the 1-degree wrist
       test. All six joint angles are constant, with isolated raw velocity
       pulses up to 0.161 rad/s and one-quantum motor encoder oscillations. */
    FILE *trace=fopen("tests/fixtures/stationary-idle-velocity.csv","r"); assert(trace);
    char line[1024]; double previous=0; unsigned samples=0; bool warmed=false;
    while (fgets(line,sizeof line,trace)) {
        if (line[0]=='#') continue;
        double seconds,velocities[6];
        assert(sscanf(line,"%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf",&seconds,
            &pose[0],&pose[1],&pose[2],&pose[3],&pose[4],&pose[5],
            &velocities[0],&velocities[1],&velocities[2],&velocities[3],&velocities[4],&velocities[5])==13);
        if (!warmed) {
            for (unsigned i=0;i<6;++i) d->feedback.motor[i].velocity=0;
            for (unsigned n=0;n<25;++n) feed(d,pose);
            warmed=true; assert(d->stationary_since&&clock_now-d->stationary_since>=.3);
        }
        for (unsigned i=0;i<6;++i) d->feedback.motor[i].velocity=velocities[i];
        feed_at(d,pose,samples?seconds-previous:.05);
        assert(d->pose_stationary_since&&clock_now-d->pose_stationary_since>=.3&&ready(&c));
        previous=seconds; ++samples;
    }
    assert(samples==106); fclose(trace);
    /* Heavier real camera/editor load produced much denser speed pulses.
       Check actual encoder positions as well: both independent pose sources
       stay fixed (apart from one raw motor quantum) throughout ten seconds. */
    trace=fopen("tests/fixtures/stationary-loaded-idle.csv","r"); assert(trace);
    samples=0; warmed=false; previous=0; d->simulated=false; d->fd=9;
    while (fgets(line,sizeof line,trace)) {
        if (line[0]=='#') continue;
        double seconds,velocities[6],encoders[6];
        assert(sscanf(line,"%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf,%lf",&seconds,
            &pose[0],&pose[1],&pose[2],&pose[3],&pose[4],&pose[5],
            &velocities[0],&velocities[1],&velocities[2],&velocities[3],&velocities[4],&velocities[5],
            &encoders[0],&encoders[1],&encoders[2],&encoders[3],&encoders[4],&encoders[5])==19);
        for (unsigned i=0;i<6;++i) d->feedback.motor[i].position_raw=(int32_t)llround(encoders[i]);
        if (!warmed) {
            ++d->generation;
            for (unsigned i=0;i<6;++i) d->feedback.motor[i].velocity=0;
            for (unsigned n=0;n<25;++n) feed(d,pose);
            warmed=true; assert(ready(&c));
        }
        for (unsigned i=0;i<6;++i) d->feedback.motor[i].velocity=velocities[i];
        feed_at(d,pose,samples?seconds-previous:.05);
        assert(ready(&c)&&d->pose_stationary_since&&clock_now-d->pose_stationary_since>=.3);
        previous=seconds; ++samples;
    }
    assert(samples==192); fclose(trace);
    /* Kinematic angle reports alone cannot hide an actually moving motor.
       With all reported angles unchanged, independent encoder drift still
       revokes position readiness and the strict stationary qualification. */
    for (unsigned i=0;i<6;++i) d->feedback.motor[i].velocity=0;
    for (unsigned n=0;n<25;++n) feed(d,pose);
    assert(ready(&c));
    for (unsigned n=0;n<8;++n) {
        ++d->feedback.motor[5].position_raw; feed(d,pose);
        if (n>=4) assert(!ready(&c)&&!d->pose_stationary_since&&!d->stationary_since);
    }
    for (unsigned n=0;n<25;++n) feed(d,pose);
    d->feedback.motor[5].velocity=.16;
    for (unsigned n=0;n<5;++n) feed(d,pose);
    assert(ready(&c)&&!d->stationary_since&&c.bus.simulated);
    cJSON *command=cJSON_CreateObject();
    cJSON_AddStringToObject(command,"device_id",d->id); cJSON_AddNumberToObject(command,"generation",d->generation);
    cJSON_AddStringToObject(command,"owner","stable-pose-test-client");
    cJSON_AddBoolToObject(command,"standalone",true); cJSON_AddBoolToObject(command,"clear",true); cJSON_AddBoolToObject(command,"supported",true);
    cJSON_AddStringToObject(command,"action","reset");
    uint64_t before=d->tx; int command_status; cJSON *response=controller_action(&c,"/api/command",command,&command_status);
    assert(command_status==409&&d->tx==before&&c.op==OP_IDLE); cJSON_Delete(response);
    cJSON_ReplaceItemInObject(command,"action",cJSON_CreateString("joint"));
    cJSON_AddNumberToObject(command,"joint",6); cJSON_AddNumberToObject(command,"degrees",.1);
    response=controller_action(&c,"/api/command",command,&command_status);
    assert(command_status==202&&c.op==OP_MOVE&&c.target[5]==.1&&c.bus.simulated);
    cJSON_Delete(response); cJSON_Delete(command);
    d->simulated=true; d->fd=-1;
    controller_close(&c);
    char path[160]; snprintf(path,sizeof path,"%s/control-state.json",directory);
    assert(!unlink(path)); assert(!rmdir(directory));
    puts("Position readiness passed: noisy angles plus 106 idle/192 loaded real samples keep controls usable; genuine joint/independent encoder movement, faults and stale/generation guards reject; strict velocity-dependent dwell remains separate.");
}

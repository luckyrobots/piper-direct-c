#define _GNU_SOURCE
#include "fixture.h"
#include <errno.h>
#include <time.h>

/* The motion loop has no path that ends the process. A malformed frame is
   refused by can_bus_send, and a failed monotonic clock stops every action
   and blocks new ones. Linked with -Wl,--wrap=clock_gettime. */
static bool clock_broken;
int __real_clock_gettime(clockid_t id,struct timespec *t);
int __wrap_clock_gettime(clockid_t id,struct timespec *t)
{
    if (clock_broken&&id==CLOCK_MONOTONIC) { errno=EINVAL; return -1; }
    return __real_clock_gettime(id,t);
}
static cJSON *move(struct can_device *d)
{
    cJSON *body=cJSON_CreateObject(); cJSON_AddStringToObject(body,"device_id",d->id);
    cJSON_AddNumberToObject(body,"generation",d->generation); cJSON_AddStringToObject(body,"action","joint");
    cJSON_AddStringToObject(body,"owner","clock-failure-fixture"); cJSON_AddNumberToObject(body,"joint",6);
    cJSON_AddNumberToObject(body,"degrees",1); cJSON_AddBoolToObject(body,"standalone",true); cJSON_AddBoolToObject(body,"clear",true);
    return body;
}
static void scenarios(void)
{
    char directory[96],path[160]; fixture_directory(directory,sizeof directory,"piper-clock-failure");
    snprintf(path,sizeof path,"%s/control-state.json",directory);
    struct controller c; CHECK(controller_init(&c,true,directory,""));
    double now=piper_now(); controller_tick(&c,now);
    struct can_device *d=&c.bus.devices[0];
    c.bus.sim_enabled=63; controller_tick(&c,piper_now());
    fixture_configure(d,"S-V1.9-0",-180,180,.3,now); fixture_still(d,now-1);

    /* A frame the protocol layer could never build is refused, not asserted on. */
    struct can_frame bad[]={{.can_id=0x155,.len=7},{.can_id=0x800,.len=8},{.can_id=0x150,.len=9,.data={1}}};
    for (unsigned i=0;i<3;++i) {
        uint64_t tx=d->tx; CHECK(!can_bus_send(&c.bus,d,d->generation,&bad[i],1)&&d->tx==tx);
    }

    int status=0; cJSON *body=move(d); cJSON_Delete(controller_action(&c,"/api/command",body,&status)); cJSON_Delete(body);
    CHECK(status==202&&LANE(&c)->op==OP_MOVE&&!piper_clock_failed());
    controller_tick(&c,piper_now()); CHECK(LANE(&c)->op==OP_MOVE);

    /* The clock fails: time freezes at the last good reading and the next tick stops the arm. */
    double last=piper_now(); clock_broken=true;
    CHECK(piper_now()==last&&piper_clock_failed());
    uint64_t tx=d->tx; controller_tick(&c,piper_now());
    CHECK(LANE(&c)->op==OP_IDLE&&d->tx==tx+1&&strstr(LANE(&c)->message,"Monotonic clock failed"));
    cJSON *disk=fixture_read_json(path);
    const cJSON *record=cJSON_GetObjectItem(cJSON_GetObjectItem(disk,"devices"),d->id);
    CHECK(cJSON_IsTrue(cJSON_GetObjectItem(record,"blocked"))&&!strcmp(cJSON_GetStringValue(cJSON_GetObjectItem(record,"kind")),"stop"));
    cJSON_Delete(disk);

    /* The failure is latched even if the clock answers again, and no motion is admitted. */
    clock_broken=false; CHECK(piper_clock_failed()&&piper_now()==last);
    CHECK(strstr(controller_blocker(&c,d,piper_now()),"monotonic clock failed"));
    tx=d->tx; body=move(d); cJSON *result=controller_action(&c,"/api/command",body,&status);
    CHECK(status==409&&d->tx==tx&&LANE(&c)->op==OP_IDLE); cJSON_Delete(body); cJSON_Delete(result);
    /* STOP is still delivered. */
    tx=d->tx; body=cJSON_CreateObject(); cJSON_Delete(controller_action(&c,"/api/stop",body,&status)); cJSON_Delete(body);
    CHECK(status==200&&d->tx==tx+1);
    fixture_cleanup(&c,directory);
    puts("Clock failure: malformed frames are refused; a failed monotonic clock stops the active arm, latches, blocks new motion and still delivers STOP without ending the process.");
}
static const struct check_test tests[]={{"clock-failure","every scenario of this file",scenarios,NULL}};
int main(void) { return check_main("clock-failure",tests,1); }

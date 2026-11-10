#define _GNU_SOURCE
#include "fake_bus.h"
#define CHECK_STATE_SHARED
#include "check.h"
#include <string.h>

struct fake_bus fake_bus={.now=100,.fail_device=-1};

double __real_piper_now(void);
void __real_can_bus_receive(struct can_bus *bus,double now);
bool __real_can_bus_send(struct can_bus *bus,struct can_device *device,unsigned generation,const struct can_frame *frames,size_t count);

double __wrap_piper_now(void) { return fake_bus.now; }

void fake_bus_reset(double now)
{
    free(fake_bus.frames);
    fake_bus=(struct fake_bus){.now=now,.fail_device=-1};
}
void fake_bus_model(fake_bus_receive_fn *receive,fake_bus_send_fn *send) { fake_bus.receive=receive; fake_bus.send=send; }
void fake_bus_watch(const struct controller *c) { fake_bus.watched=c; }
void fake_bus_feed(unsigned device,double at,struct can_frame frame)
{
    CHECK(device<CANBUS_MAX&&fake_bus.scripted<sizeof fake_bus.script/sizeof *fake_bus.script);
    fake_bus.script[fake_bus.scripted].device=device; fake_bus.script[fake_bus.scripted].at=at;
    fake_bus.script[fake_bus.scripted].frame=frame; fake_bus.script[fake_bus.scripted++].delivered=false;
}
void fake_bus_fail(int device,unsigned id,unsigned times) { fake_bus.fail_device=device; fake_bus.fail_id=id; fake_bus.fail_times=times; }

/* canbus.c reads piper_now() inside its own object, where the linker cannot
   substitute it: the simulator stamps configuration replies with the real
   clock and the gripper batch check ages the gripper configuration against
   it. This function is the whole translation between that clock and the
   injected one: the two times the batch check compares move into the real
   clock for the call and back, and replies are restamped, so no test ever
   sees a real timestamp. */
void fake_bus_simulator_receive(struct can_bus *bus,double now) { __real_can_bus_receive(bus,now); }
bool fake_bus_simulator_send(struct can_bus *bus,struct can_device *device,unsigned generation,const struct can_frame *frames,size_t count)
{
    if (!device) return __real_can_bus_send(bus,device,generation,frames,count);
    double config_at=device->feedback.gripper_config_at,since=device->configuration_since,shift=__real_piper_now()-fake_bus.now;
    device->feedback.gripper_config_at+=shift; device->configuration_since+=shift;
    bool sent=__real_can_bus_send(bus,device,generation,frames,count);
    device->feedback.gripper_config_at=config_at; device->configuration_since=since;
    if (sent) for (size_t i=0;i<count;++i) {
        unsigned joint=frames[i].data[0];
        if (frames[i].can_id==0x472&&joint>=1&&joint<=6) {
            if (frames[i].data[1]==1) device->feedback.limit[joint-1].at=fake_bus.now;
            if (frames[i].data[1]==2) device->feedback.acceleration[joint-1].at=fake_bus.now;
        }
        if (frames[i].can_id==0x477) device->feedback.gripper_config_at=fake_bus.now;
    }
    return sent;
}

void __wrap_can_bus_receive(struct can_bus *bus,double now)
{
    if (fake_bus.receive) fake_bus.receive(bus,now); else fake_bus_simulator_receive(bus,now);
    for (unsigned i=0;i<fake_bus.scripted;++i) if (!fake_bus.script[i].delivered&&fake_bus.script[i].at<=now) {
        fake_bus.script[i].delivered=true;
        can_bus_ingest(&bus->devices[fake_bus.script[i].device],&fake_bus.script[i].frame,false,now);
    }
    if (fake_bus.after_receive) fake_bus.after_receive(bus,now);
}

static void persisted(const struct can_device *device,struct fake_frame *out)
{
    if (!fake_bus.watched) return;
    char path[700]; snprintf(path,sizeof path,"%s/control-state.json",fake_bus.watched->state_dir);
    FILE *f=fopen(path,"rb"); if (!f) return;
    static char text[1<<16]; size_t n=fread(text,1,sizeof text-1,f); fclose(f); text[n]=0;
    cJSON *json=cJSON_Parse(text); CHECK(json);
    const cJSON *record=cJSON_GetObjectItemCaseSensitive(cJSON_GetObjectItemCaseSensitive(json,"devices"),device->id);
    const cJSON *state=cJSON_GetObjectItemCaseSensitive(record,"state"),*kind=cJSON_GetObjectItemCaseSensitive(record,"kind");
    if (cJSON_IsString(state)) snprintf(out->state,sizeof out->state,"%s",state->valuestring);
    if (cJSON_IsString(kind)) snprintf(out->kind,sizeof out->kind,"%s",kind->valuestring);
    cJSON_Delete(json);
}
bool __wrap_can_bus_send(struct can_bus *bus,struct can_device *device,unsigned generation,const struct can_frame *frames,size_t count)
{
    size_t first=fake_bus.count; unsigned slot=device?(unsigned)(device-bus->devices):0;
    if (fake_bus.count+count>fake_bus.capacity) {
        fake_bus.capacity=(fake_bus.count+count)*2+256;
        fake_bus.frames=realloc(fake_bus.frames,fake_bus.capacity*sizeof *fake_bus.frames); CHECK(fake_bus.frames);
    }
    struct fake_frame seen={.at=fake_bus.now,.device=slot,.batch=fake_bus.batches++};
    if (device) persisted(device,&seen);
    bool injected=false;
    for (size_t i=0;i<count;++i) {
        seen.frame=frames[i]; fake_bus.frames[fake_bus.count++]=seen;
        if (fake_bus.fail_times&&(fake_bus.fail_device<0||fake_bus.fail_device==(int)slot)&&
            (!fake_bus.fail_id||fake_bus.fail_id==frames[i].can_id)) injected=true;
    }
    /* can_bus_record_tx_failure() stamps with the real clock (see above). */
    struct can_tx_failure before=device?device->tx_failure:(struct can_tx_failure){0};
    bool sent=false;
    if (injected) { --fake_bus.fail_times; can_bus_record_tx_failure(device,generation,1,(unsigned)count,frames[0].can_id,5,0); }
    else sent=fake_bus.send?fake_bus.send(bus,device,generation,frames,count):fake_bus_simulator_send(bus,device,generation,frames,count);
    if (device&&memcmp(&before,&device->tx_failure,sizeof before)) device->tx_failure.at=fake_bus.now;
    for (size_t i=first;i<first+count;++i) fake_bus.frames[i].accepted=sent;
    return sent;
}

const struct fake_frame *fake_bus_next(size_t *cursor,int device,unsigned id)
{
    while (*cursor<fake_bus.count) {
        const struct fake_frame *f=&fake_bus.frames[(*cursor)++];
        if ((device<0||f->device==(unsigned)device)&&(!id||f->frame.can_id==id)) return f;
    }
    return NULL;
}
unsigned fake_bus_sent(size_t mark,int device,unsigned id)
{
    unsigned n=0; while (fake_bus_next(&mark,device,id)) ++n;
    return n;
}

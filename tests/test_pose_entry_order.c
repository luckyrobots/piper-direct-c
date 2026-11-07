/* Offline MCU model: requesting position mode immediately activates its last
   latched six-angle endpoint. A new move must replace that stale endpoint with
   the admission's measured hold before its first mode packet. No USB opens. */
#include "controller.h"
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
struct fake_mcu { double frozen[6],latched[6]; unsigned seeds,modes; bool active; };
static struct fake_mcu mcu[2];
static int fail_seed=-1;
static unsigned fail_frame,stops[2];
double __wrap_piper_now(void) { return clock_now; }
static unsigned slot(struct can_bus *bus,struct can_device *d)
{
    assert(d>=bus->devices&&d<bus->devices+2);return (unsigned)(d-bus->devices);
}
static void decode_target(struct fake_mcu *m,const struct can_frame *frame)
{
    struct piper_feedback decoded={0};
    assert(piper_decode(&decoded,0x2a5+frame->can_id-0x155,frame->data,8,clock_now));
    unsigned first=(frame->can_id-0x155)*2;
    m->latched[first]=decoded.arm[0].deg[first];m->latched[first+1]=decoded.arm[0].deg[first+1];
}
static void mode_commit(struct fake_mcu *m,double actual[6])
{
    /* Deliberately reproduce the hazardous MCU ordering, rather than assuming
       that targets arriving after the mode packet form an atomic operation. */
    memcpy(actual,m->latched,6*sizeof(double));m->active=true;
}
void __wrap_can_bus_receive(struct can_bus *bus,double now)
{
    for (unsigned n=0;n<2;++n) {
        struct can_device *d=&bus->devices[n];struct piper_sample *s=&d->feedback.arm[0];
        if (mcu[n].active) for (unsigned i=0;i<6;++i) {
            double remaining=mcu[n].latched[i]-s->deg[i];
            s->deg[i]+=copysign(fmin(fabs(remaining),.1),remaining);
        }
        s->status_seen=true;s->status_at=now;
        for (unsigned i=0;i<6;++i) {
            s->joint_seen[i]=true;s->joint_at[i]=now;
            d->feedback.motor[i].seen=d->feedback.motor[i].high_seen=true;
            d->feedback.motor[i].at=d->feedback.motor[i].high_at=now;
        }
    }
}
bool __wrap_can_bus_send(struct can_bus *bus,struct can_device *d,unsigned generation,const struct can_frame *frames,size_t count)
{
    assert(bus->simulated&&can_bus_batch_allowed(d,frames,count));
    if(generation!=d->generation)return false;
    unsigned n=slot(bus,d);struct fake_mcu *m=&mcu[n];
    if (count==1&&frames[0].can_id==0x150) { ++stops[n];return true; }
    bool seed=count==3;
    if (seed) { assert(frames[0].can_id==0x155&&frames[1].can_id==0x156&&frames[2].can_id==0x157);++m->seeds; }
    else assert(count==4&&frames[0].can_id==0x151);
    for (unsigned i=0;i<count;++i) {
        if (seed&&fail_seed==(int)n&&i+1==fail_frame) {
            can_bus_record_tx_failure(d,generation,i+1,(unsigned)count,frames[i].can_id,121,14);return false;
        }
        if (frames[i].can_id==0x151) {
            if (!m->modes) {
                assert(m->seeds==1);
                for (unsigned j=0;j<6;++j) assert(fabs(m->latched[j]-m->frozen[j])<.00001);
                mode_commit(m,d->feedback.arm[0].deg);
                for (unsigned j=0;j<6;++j) assert(fabs(d->feedback.arm[0].deg[j]-m->frozen[j])<.00001);
            }
            ++m->modes;d->feedback.arm[0].control=1;
        } else decode_target(m,&frames[i]);
        ++d->tx;
    }
    return true;
}
static struct controller_lane *lane(struct controller *c,unsigned n)
{
    for (unsigned i=0;i<CANBUS_MAX;++i) if (!strcmp(c->lanes[i].lane_device,c->bus.devices[n].id)) return &c->lanes[i];
    assert(!"missing lane");return NULL;
}
static void fixture(struct controller *c,char *directory,size_t capacity,unsigned scenario)
{
#ifdef _WIN32
    char temp[MAX_PATH];assert(GetTempPathA(sizeof temp,temp)>0);
    snprintf(directory,capacity,"%spiper-pose-entry-%lu-%u",temp,(unsigned long)GetCurrentProcessId(),scenario);
#else
    snprintf(directory,capacity,"/tmp/piper-pose-entry-%ld-%u",(long)getpid(),scenario);
#endif
    clock_now+=10;assert(controller_init_simulated(c,2,directory,""));
    memset(mcu,0,sizeof mcu);memset(stops,0,sizeof stops);fail_seed=-1;
    const double poses[2][6]={{-5.601,2.281,.819,1.475,7.239,-1.086},{5,15,-35,-5,12,3}};
    const double limits[6][2]={{-150,150},{0,180},{-170,0},{-100,100},{-70,70},{-180,180}};
    for (unsigned n=0;n<2;++n) {
        struct can_device *d=&c->bus.devices[n];struct piper_sample *s=&d->feedback.arm[0];
        d->stationary_since=clock_now-1;s->control=s->motion_mode=1;
        memcpy(s->deg,poses[n],sizeof s->deg);memcpy(mcu[n].frozen,poses[n],sizeof s->deg);
        memcpy(mcu[n].latched,poses[n],sizeof s->deg);mcu[n].latched[4]+=20;
        strcpy(d->feedback.hardware,"H-V1.2-1");strcpy(d->feedback.version,"S-V1.9-0");
        for (unsigned i=0;i<6;++i) {
            d->feedback.motor[i].enabled=true;
            d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=limits[i][0],.max=limits[i][1],.speed=3,.at=clock_now};
            d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=clock_now};
        }
    }
    __wrap_can_bus_receive(&c->bus,clock_now);
}
static void start(struct controller *c,unsigned n)
{
    struct can_device *d=&c->bus.devices[n];cJSON *body=cJSON_CreateObject();
    cJSON_AddStringToObject(body,"device_id",d->id);cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddStringToObject(body,"owner","pose-entry-order-test");cJSON_AddStringToObject(body,"action","joint");
    cJSON_AddBoolToObject(body,"standalone",true);cJSON_AddBoolToObject(body,"clear",true);
    cJSON_AddNumberToObject(body,"joint",n?6:3);cJSON_AddNumberToObject(body,"degrees",n?6:-.5);
    cJSON_AddNumberToObject(body,"speed_deg_s",5);cJSON_AddNumberToObject(body,"tolerance_deg",.1);
    int status;cJSON *result=controller_action(c,"/api/command",body,&status);
    assert(status==202);cJSON_Delete(body);cJSON_Delete(result);
}
static void tick(struct controller *c)
{
    clock_now+=.025;
    c->heartbeat=clock_now;
    for (unsigned n=0;n<2;++n) { struct controller_lane *l=lane(c,n);if(l->op!=OP_IDLE)l->heartbeat=clock_now; }
    controller_tick(c,clock_now);
}
static void cleanup(struct controller *c,const char *directory)
{
    controller_close(c);char path[600];snprintf(path,sizeof path,"%s/control-state.json",directory);
    assert(!remove(path));assert(!remove_directory(directory));
}
int main(void)
{
    /* Validate the model can expose the old mode-first hazard before testing
       production admission ordering against that same modeled controller. */
    struct fake_mcu stale={.latched={0,0,0,0,27.239,0}};double actual[6]={0};
    mode_commit(&stale,actual);assert(actual[4]==27.239);
    struct controller *c=calloc(1,sizeof *c);assert(c);char directory[512];
    fixture(c,directory,sizeof directory,1);start(c,0);start(c,1);tick(c);
    for(unsigned n=0;n<2;++n) assert(mcu[n].seeds==1&&!mcu[n].modes&&!lane(c,n)->position_seed_pending);
    tick(c);
    for(unsigned n=0;n<2;++n) assert(mcu[n].seeds==1&&mcu[n].modes==1&&lane(c,n)->op!=OP_IDLE&&!stops[n]);
    for(unsigned i=0;i<8;++i)tick(c);
    for(unsigned n=0;n<2;++n) assert(mcu[n].seeds==1&&mcu[n].modes>1&&!stops[n]);
    assert(mcu[1].latched[5]>mcu[1].frozen[5]);cleanup(c,directory);

    /* Every partial seed failure retires this lane before any later mode;
       the other lane still seeds once and advances independently. */
    for(unsigned failure=1;failure<=3;++failure) {
        fixture(c,directory,sizeof directory,1+failure);start(c,0);start(c,1);fail_seed=0;fail_frame=failure;tick(c);
        assert(lane(c,0)->op==OP_IDLE&&!lane(c,0)->position_seed_pending&&stops[0]==1&&!mcu[0].modes);
        for(unsigned i=0;i<4;++i)tick(c);
        assert(!mcu[0].modes&&mcu[1].seeds==1&&mcu[1].modes&&!stops[1]);cleanup(c,directory);
    }
    fixture(c,directory,sizeof directory,5);start(c,0);tick(c);++c->bus.devices[0].generation;tick(c);
    assert(lane(c,0)->op==OP_IDLE&&!lane(c,0)->position_seed_pending&&!mcu[0].modes);cleanup(c,directory);
    fixture(c,directory,sizeof directory,6);start(c,0);
    c->bus.devices[0].feedback.arm[0].deg[4]+=.501;tick(c);
    assert(lane(c,0)->op==OP_IDLE&&!lane(c,0)->position_seed_pending&&!mcu[0].seeds&&!mcu[0].modes&&stops[0]==1);
    cleanup(c,directory);
    fixture(c,directory,sizeof directory,7);start(c,0);tick(c);
    cJSON *cancel=cJSON_CreateObject();struct can_device *d=&c->bus.devices[0];
    cJSON_AddStringToObject(cancel,"device_id",d->id);cJSON_AddNumberToObject(cancel,"generation",d->generation);
    cJSON_AddStringToObject(cancel,"owner","pose-entry-order-test");cJSON_AddNumberToObject(cancel,"request_id",lane(c,0)->request_id);
    int status;cJSON *result=controller_action(c,"/api/stop",cancel,&status);assert(status==200);
    cJSON_Delete(cancel);cJSON_Delete(result);for(unsigned i=0;i<4;++i)tick(c);
    assert(lane(c,0)->op==OP_IDLE&&!lane(c,0)->position_seed_pending&&!mcu[0].modes&&stops[0]==1);cleanup(c,directory);
    fixture(c,directory,sizeof directory,8);start(c,0);
    c->bus.devices[0].feedback.arm[0].control=2;c->bus.devices[0].feedback.arm[0].teach=1;
    c->bus.devices[0].feedback.arm[0].arm_status=11;tick(c);
    assert(lane(c,0)->op==OP_IDLE&&!lane(c,0)->position_seed_pending&&!mcu[0].seeds&&!mcu[0].modes&&!stops[0]);
    c->bus.devices[0].feedback.arm[0].control=1;c->bus.devices[0].feedback.arm[0].teach=0;
    c->bus.devices[0].feedback.arm[0].arm_status=0;tick(c);
    assert(!mcu[0].seeds&&!mcu[0].modes);cleanup(c,directory);
    free(c);puts("PASS position entry ordering: stale MCU target is seeded with each lane's frozen measured pose before mode; one-shot seeds preserve later references, partial failure and generation changes cannot commit mode.");
}

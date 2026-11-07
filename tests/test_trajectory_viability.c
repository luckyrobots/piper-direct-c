#define _GNU_SOURCE
/* White-box reference-math tests avoid inventing a new public motion API. */
#include "../src/controller.c"
#include <assert.h>
#include <unistd.h>

static double clock_now=100;
static unsigned stop_packets,pose_packets;
static bool fail_pose;
double __wrap_piper_now(void) { return clock_now; }
void __wrap_can_bus_receive(struct can_bus *bus,double now)
{
    struct can_device *d=&bus->devices[0];struct piper_sample *s=&d->feedback.arm[0];
    d->last_rx=now;s->status_seen=true;s->status_at=now;
    for (unsigned i=0;i<6;++i) {
        s->joint_seen[i]=true;s->joint_at[i]=now;
        d->feedback.motor[i].seen=d->feedback.motor[i].high_seen=d->feedback.motor[i].enabled=true;
        d->feedback.motor[i].at=d->feedback.motor[i].high_at=now;
    }
}
bool __wrap_can_bus_send(struct can_bus *bus,struct can_device *d,unsigned generation,const struct can_frame *frames,size_t count)
{
    assert(bus->simulated&&generation==d->generation);
    if (count==1) { assert(frames[0].can_id==0x150&&frames[0].data[0]==1);++stop_packets;return true; }
    assert(count==4&&frames[0].can_id==0x151);
    if (fail_pose) { strcpy(d->problem,"fixture failed complete pose write");return false; }
    struct piper_feedback decoded={0};
    for (unsigned i=1;i<4;++i) { assert(frames[i].can_id==0x154+i);assert(piper_decode(&decoded,frames[i].can_id+0x150,frames[i].data,8,clock_now)); }
    memcpy(bus->sim_target,decoded.arm[0].deg,sizeof bus->sim_target);d->tx+=4;++pose_packets;return true;
}
static void configure_math(struct controller *c,struct can_device *d)
{
    memset(c,0,sizeof *c);memset(d,0,sizeof *d);c->speed_percent=90;
    const double minimum[6]={-150,0,-170,-100,-70,-170},maximum[6]={150,180,0,100,70,170};
    for (unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=minimum[i],.max=maximum[i],.speed=3,.at=clock_now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=clock_now};
        c->trajectory_minimum[i]=minimum[i];c->trajectory_maximum[i]=maximum[i];
        c->trajectory_speed[i]=3*180/3.141592653589793*.9;c->trajectory_acceleration[i]=5*180/3.141592653589793;
    }
}
static void assert_step(struct controller *c,struct can_device *d,double dt)
{
    double before[6],old_velocity[6],previous_dt=c->trajectory_previous_dt;
    memcpy(before,c->command,sizeof before);memcpy(old_velocity,c->trajectory_velocity,sizeof old_velocity);
    enum trajectory_step_result result=trajectory_reference_step(c,d,clock_now,dt);
    if (result!=TRAJECTORY_STEP_OK) fprintf(stderr,"math failed: %s\n",c->error);
    assert(result==TRAJECTORY_STEP_OK);
    for (unsigned i=0;i<6;++i) {
        double velocity=(c->command[i]-before[i])/dt,midpoint=previous_dt>0?(dt+previous_dt)/2:dt;
        assert(fabs(velocity)<=c->trajectory_speed[i]+1e-7);
        assert(fabs(velocity-old_velocity[i])<=c->trajectory_acceleration[i]*midpoint+1e-7);
        assert(c->command[i]>=d->feedback.limit[i].min&&c->command[i]<=d->feedback.limit[i].max);
        assert(fabs(c->command[i]-d->feedback.arm[0].deg[i])<=2.0000001);
        assert(fabs(c->command[i]*1000-round(c->command[i]*1000))<1e-7);
    }
}
struct observation { double at,q[6]; };
static void delayed_feedback(double lag)
{
    struct controller c;struct can_device d;configure_math(&c,&d);
    struct observation history[2401]={0};history[0].at=clock_now;
    unsigned observed=0;
    for (unsigned n=0;n<2400;++n) {
        if (n==20) assert(c.trajectory_velocity[1]>.1);
        if (n==30) assert(c.trajectory_velocity[1]<-.1);
        const double interval[4]={.02,.025,.035,.05};double dt=interval[n%4];clock_now+=dt;
        double sample_time=clock_now-lag;
        while (observed<n&&history[observed+1].at<=sample_time) ++observed;
        for (unsigned i=0;i<6;++i) {
            double measured=history[observed].q[i];
            if (observed<n) {
                double fraction=fmax(0,fmin(1,(sample_time-history[observed].at)/(history[observed+1].at-history[observed].at)));
                measured+=(history[observed+1].q[i]-measured)*fraction;
            }
            d.feedback.arm[0].deg[i]=measured;
            const double first[6]={12,20,-20,15,-12,20},second[6]={-12,4,-4,-15,12,-20};
            c.target[i]=n<20?first[i]:n<30?second[i]:n<800?first[i]:n<1600?second[i]:0;
        }
        assert_step(&c,&d,dt);
        history[n+1].at=clock_now;memcpy(history[n+1].q,c.command,sizeof c.command);
    }
    for (unsigned i=0;i<6;++i) assert(fabs(c.command[i])<.003);
}
static void fixture(struct controller *c,char directory[96])
{
    strcpy(directory,"/tmp/piper-trajectory-viability-XXXXXX");assert(mkdtemp(directory));clock_now+=10;
    assert(controller_init(c,true,directory,""));stop_packets=pose_packets=0;fail_pose=false;
    struct can_device *d=&c->bus.devices[0];d->stationary_since=clock_now-1;
    d->feedback.arm[0].control=1;d->feedback.arm[0].motion_mode=1;
    strcpy(d->feedback.hardware,"H-V1.2-1");strcpy(d->feedback.version,"S-V1.9-1");
    memset(d->feedback.arm[0].deg,0,sizeof d->feedback.arm[0].deg);
    for (unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=-150,.max=150,.speed=3,.at=clock_now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=clock_now};
    }
    __wrap_can_bus_receive(&c->bus,clock_now);
    cJSON *body=cJSON_CreateObject();cJSON_AddStringToObject(body,"device_id",d->id);cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddStringToObject(body,"owner","trajectory-viability-fixture");cJSON_AddStringToObject(body,"action","follow-start");
    cJSON_AddNumberToObject(body,"speed_percent",90);cJSON_AddBoolToObject(body,"trajectory",true);
    cJSON_AddBoolToObject(body,"standalone",true);cJSON_AddBoolToObject(body,"clear",true);
    int status;cJSON *reply=controller_action(c,"/api/command",body,&status);assert(status==202&&c->trajectory_follow);
    cJSON_Delete(reply);cJSON_Delete(body);
}
static void close_fixture(struct controller *c,const char *directory)
{
    assert(c->op==OP_IDLE);controller_close(c);char path[128];snprintf(path,sizeof path,"%s/control-state.json",directory);
    assert(!unlink(path));assert(!rmdir(directory));
}
int main(void)
{
    struct controller c;struct can_device d;configure_math(&c,&d);
    for (unsigned n=0;n<1000;++n) {
        for (unsigned i=0;i<6;++i) c.target[i]=i==2?-20:20;
        clock_now+=.025;assert_step(&c,&d,.025);
    }
    assert(c.command[1]>1.99&&c.command[1]<=2&&fabs(c.trajectory_velocity[1])<1e-9);
    delayed_feedback(.055);delayed_feedback(.075);

    /* Captured live J2 geometry (reference4.824, measured4.191, destination7.056),
       with viable approach velocities; the delayed-encoder cases above cover
       the evolving reference rather than assuming an unknown logged velocity. */
    configure_math(&c,&d);c.command[1]=4.824;d.feedback.arm[0].deg[1]=4.191;c.target[1]=7.056;
    c.trajectory_velocity[1]=14;c.trajectory_previous_dt=.025;clock_now+=.025;assert_step(&c,&d,.025);

    char directory[96];
    for (unsigned failure=0;failure<7;++failure) {
        fixture(&c,directory);struct can_device *device=&c.bus.devices[0];
        c.command[1]=1.8;c.target[1]=10;c.trajectory_velocity[1]=15;c.trajectory_previous_dt=.025;
        if (!failure) {
            device->feedback.arm[0].deg[1]=.8;
            assert(trajectory_reference_step(&c,device,clock_now,.025)==TRAJECTORY_STEP_OK);
            device->feedback.arm[0].deg[1]=0; /* Fresh encoder wall recedes after a previously viable reference. */
        }
        if (failure==1) c.command[1]=3.1;
        if (failure==2) c.obstruction[1]=(struct obstruction_watch){.progress_position=0,.progress_at=clock_now-.6,.direction=1};
        if (failure==3) device->feedback.acceleration[1].max=4;
        if (failure==4) fail_pose=true;
        if (failure==5) device->feedback.arm[0].error=1;
        if (failure==6) c.trajectory_velocity[1]=NAN;
        clock_now+=.025;c.heartbeat=c.follow_updated=clock_now;controller_tick(&c,clock_now);
        assert(c.op==OP_IDLE&&!c.trajectory_follow);
        if (!failure) {
            assert(!c.following&&!stop_packets&&pose_packets==1&&device->feedback.motor[1].enabled);
            assert(strstr(c.message,"boundary")&&strstr(c.message,"Holding"));
            for (unsigned i=0;i<6;++i) assert(c.command[i]==device->feedback.arm[0].deg[i]&&c.bus.sim_target[i]==c.command[i]);
        } else {
            assert(stop_packets==1);
            if (failure==1) assert(strstr(c.message,"3 degrees"));
            if (failure==2) assert(strstr(c.message,"obstruction"));
            if (failure==3) assert(strstr(c.message,"limits changed"));
            if (failure==4) assert(strstr(c.message,"hold failed"));
            if (failure==5) assert(strstr(c.message,"fault"));
            if (failure==6) assert(strstr(c.message,"arithmetic"));
        }
        close_fixture(&c,directory);
    }
    puts("Trajectory viability passed: quantized two-wall braking, frozen/55/75ms encoders, variable ticks, reversal, captured J2 geometry, healthy hold and unchanged hard fault precedence.");
}

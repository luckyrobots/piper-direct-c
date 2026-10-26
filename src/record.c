#define _GNU_SOURCE
#include "canbus.h"
#include "cJSON.h"
#include <errno.h>
#include <math.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

/* Passive recorder: there is deliberately no call to can_bus_send. The shared
   discovery code reads link metadata but never changes it or sends robot CAN. */
static volatile sig_atomic_t interrupted;
static void on_signal(int n) { (void)n; interrupted=1; }
static void value(cJSON *o,const char *key,double x)
{
    cJSON_AddItemToObject(o,key,isfinite(x)?cJSON_CreateNumber(x):cJSON_CreateNull());
}
static cJSON *numbers(const double *v,unsigned n)
{
    cJSON *a=cJSON_CreateArray();
    for (unsigned i=0;i<n;++i)
        cJSON_AddItemToArray(a,isfinite(v[i])?cJSON_CreateNumber(v[i]):cJSON_CreateNull());
    return a;
}
static bool line(FILE *out,cJSON *o)
{
    char *s=cJSON_PrintUnformatted(o);
    bool ok=s&&fprintf(out,"%s\n",s)>0;
    free(s); cJSON_Delete(o); return ok;
}
static bool ready(struct can_device *d,double now)
{
    if (!d||!d->present||!d->up||!d->stable||(!d->simulated&&d->fd<0)) return false;
    if (!piper_fresh(&d->feedback.arm[0],now)) return false;
    for (unsigned i=1;i<3;++i)
        if (piper_fresh(&d->feedback.arm[i],now)) return false;
    return true;
}
static cJSON *sample(struct can_device *d,double now,double start,unsigned index)
{
    struct piper_feedback *f=&d->feedback; struct piper_sample *s=&f->arm[0];
    cJSON *j=cJSON_CreateObject(); cJSON_AddStringToObject(j,"type","sample");
    value(j,"frame_index",index); value(j,"timestamp",now-start); value(j,"host_monotonic_s",now);
    cJSON_AddItemToObject(j,"joint_degrees",numbers(s->deg,6));
    double age[6],amps[6],velocity[6],radians[6];
    unsigned mask=0,fault=0,fresh_mask=0;
    for (unsigned i=0;i<6;++i) {
        age[i]=now-s->joint_at[i]; radians[i]=s->deg[i]*3.141592653589793/180;
        bool fresh=f->motor[i].high_seen&&piper_recent(f->motor[i].high_at,now);
        amps[i]=fresh?f->motor[i].current:NAN; velocity[i]=fresh?f->motor[i].velocity:NAN;
        if (f->motor[i].seen&&piper_recent(f->motor[i].at,now)) {
            fresh_mask|=1u<<i;
            if (f->motor[i].enabled) mask|=1u<<i;
            if (f->motor[i].fault) fault|=1u<<i;
        }
    }
    cJSON_AddItemToObject(j,"joint_radians",numbers(radians,6));
    cJSON_AddItemToObject(j,"joint_sample_age_s",numbers(age,6));
    cJSON_AddItemToObject(j,"motor_current_a",numbers(amps,6));
    cJSON_AddItemToObject(j,"motor_velocity_rad_s",numbers(velocity,6));
    bool gf=f->gripper_seen&&piper_recent(f->gripper_at,now);
    value(j,"gripper_feedback_mm",gf?f->gripper_mm:NAN);
    value(j,"gripper_effort",gf?f->gripper_effort:NAN);
    value(j,"gripper_sample_age_s",f->gripper_seen?now-f->gripper_at:NAN);
    cJSON_AddBoolToObject(j,"gripper_fresh",gf);
    value(j,"gripper_enabled",gf?f->gripper_enabled:NAN); value(j,"gripper_fault",gf?f->gripper_fault:NAN);
    value(j,"control_mode",s->control); value(j,"teach_state",s->teach);
    value(j,"arm_status",s->arm_status); value(j,"error_code",s->error);
    value(j,"enabled_mask",mask); value(j,"driver_fault_mask",fault); value(j,"driver_fresh_mask",fresh_mask);
    value(j,"rx_frames",(double)d->rx); value(j,"can_errors",(double)d->errors);
    return j;
}
int main(int argc,char **argv)
{
    const char *identity=NULL,*output=NULL; double seconds=30; bool sim=false;
    for (int i=1;i<argc;++i) {
        if (!strcmp(argv[i],"--help")) {
            puts("piper-record --device usb:SERIAL --out FILE.jsonl [--seconds 30] [--simulate]\n"
                 "Passive 50 Hz host snapshots, no robot commands. Refuses overwrite.\n"
                 "Disconnect, stale joint feedback or Ctrl-C yields an incomplete footer.\n"
                 "Simulation uses --device simulation:one and never opens real CAN."); return 0;
        }
        if (!strcmp(argv[i],"--simulate")) { sim=true; continue; }
        if (i+1>=argc) { fprintf(stderr,"Missing option value\n"); return 1; }
        if (!strcmp(argv[i],"--device")) identity=argv[++i];
        else if (!strcmp(argv[i],"--out")) output=argv[++i];
        else if (!strcmp(argv[i],"--seconds")) {
            char *end; errno=0; seconds=strtod(argv[++i],&end);
            if (errno||*end||!isfinite(seconds)||seconds<.2||seconds>600) {
                fprintf(stderr,"Duration must be 0.2 to 600 seconds\n"); return 1;
            }
        } else { fprintf(stderr,"Unknown option: %s\n",argv[i]); return 1; }
    }
    if (!identity||!output||(sim?strcmp(identity,"simulation:one"):strncmp(identity,"usb:",4))) {
        fprintf(stderr,"An explicit stable USB identity and output file are required\n"); return 1;
    }
    FILE *out=fopen(output,"wx"); if (!out) { perror(output); return 1; }
    signal(SIGINT,on_signal); signal(SIGTERM,on_signal);
    struct can_bus bus; can_bus_init(&bus,sim); can_bus_scan(&bus);
    double wait_start=piper_now(),scan_at=wait_start,start=0,next=0,last=0,max_gap=0;
    unsigned generation=0,count=0,late=0; const char *reason="duration_reached"; bool ok=true;
    cJSON *header=cJSON_CreateObject(); cJSON_AddStringToObject(header,"type","header");
    cJSON_AddStringToObject(header,"kind","piper_passive_measured_trajectory");
    cJSON_AddStringToObject(header,"device_id",identity); cJSON_AddBoolToObject(header,"simulated",sim);
    cJSON_AddBoolToObject(header,"commands_sent",false); value(header,"requested_seconds",seconds);
    value(header,"nominal_hz",50); value(header,"schema_version",1);
    struct timespec wall; clock_gettime(CLOCK_REALTIME,&wall);
    value(header,"capture_start_unix_s",wall.tv_sec+wall.tv_nsec/1e9);
    cJSON_AddStringToObject(header,"timing","Host monotonic snapshots; joint timestamps are host receive times, not hardware clock synchronization.");
    cJSON_AddStringToObject(header,"action_semantics","No action targets recorded. Measured joints are observations, not issued commands.");
    ok=line(out,header);
    while (ok&&!interrupted) {
        double now=piper_now();
        if (now-scan_at>=1) { can_bus_scan(&bus); scan_at=now; }
        can_bus_receive(&bus,now);
        struct can_device *d=can_bus_find(&bus,identity);
        if (!start) {
            if (ready(d,now)) { start=next=now; generation=d->generation; }
            else if (now-wait_start>5) { reason="no_fresh_selected_arm"; break; }
        } else if (!ready(d,now)||d->generation!=generation) { reason="connection_or_feedback_lost"; break; }
        if (start&&now>=next) {
            if (now-start>=seconds) break;
            if (last) max_gap=fmax(max_gap,now-last);
            if (now-next>=.02) ++late;
            ok=line(out,sample(d,now,start,count++)); last=now;
            next+=.02; if (next<=now) next=now+.02; /* Never fabricate missed samples. */
            if (count%50==0&&fflush(out)) ok=false;
        }
        struct timespec nap={.tv_nsec=1000000}; nanosleep(&nap,NULL);
    }
    if (interrupted) reason="interrupted";
    if (!ok) reason="write_failed";
    bool complete=ok&&count&&!strcmp(reason,"duration_reached");
    cJSON *footer=cJSON_CreateObject(); cJSON_AddStringToObject(footer,"type","footer");
    cJSON_AddBoolToObject(footer,"capture_complete",complete); cJSON_AddStringToObject(footer,"reason",reason);
    value(footer,"samples",count); value(footer,"late_intervals",late); value(footer,"max_sample_gap_s",max_gap);
    cJSON_AddBoolToObject(footer,"task_success_verified",false);
    ok=line(out,footer)&&ok;
    if (fflush(out)||fsync(fileno(out))) ok=false;
    if (fclose(out)) ok=false;
    can_bus_close(&bus);
    fprintf(stderr,"%s: %u measured samples; %s; no robot commands sent\n",output,count,reason);
    return complete&&ok?0:2;
}

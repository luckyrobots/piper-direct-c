#define _GNU_SOURCE
#include "controller.h"
#include <errno.h>
#include <fcntl.h>
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <sys/stat.h>
#include <unistd.h>
#ifdef _WIN32
#include <windows.h>
#include <direct.h>
#define CAN_STATE_BUS_OFF 3
#define CAN_CTRLMODE_LOOPBACK 1
#define CAN_CTRLMODE_LISTENONLY 2
#define CAN_CTRLMODE_FD 32
#else
#include <linux/can/netlink.h>
#endif

static cJSON *field(const cJSON *o,const char *key) { return cJSON_GetObjectItemCaseSensitive(o,key); }
static const char *text_field(const cJSON *o,const char *key)
{
    cJSON *v=field(o,key); return cJSON_IsString(v)&&v->valuestring?v->valuestring:"";
}
static bool yes(const cJSON *o,const char *key) { return cJSON_IsTrue(field(o,key)); }
static void put(cJSON *o,const char *key,cJSON *v) { cJSON_DeleteItemFromObjectCaseSensitive(o,key); cJSON_AddItemToObject(o,key,v); }
static void str(cJSON *o,const char *key,const char *value) { put(o,key,cJSON_CreateString(value)); }
static void num(cJSON *o,const char *key,double value) { put(o,key,isfinite(value)?cJSON_CreateNumber(value):cJSON_CreateNull()); }
static void flag(cJSON *o,const char *key,bool value) { put(o,key,cJSON_CreateBool(value)); }
static cJSON *read_json(const char *dir,const char *filename,bool *invalid)
{
    char path[1024]; snprintf(path,sizeof path,"%s/%s",dir,filename);
    FILE *f=fopen(path,"rb");
    if (!f) { if (errno!=ENOENT) *invalid=true; return NULL; }
    char *buffer=calloc(1,1024*1024+1); if (!buffer) { fclose(f); *invalid=true; return NULL; }
    size_t n=fread(buffer,1,1024*1024,f); bool bad=ferror(f)||!feof(f); fclose(f);
    const char *end=NULL; cJSON *json=bad?NULL:cJSON_ParseWithLengthOpts(buffer,n+1,&end,1);
    if (!json) *invalid=true;
    free(buffer); return json;
}
static bool persist(struct controller *c)
{
    char path[1024],temp[1024];
    snprintf(path,sizeof path,"%s/control-state.json",c->state_dir);
    snprintf(temp,sizeof temp,"%s/.control-XXXXXX",c->state_dir);
    char *data=cJSON_PrintUnformatted(c->saved); if (!data) { c->storage_failed=true; return false; }
#ifdef _WIN32
    char unique[MAX_PATH]={0};
    bool ok=GetTempFileNameA(c->state_dir,"pip",0,unique)!=0;
    HANDLE file=ok?CreateFileA(unique,GENERIC_WRITE,0,NULL,TRUNCATE_EXISTING,FILE_ATTRIBUTE_NORMAL,NULL):INVALID_HANDLE_VALUE;
    if (file==INVALID_HANDLE_VALUE) ok=false;
    if (ok) {
        DWORD written=0;
        ok=WriteFile(file,data,(DWORD)strlen(data),&written,NULL)&&written==strlen(data)&&FlushFileBuffers(file);
        if (!CloseHandle(file)) ok=false;
    }
    if (ok) ok=MoveFileExA(unique,path,MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)!=0;
    if (*unique) DeleteFileA(unique);
    free(data);
#else
    int fd=mkstemp(temp); bool ok=fd>=0;
    size_t size=strlen(data),offset=0;
    while (ok&&offset<size) {
        ssize_t n=write(fd,data+offset,size-offset);
        if (n<0&&errno==EINTR) continue;
        if (n<=0) ok=false; else offset+=(size_t)n;
    }
    if (fd>=0) { if (fsync(fd)<0) ok=false; if (close(fd)<0) ok=false; }
    if (ok&&rename(temp,path)<0) ok=false;
    int directory=open(c->state_dir,O_RDONLY|O_DIRECTORY|O_CLOEXEC);
    if (ok&&(directory<0||fsync(directory)<0)) ok=false;
    if (directory>=0) close(directory);
    unlink(temp); free(data);
#endif
    if (!ok) c->storage_failed=true;
    return ok;
}
static cJSON *record(struct controller *c,const char *id)
{
    cJSON *records=field(c->saved,"devices"),*r=field(records,id);
    if (!r) { r=cJSON_CreateObject(); cJSON_AddItemToObject(records,id,r); }
    return r;
}
static const cJSON *legacy_stop(struct controller *c,const char *id)
{
    const cJSON *item;
    cJSON_ArrayForEach(item,field(c->legacy_stops,"stops"))
        if (!strcmp(text_field(item,"device_id"),id)) return item;
    return NULL;
}
static bool blocked(struct controller *c,const char *id)
{
    cJSON *r=record(c,id); const cJSON *old=legacy_stop(c,id);
    return yes(r,"blocked") || (old&&strcmp(text_field(r,"legacy_ack"),text_field(old,"at")));
}
static bool reset_pending(struct controller *c,const char *id)
{
    cJSON *r=record(c,id); const cJSON *old=legacy_stop(c,id);
    return yes(r,"reset_pending")||(old&&strcmp(text_field(r,"legacy_ack"),text_field(old,"at"))&&!strcmp(text_field(old,"source"),"reset_intent"));
}
static bool latch(struct controller *c,const char *id,const char *reason,bool pending)
{
    cJSON *r=record(c,id); flag(r,"blocked",true); flag(r,"reset_pending",pending); str(r,"reason",reason);
    return persist(c);
}
static bool clear_latch(struct controller *c,const char *id)
{
    cJSON *r=record(c,id); const cJSON *old=legacy_stop(c,id);
    flag(r,"blocked",false); flag(r,"reset_pending",false); str(r,"reason","");
    flag(r,"stop_event_active",false);
    if (old) str(r,"legacy_ack",text_field(old,"at"));
    return persist(c);
}
static void message(struct controller *c,const char *phase,const char *format,...)
{
    snprintf(c->phase,sizeof c->phase,"%s",phase);
    va_list args; va_start(args,format); vsnprintf(c->message,sizeof c->message,format,args); va_end(args);
    fprintf(stderr,"%s: %s\n",phase,c->message);
}
/* These are feedback states, not permission to send ctrl_mode=2 (which is not
   a supported command in the current vendor SDK). The physical teach button
   retains priority. Read-only scene tracking works in every fresh state. */
static const char *teaching_phase(const struct piper_sample *s)
{
    if (!s->status_seen) return "unknown";
    if (s->teach==1) return "hand-guiding";
    if (s->teach==3||s->teach==5||s->teach==7) return "playback";
    if (s->teach==4) return "playback-paused";
    if (s->teach>7) return "unknown";
    return s->control==2?"ended":"off";
}
static bool teaching_active(const struct piper_sample *s)
{
    return s->status_seen&&(s->teach==1||s->teach==3||s->teach==4||s->teach==5||s->teach==7);
}
static bool teaching_status(const struct piper_sample *s)
{
    return (s->teach==1&&s->arm_status==11)||((s->teach==3||s->teach==5||s->teach==7)&&s->arm_status==12)||
        (s->teach==4&&s->arm_status==13);
}
static bool teaching_benign(const struct can_device *d,double now)
{
    const struct piper_sample *s=&d->feedback.arm[0];
    if (!d->present||!d->up||(!d->simulated&&d->fd<0)||!d->stable||!d->link_verified||d->bitrate!=1000000||
        d->bus_state>=CAN_STATE_BUS_OFF||(d->control_flags&(CAN_CTRLMODE_LOOPBACK|CAN_CTRLMODE_LISTENONLY|CAN_CTRLMODE_FD))||
        !piper_fresh(s,now)||s->error||!teaching_active(s)) return false;
    if (s->arm_status&&!teaching_status(s)) return false;
    if (d->feedback.foreign_seen&&now-d->feedback.foreign_at<2) return false;
    for (unsigned i=1;i<3;++i) if (d->feedback.arm[i].status_seen&&piper_recent(d->feedback.arm[i].status_at,now)) return false;
    for (unsigned i=0;i<6;++i) if (!d->feedback.motor[i].seen||!piper_recent(d->feedback.motor[i].at,now)||d->feedback.motor[i].fault) return false;
    return true;
}
enum health_check { CHECK_CONTROL, CHECK_POWER, CHECK_RESET, CHECK_TEACH_RETURN };
static const char *health(struct can_device *d,double now,enum health_check check)
{
    if (!d) return "Select an arm.";
    if (!d->present) return "USB adapter is missing.";
    if (!d->up||(!d->simulated&&d->fd<0)) return "CAN socket unavailable. Check interface state and bitrate.";
    if (!d->stable) return "A unique USB adapter identity is required for control.";
    if (!d->link_verified||d->bitrate!=1000000) return "CAN link must be verified at 1 Mbit/s before control.";
    if (d->bus_state>=CAN_STATE_BUS_OFF||(d->control_flags&(CAN_CTRLMODE_LOOPBACK|CAN_CTRLMODE_LISTENONLY|CAN_CTRLMODE_FD))) return "CAN controller is bus-off or configured in an unsupported mode.";
    struct piper_feedback *f=&d->feedback; struct piper_sample *s=&f->arm[0];
    if (!piper_fresh(s,now)) return "Waiting for fresh controller and all six joint readings.";
    for (unsigned i=1;i<3;++i) if (f->arm[i].status_seen&&piper_recent(f->arm[i].status_at,now)) return "Multiple arm address ranges detected; one standalone arm per adapter is required.";
    if (f->foreign_seen&&now-f->foreign_at<2) return "Another controller is sending commands on this CAN bus.";
    /* During a requested reset, observe transient MCU status without sending
       another stop into the reset sequence. All transport/driver checks remain
       active, and completion below still requires zero error and normal status. */
    if (check!=CHECK_RESET&&(s->error||(!teaching_status(s)&&(((check==CHECK_CONTROL||check==CHECK_TEACH_RETURN)&&s->arm_status)||s->arm_status>1)))) {
        if (s->arm_status==5||(s->error&0x3f)) return "Robot reports a joint-drive communication fault. Motion stopped; inspect the saved fault before recovery.";
        return "The arm reports a fault or stop. Inspect status and recover before control.";
    }
    for (unsigned i=0;i<6;++i) {
        if (!isfinite(s->deg[i])||fabs(s->deg[i])>360) return "Joint feedback is outside the supported physical range.";
        if (!f->motor[i].seen||!piper_recent(f->motor[i].at,now)) return "Waiting for fresh feedback from all six motor drivers.";
        if (f->motor[i].fault) return "A motor driver reports a fault.";
        if ((check==CHECK_CONTROL||check==CHECK_TEACH_RETURN) && !d->simulated) {
            struct piper_motor *m=&f->motor[i];
            if (!m->high_seen || !piper_recent(m->high_at,now)) return "Waiting for fresh motor encoder feedback.";
            double encoder_deg=m->position_raw*180.0/(1000.0*3.141592653589793);
            if (fabs(encoder_deg-s->deg[i])>2) return "Joint feedback disagrees with motor encoders. Waiting for the actual measured pose.";
        }
    }
    if (!*f->hardware||!*f->version) return "Firmware has not been read on this connection. Click Read firmware & limits.";
    if (!piper_supported(f)) return "The reported hardware/firmware is not supported for motion. See the detected versions in Arm connection.";
    if (check==CHECK_CONTROL&&teaching_active(s)) return "Onboard teaching is active. Move the arm by hand while the scene follows; dashboard motion is paused.";
    if (check==CHECK_CONTROL&&s->control==2) return "Teaching ended with the controller still in teach mode. Return to physical zero, turn the teach light off, then use Return to CAN control.";
    if (check==CHECK_CONTROL&&(s->control>1||(s->teach!=0&&s->teach!=2&&s->teach!=6)||(s->motion_mode!=1&&!(s->control==0&&s->motion_mode==0)))) return "Arm must be in standby/CAN MoveJ mode with teaching inactive.";
    return NULL;
}
/* These are static configuration replies, not streaming telemetry. They
   remain valid for this uninterrupted connection instead of expiring on a
   timer. Transport loss, a status-stream gap or a manual query clears them. */
static bool configuration_current(struct can_device *d,bool seen,double at,double now)
{
    return seen && at>=d->configuration_since && at<=now;
}
static bool arm_configuration_received(struct can_device *d,double now)
{
    if (!*d->feedback.hardware || !*d->feedback.version) return false;
    for (unsigned i=0;i<6;++i) {
        struct piper_limit *l=&d->feedback.limit[i];
        struct piper_acceleration *a=&d->feedback.acceleration[i];
        if (!configuration_current(d,l->seen,l->at,now) ||
            !configuration_current(d,a->seen,a->at,now)) return false;
    }
    return true;
}
const char *controller_blocker(struct controller *c,struct can_device *d,double now)
{
    const char *why=health(d,now,false); if (why) return why;
    if (c->storage_failed) return "Control-state storage failed. Repair storage and restart; motion remains blocked.";
    if (blocked(c,d->id)) return "Stopped earlier. Click Enable controls to verify the current robot state.";
    for (unsigned i=0;i<6;++i) {
        struct piper_limit *l=&d->feedback.limit[i];
        struct piper_acceleration *a=&d->feedback.acceleration[i];
        if (!configuration_current(d,l->seen,l->at,now)) return "Reading joint limits for this connection.";
        if (!isfinite(l->min)||!isfinite(l->max)||l->min>=l->max||l->min < -360||l->max>360||l->speed<=0||l->speed>3)
            return "Controller returned invalid joint limits or maximum speed.";
        if (!configuration_current(d,a->seen,a->at,now)) return "Reading acceleration limits for this connection.";
        if (!isfinite(a->max)||a->max<=0||a->max>5) return "An acceleration limit is outside the supported 0–5 rad/s² range. Inspect firmware settings before motion.";
    }
    return NULL;
}
/* Encoder drift at a limit does not require a separate alignment action.
   This allowance applies only to feedback; every sent target stays in range. */
static const double limit_feedback_slop_deg=.1;
static double range_target(struct can_device *d,unsigned i)
{
    struct piper_limit *l=&d->feedback.limit[i]; double angle=d->feedback.arm[0].deg[i];
    double margin=fmin(.5,(l->max-l->min)/2);
    return angle<l->min?l->min+margin:angle>l->max?l->max-margin:angle;
}
static double range_distance(struct can_device *d)
{
    double distance=0;
    for (unsigned i=0;i<6;++i) {
        struct piper_limit *l=&d->feedback.limit[i]; double angle=d->feedback.arm[0].deg[i];
        if (!l->seen) return NAN;
        if (angle>=l->min-limit_feedback_slop_deg && angle<=l->max+limit_feedback_slop_deg) continue;
        distance=fmax(distance,fabs(range_target(d,i)-angle));
    }
    return distance;
}
static bool joint_outside_range(const struct can_device *d,unsigned i)
{
    const struct piper_limit *l=&d->feedback.limit[i];
    double angle=d->feedback.arm[0].deg[i];
    return angle<l->min-limit_feedback_slop_deg||angle>l->max+limit_feedback_slop_deg;
}
static unsigned outside_range_count(const struct can_device *d)
{
    unsigned count=0;
    for (unsigned i=0;i<6;++i) if (joint_outside_range(d,i)) ++count;
    return count;
}
static const char *range_reason(struct controller *c,struct can_device *d)
{
    for (unsigned i=0;i<6;++i) if (joint_outside_range(d,i)) {
        snprintf(c->error,sizeof c->error,"J%u reads %.3f degrees outside %.3f to %.3f. Move that joint inward or explicitly move all joints to zero; every CAN position command contains all six joints.",i+1,d->feedback.arm[0].deg[i],d->feedback.limit[i].min,d->feedback.limit[i].max);
        return c->error;
    }
    return "No range correction is required.";
}
static bool valid_target(struct controller *c,struct can_device *d,const double targets[6])
{
    for (unsigned i=0;i<6;++i) {
        struct piper_limit *l=&d->feedback.limit[i];
        if (!isfinite(targets[i])||targets[i]<l->min||targets[i]>l->max) {
            snprintf(c->error,sizeof c->error,"J%u target %.3f° is outside %.1f° to %.1f°.",i+1,targets[i],l->min,l->max); return false;
        }
    }
    return true;
}
static bool enabled(struct can_device *d,bool wanted,double after)
{
    for (unsigned i=0;i<6;++i) {
        struct piper_motor *m=&d->feedback.motor[i];
        if (!m->seen||m->enabled!=wanted||m->at<=after) return false;
    }
    return true;
}
static bool measured_stationary(struct can_device *d,double now,double after)
{
    if (!d->stationary_since||now-d->stationary_since<.4) return false;
    for (unsigned i=0;i<6;++i) {
        struct piper_motor *m=&d->feedback.motor[i];
        if (!m->high_seen||m->high_at<=after||!piper_recent(m->high_at,now)||!isfinite(m->velocity)||fabs(m->velocity)>.01) return false;
    }
    return true;
}
static bool measured_through(struct can_device *d,double endpoint)
{
    struct piper_sample *s=&d->feedback.arm[0];
    if (s->status_at<endpoint) return false;
    for (unsigned i=0;i<6;++i) if (s->joint_at[i]<endpoint||d->feedback.motor[i].high_at<endpoint) return false;
    return true;
}
static bool teaching_zero(const struct can_device *d)
{
    for (unsigned i=0;i<6;++i) if (!isfinite(d->feedback.arm[0].deg[i])||fabs(d->feedback.arm[0].deg[i])>1) return false;
    return true;
}
static const char *teaching_return_guard(struct controller *c,struct can_device *d,double now)
{
    if (c->storage_failed) return "Control-state storage failed; repair storage before teaching handoff.";
    const char *why=health(d,now,CHECK_TEACH_RETURN); if (why) return why;
    struct piper_sample *s=&d->feedback.arm[0];
    if (s->teach!=0&&s->teach!=2&&s->teach!=6) return "Turn off the physical teach button and finish onboard teaching/playback before returning to CAN.";
    if (s->control>2||s->motion_mode>1) return "Only a completed onboard teaching state can return to CAN through this action.";
    if (blocked(c,d->id)||reset_pending(c,d->id)) return "A recorded stop/reset still requires its separate recovery flow before teaching handoff.";
    if (!arm_configuration_received(d,now)) return "Read current firmware and joint limits before returning to CAN.";
    for (unsigned i=0;i<6;++i) {
        const struct piper_limit *l=&d->feedback.limit[i]; const struct piper_acceleration *a=&d->feedback.acceleration[i];
        if (!isfinite(l->min)||!isfinite(l->max)||l->min>=l->max||l->min < -360||l->max>360||!isfinite(l->speed)||l->speed<=0||l->speed>3||
            !isfinite(a->max)||a->max<=0||a->max>5) return "Robot configuration is invalid; inspect it before teaching handoff.";
    }
    if (!teaching_zero(d)) return "Return every physical joint to its zero pose before switching from teaching to CAN (application guard: within 1 degree).";
    if (range_distance(d)>0) return "A joint is outside its reported command range; reposition it by hand before returning to CAN.";
    if (!enabled(d,true,0)&&!enabled(d,false,0)) return "Wait for a consistent measured motor power state before teaching handoff.";
    /* Establish stillness before the first packet. During the bounded mode
       exchange, fresh zero/range feedback and the 0.5-degree initial-pose guard
       permit minor supported settling without restarting this timer. */
    if (c->op!=OP_TEACH_RETURN&&(!d->stationary_since||now-d->stationary_since<.4)) return "Keep the zero pose stationary for 0.4 seconds before teaching handoff.";
    return NULL;
}
/* A hardware button may preempt a leased move. Do not fight that button with a
   STOP/reset or preserve an old target for later replay. Only this operation's
   ordinary interrupted-action intent can be cleared; real incidents stay. */
static bool relinquish_to_teaching(struct controller *c,struct can_device *d,double now)
{
    if (c->op==OP_IDLE||c->op==OP_TEACH_RETURN||!d||d->generation!=c->generation||!teaching_benign(d,now)) return false;
    cJSON *r=record(c,d->id);
    bool own_intent=!strcmp(text_field(r,"reason"),"Control action interrupted before verified completion")&&
        !yes(r,"stop_event_active")&&!reset_pending(c,d->id);
    c->op=OP_IDLE; c->cycle_stage=0; c->owner[0]=0; c->following=c->follow_committed=false; c->range_followup=c->arrival_hold=false;
    c->teach_return_stage=0;
    if (own_intent&&!clear_latch(c,d->id)) message(c,"blocked","Teaching took over, but state storage failed; previous target discarded.");
    else message(c,"teaching","Onboard teaching took control. The previous movement and lease were discarded; measured scene tracking continues.");
    return true;
}
static void abandon_teaching_return(struct controller *c,const char *reason)
{
    c->op=OP_IDLE; c->owner[0]=0; c->teach_return_stage=0; c->following=c->follow_committed=false;
    message(c,"teaching","Return to CAN cancelled: %s No reset, power or position command was sent.",reason);
}
static bool send_pose(struct controller *c,struct can_device *d,const double target[6])
{
    struct can_frame f[4]; f[0]=piper_mode(c->op==OP_ALIGN||c->arrival_hold?1:(unsigned)fmax(1,ceil(c->speed_percent)));
    return piper_targets(target,f+1)&&can_bus_send(&c->bus,d,c->generation,f,4);
}
static bool send_power(struct controller *c,struct can_device *d,bool on)
{
    struct can_frame f[6]; for (unsigned i=0;i<6;++i) f[i]=piper_power(i+1,on);
    return can_bus_send(&c->bus,d,c->generation,f,6);
}
void controller_stop(struct controller *c,const char *reason)
{
    struct can_device *d=can_bus_find(&c->bus,c->selected);
    bool had_job=c->op!=OP_IDLE, sent=false;
    unsigned generation=had_job?c->generation:(d?d->generation:0);
    if (d) {
        cJSON *r=record(c,d->id);
        if (!yes(r,"stop_event_active")) {
            cJSON_DeleteItemFromObjectCaseSensitive(r,"last_fault");
            cJSON *event=cJSON_CreateObject(), *joints=cJSON_CreateArray();
            str(event,"reason",reason); num(event,"unix_time",(double)time(NULL));
            num(event,"joint",c->obstruction_joint); num(event,"threshold_s",.5); num(event,"tolerance_deg",c->tolerance);
            num(event,"request_id",c->request_id); num(event,"speed_deg_s",c->speed);
            flag(event,"following",c->following); flag(event,"finishing_target",c->follow_committed);
            num(event,"follow_sequence",c->follow_sequence);
            num(event,"arm_status",d->feedback.arm[0].arm_status);
            num(event,"error",d->feedback.arm[0].error);
            for (unsigned i=0;i<6;++i) {
                cJSON *j=cJSON_CreateObject(); num(j,"joint",i+1);
                num(j,"actual_deg",d->feedback.arm[0].deg[i]);
                num(j,"command_deg",had_job?c->command[i]:NAN);
                num(j,"target_deg",had_job?c->target[i]:NAN);
                num(j,"current_a",d->feedback.motor[i].high_seen&&piper_recent(d->feedback.motor[i].high_at,piper_now())?d->feedback.motor[i].current:NAN);
                num(j,"driver_fault",d->feedback.motor[i].fault);
                num(j,"no_progress_s",had_job?piper_now()-c->obstruction[i].progress_at:NAN);
                num(j,"progress_reference_deg",had_job?c->obstruction[i].progress_position:NAN);
                cJSON_AddItemToArray(joints,j);
            }
            put(event,"joints",joints); put(r,"stop_event",event); flag(r,"stop_event_active",true);
        }
        /* Save the robot's fault before a stop changes its reported state. */
        struct piper_sample *s=&d->feedback.arm[0];
        if (!field(r,"last_fault")&&s->status_seen&&piper_recent(s->status_at,piper_now())&&(s->arm_status||s->error)) {
            cJSON *fault=cJSON_CreateObject(); num(fault,"arm_status",s->arm_status);
            num(fault,"error",s->error); num(fault,"teach_state",s->teach);
            num(fault,"control",s->control); num(fault,"observed_monotonic_s",s->status_at);
            put(record(c,d->id),"last_fault",fault);
        }
        struct can_frame f=piper_stop_frame();
        sent=can_bus_send(&c->bus,d,generation,&f,1);
        if (c->op==OP_GRIPPER) { piper_gripper(0,.1,false,&f); sent=can_bus_send(&c->bus,d,generation,&f,1)&&sent; }
        latch(c,d->id,reason,reset_pending(c,d->id));
    }
    c->op=OP_IDLE; c->cycle_stage=0; c->owner[0]=0; c->query=false; c->range_followup=c->arrival_hold=false;
    message(c,"stopped","%s %s",reason,sent?"Stop queued; verify the arm has stopped. This is not a power cut.":"Stop delivery unavailable; physical stop may be needed.");
}
static void finish(struct controller *c,struct can_device *d,const char *result,bool clear)
{
    c->op=OP_IDLE; c->cycle_stage=0; c->owner[0]=0; c->range_followup=c->arrival_hold=false;
    if (clear&&!clear_latch(c,d->id)) message(c,"blocked","Action completed, but state could not be saved. Motion remains blocked.");
    else message(c,"complete","%s",result);
}
static struct can_device *remember_device(struct controller *c,const char *id,const char *name)
{
    if (!*id||strlen(id)>=sizeof c->selected) return NULL;
    struct can_device *d=can_bus_find(&c->bus,id);
    if (!d) for (unsigned i=0;i<CANBUS_MAX;++i) if (!*c->bus.devices[i].id) {
        d=&c->bus.devices[i];
        snprintf(d->id,sizeof d->id,"%s",id);
        snprintf(d->problem,sizeof d->problem,"USB adapter missing");
        break;
    }
    if (d&&name&&*name) snprintf(d->label,sizeof d->label,"%s",name);
    return d;
}
bool controller_init(struct controller *c,bool simulation,const char *directory,const char *legacy)
{
    memset(c,0,sizeof *c); can_bus_init(&c->bus,simulation);
    if (strlen(directory)>=sizeof c->state_dir||strlen(legacy)>=sizeof c->legacy_dir) return false;
    snprintf(c->state_dir,sizeof c->state_dir,"%s",directory); snprintf(c->legacy_dir,sizeof c->legacy_dir,"%s",legacy);
#ifdef _WIN32
    if (_mkdir(directory)<0&&errno!=EEXIST) return false;
#else
    if (mkdir(directory,0700)<0&&errno!=EEXIST) return false;
#endif
    bool bad=false; c->saved=read_json(directory,"control-state.json",&bad);
    if (bad) return false;
    if (!c->saved) { c->saved=cJSON_CreateObject(); num(c->saved,"schema",1); flag(c->saved,"simulated",simulation); put(c->saved,"devices",cJSON_CreateObject()); }
    if (!cJSON_IsBool(field(c->saved,"simulated"))||yes(c->saved,"simulated")!=simulation) return false;
    if (!cJSON_IsObject(field(c->saved,"devices"))||!cJSON_IsNumber(field(c->saved,"schema"))||field(c->saved,"schema")->valuedouble!=1) return false;
    cJSON *saved_record;
    cJSON_ArrayForEach(saved_record,field(c->saved,"devices")) {
        if (!cJSON_IsObject(saved_record)) return false;
        const char *keys[]={"blocked","reset_pending"};
        for (unsigned i=0;i<2;++i) if (field(saved_record,keys[i])&&!cJSON_IsBool(field(saved_record,keys[i]))) return false;
    }
    if (!simulation&&*legacy) {
        c->profiles=read_json(legacy,"calibrations.json",&bad);
        c->legacy_stops=read_json(legacy,"motion-stops.json",&bad);
        if (bad||(c->legacy_stops&&!cJSON_IsArray(field(c->legacy_stops,"stops")))) return false;
        cJSON *stop;
        cJSON_ArrayForEach(stop,field(c->legacy_stops,"stops"))
            if (!*text_field(stop,"device_id")||!*text_field(stop,"at")||!*text_field(stop,"reason")) return false;
    }
    if (field(c->saved,"selected")&&!cJSON_IsString(field(c->saved,"selected"))) return false;
    const char *saved_selection=text_field(c->saved,"selected");
    if (strlen(saved_selection)>=sizeof c->selected) return false;
    snprintf(c->selected,sizeof c->selected,"%s",saved_selection);
    /* Remember absent adapters before discovery. A restart must never silently
       substitute a different physical arm for the selected missing one. */
    cJSON *profile;
    cJSON_ArrayForEach(profile,field(c->profiles,"slots")) {
        struct can_device *d=remember_device(c,text_field(profile,"device_id"),text_field(profile,"name"));
        if (d&&!*c->selected) snprintf(c->selected,sizeof c->selected,"%s",d->id);
    }
    cJSON_ArrayForEach(saved_record,field(c->saved,"devices"))
        remember_device(c,saved_record->string,text_field(saved_record,"name"));
    if (*c->selected&&!remember_device(c,c->selected,NULL)) return false;
    can_bus_scan(&c->bus);
    if (!*c->selected) for (unsigned i=0;i<CANBUS_MAX;++i) if (c->bus.devices[i].id[0]) { snprintf(c->selected,sizeof c->selected,"%s",c->bus.devices[i].id); break; }
    for (unsigned i=0;i<CANBUS_MAX;++i) {
        struct can_device *d=&c->bus.devices[i];
        if (*d->id) str(record(c,d->id),"name",*d->label?d->label:d->name);
    }
    str(c->saved,"selected",c->selected);
    message(c,"monitor","Monitoring only. Read firmware and limits, then choose a control action.");
    return persist(c);
}
void controller_close(struct controller *c)
{
    relinquish_to_teaching(c,can_bus_find(&c->bus,c->selected),piper_now());
    if (c->op==OP_TEACH_RETURN) abandon_teaching_return(c,"Controller closing.");
    if (c->op!=OP_IDLE) controller_stop(c,"Controller shutting down.");
    can_bus_close(&c->bus); cJSON_Delete(c->saved); cJSON_Delete(c->profiles); cJSON_Delete(c->legacy_stops);
}
static bool numeric(const cJSON *body,const char *key,double lo,double hi,double *out)
{
    cJSON *item=field(body,key);
    if (!cJSON_IsNumber(item)||!isfinite(item->valuedouble)||item->valuedouble<lo||item->valuedouble>hi) return false;
    *out=item->valuedouble; return true;
}
static cJSON *reply(int *status,int code,const char *error)
{
    *status=code; cJSON *r=cJSON_CreateObject(); flag(r,"ok",code<400); str(r,code<400?"message":"error",error); return r;
}
static void begin(struct controller *c,struct can_device *d,const char *owner,enum operation op,double now)
{
    c->obstruction_joint=0;
    for (unsigned i=0;i<6;++i) obstruction_reset(&c->obstruction[i],now);
    c->op=op; c->generation=d->generation; c->started=c->heartbeat=c->last_tick=c->progress_at=now;
    c->sent_at=c->settled_at=c->final_sent_at=0; c->best_error=1e9; c->enable_count=0; ++c->request_id;
    c->cycle_stage=0;
    c->teach_return_stage=0;
    c->synchronized_pose=false;
    c->range_followup=false;
    c->arrival_hold=false; c->arrival_hold_at=0;
    c->following=c->follow_committed=false; c->follow_updated=0; c->follow_sequence=0;
    snprintf(c->owner,sizeof c->owner,"%s",owner);
    memcpy(c->initial,d->feedback.arm[0].deg,sizeof c->initial);
    memcpy(c->command,c->initial,sizeof c->command);
}
static void cycle_leg(struct controller *c,struct can_device *d,double now)
{
    c->arrival_hold=false; c->arrival_hold_at=0;
    for (unsigned i=0;i<6;++i) obstruction_reset(&c->obstruction[i],now);
    memcpy(c->leg_start,d->feedback.arm[0].deg,sizeof c->leg_start);
    memcpy(c->command,c->leg_start,sizeof c->command);
    c->leg_span=c->leg_fraction=0;
    for (unsigned i=0;i<6;++i) c->leg_span=fmax(c->leg_span,fabs(c->target[i]-c->leg_start[i]));
    c->settled_at=c->final_sent_at=c->sent_at=0;
    c->best_error=1e9; c->progress_at=c->last_tick=now;
}
static const char *start_query(struct controller *c,struct can_device *d,double now)
{
    if (!d||!d->present||!d->up||(!d->simulated&&d->fd<0)) return "CAN interface is unavailable.";
    struct can_frame f[2]={{.can_id=0x4af,.len=8,.data={1}},
        {.can_id=0x477,.len=8,.data={4,0,0,0,3,0,0,0}}};
    can_bus_invalidate_configuration(d,now);
    if (!can_bus_send(&c->bus,d,d->generation,f,2)) return "Information requests could not be queued.";
    c->query=true; c->query_generation=d->generation; c->query_joint=0;
    c->query_start=now; c->query_next=now+.1; c->auto_query_at=now;
    message(c,"querying","Reading firmware, gripper configuration and joint limits. Waiting for robot replies.");
    return NULL;
}
static bool follow_hold(struct controller *c,struct can_device *d,double now,const char *reason)
{
    const char *why=health(d,now,CHECK_CONTROL);
    if (why||!enabled(d,true,0)||d->generation!=c->generation) {
        controller_stop(c,why?why:"Motor enable or adapter changed during following."); return false;
    }
    memcpy(c->target,d->feedback.arm[0].deg,sizeof c->target);
    for (unsigned i=0;i<6;++i) {
        struct piper_limit *l=&d->feedback.limit[i];
        if (c->target[i]>=l->min-limit_feedback_slop_deg&&c->target[i]<=l->max+limit_feedback_slop_deg)
            c->target[i]=fmin(l->max,fmax(l->min,c->target[i]));
    }
    if (!valid_target(c,d,c->target)||!send_pose(c,d,c->target)) {
        controller_stop(c,"Could not hold a verified measured pose after following."); return false;
    }
    memcpy(c->command,c->target,sizeof c->command);
    c->following=c->follow_committed=false;
    finish(c,d,reason,true);
    return true;
}
cJSON *controller_action(struct controller *c,const char *route,const cJSON *body,int *status)
{
    double now=piper_now(); struct can_device *d=can_bus_find(&c->bus,c->selected);
    if (!strcmp(route,"/api/stop")) {
        if (*text_field(body,"device_id")&&(!d||strcmp(text_field(body,"device_id"),d->id))) return reply(status,409,"Stop belongs to a different selected adapter.");
        if (field(body,"generation")) {
            double generation;
            if (!d||!numeric(body,"generation",1,UINT32_MAX,&generation)||generation!=d->generation)
                return reply(status,409,"Stop belongs to a different CAN connection generation.");
        }
    }
    relinquish_to_teaching(c,d,now);
    if (!strcmp(route,"/api/stop")) {
        if (field(body,"request_id")) {
            double request;
            if (c->op==OP_IDLE||strcmp(c->owner,text_field(body,"owner"))||!numeric(body,"request_id",1,UINT32_MAX,&request)||request!=c->request_id)
                return reply(status,409,"No matching active action for this automatic stop.");
        }
        controller_stop(c,"Operator stop."); return reply(status,200,c->message);
    }
    if (!strcmp(route,"/api/heartbeat")) {
        double request;
        if (c->op!=OP_IDLE&&now-c->heartbeat>=.8) {
            if (c->op==OP_TEACH_RETURN) abandon_teaching_return(c,"Control lease expired.");
            else controller_stop(c,"Control heartbeat expired.");
            return reply(status,409,c->message);
        }
        if (c->op==OP_IDLE||strcmp(c->owner,text_field(body,"owner"))||!numeric(body,"request_id",1,UINT32_MAX,&request)||request!=c->request_id)
            return reply(status,409,"No matching active control lease.");
        c->heartbeat=now; return reply(status,200,"Control lease renewed.");
    }
    if (c->op!=OP_IDLE) {
        if (!strcmp(route,"/api/command")&&!strcmp(text_field(body,"action"),"hold")&&c->op==OP_ALIGN&&!strcmp(c->owner,text_field(body,"owner"))) {
            double request,generation;
            if (!d||strcmp(d->id,text_field(body,"device_id"))||
                !numeric(body,"generation",1,UINT32_MAX,&generation)||generation!=c->generation||d->generation!=c->generation||
                !numeric(body,"request_id",1,UINT32_MAX,&request)||request!=c->request_id)
                return reply(status,409,"Range-entry cancellation belongs to another controller action or CAN connection.");
            /* A full measured pose outside its limits cannot be encoded as a
               legal hold target. Cancel the pending destination with STOP. */
            controller_stop(c,"Operator cancelled range entry; the pending destination was discarded.");
            return reply(status,200,c->message);
        }
        if (!strcmp(route,"/api/command")&&(c->following||c->follow_committed)&&c->op==OP_MOVE&&
            (!strcmp(text_field(body,"action"),"follow-update")||!strcmp(text_field(body,"action"),"follow-commit")||!strcmp(text_field(body,"action"),"follow-end"))) {
            double request,generation,sequence;
            if (!d||strcmp(c->owner,text_field(body,"owner"))||strcmp(d->id,text_field(body,"device_id"))||
                !numeric(body,"request_id",1,UINT32_MAX,&request)||request!=c->request_id||
                !numeric(body,"generation",1,UINT32_MAX,&generation)||generation!=c->generation||d->generation!=c->generation)
                return reply(status,409,"Following session no longer matches this controller.");
            if (c->following&&now-c->follow_updated>=.25) {
                follow_hold(c,d,now,"Following input expired after 250 ms. Holding measured pose; no target replay.");
                return reply(status,409,c->message);
            }
            if (!strcmp(text_field(body,"action"),"follow-end")) {
                bool held=follow_hold(c,d,now,"Movement stopped. Holding measured pose; motors remain enabled.");
                return reply(status,held?200:409,c->message);
            }
            if (!c->following) return reply(status,409,"This drag was already released. Start a new gesture to change its target.");
            if (!numeric(body,"sequence",1,UINT32_MAX,&sequence)||sequence!=floor(sequence)||sequence<=c->follow_sequence)
                return reply(status,409,"Discarded an old following target.");
            const cJSON *angles=field(body,"degrees"); double target[6];
            if (!cJSON_IsArray(angles)||cJSON_GetArraySize(angles)!=6) return reply(status,400,"Following requires six finite joint angles.");
            for (unsigned i=0;i<6;++i) {
                const cJSON *n=cJSON_GetArrayItem(angles,(int)i);
                if (!cJSON_IsNumber(n)||!isfinite(n->valuedouble)) return reply(status,400,"Following angles must be finite.");
                target[i]=n->valuedouble;
            }
            const char *why=health(d,now,CHECK_CONTROL);
            if (why||!enabled(d,true,0)) return reply(status,409,why?why:"All six motors must remain enabled.");
            if (!valid_target(c,d,target)) return reply(status,400,c->error);
            memcpy(c->target,target,sizeof target); c->follow_sequence=(unsigned)sequence;
            c->follow_updated=c->heartbeat=now;
            if (!strcmp(text_field(body,"action"),"follow-commit")) {
                c->following=false; c->follow_committed=true;
                c->started=c->progress_at=now; c->best_error=1e9;
                c->settled_at=c->final_sent_at=0;
                message(c,"moving","Movement in progress to released target. Stop movement holds the measured pose.");
                return reply(status,202,c->message);
            }
            return reply(status,202,"Latest following target accepted.");
        }
        if (!strcmp(route,"/api/command")&&!strcmp(text_field(body,"action"),"hold")&&c->op==OP_GRIPPER&&!strcmp(c->owner,text_field(body,"owner"))) {
            const char *why=health(d,now,CHECK_CONTROL); if (why) return reply(status,409,why);
            if (d->generation!=c->generation||!enabled(d,true,0)||!d->feedback.gripper_enabled) return reply(status,409,"Connection or motor enable changed during gripper motion.");
            if (now-c->heartbeat>=.8||!d->feedback.gripper_seen||!piper_recent(d->feedback.gripper_at,now)||d->feedback.gripper_fault) return reply(status,409,"Fresh gripper feedback and an active lease are required to hold.");
            if (!c->sent_at) return reply(status,409,"No gripper target has been transmitted yet.");
            finish(c,d,"Gripper ramp stopped at its last transmitted target and effort limit. Contact or a secure grasp is not verified.",true);
            return reply(status,200,c->message);
        }
        if (!strcmp(route,"/api/command")&&!strcmp(text_field(body,"action"),"hold")&&(c->op==OP_MOVE||c->op==OP_CYCLE)&&!strcmp(c->owner,text_field(body,"owner"))) {
            double request;
            if (field(body,"request_id")&&(!numeric(body,"request_id",1,UINT32_MAX,&request)||request!=c->request_id))
                return reply(status,409,"Hold belongs to an older movement.");
            if (c->following||c->follow_committed) {
                bool held=follow_hold(c,d,now,"Following paused. Holding measured pose; motors remain enabled.");
                return reply(status,held?200:409,c->message);
            }
            const char *why=health(d,now,false); if (why) return reply(status,409,why);
            c->arrival_hold=false;
            memcpy(c->target,d->feedback.arm[0].deg,sizeof c->target); memcpy(c->command,c->target,sizeof c->command);
            bool was_cycle=c->op==OP_CYCLE; c->op=OP_MOVE; c->cycle_stage=0;
            c->synchronized_pose=false;
            if (was_cycle) { c->started=c->progress_at=now; c->best_error=1e9; }
            c->settled_at=c->final_sent_at=0; c->heartbeat=now;
            message(c,"holding","%s",was_cycle?"Upright/return sequence cancelled. Holding here; no automatic return will run.":"Holding the current measured pose.");
            return reply(status,202,c->message);
        }
        return reply(status,409,"Another control action is active. Hold or stop it first.");
    }
    if (!strcmp(route,"/api/select")) {
        d=can_bus_find(&c->bus,text_field(body,"device_id"));
        if (!d) return reply(status,404,"Adapter not found.");
        str(c->saved,"selected",d->id);
        str(record(c,d->id),"name",*d->label?d->label:d->name);
        if (!persist(c)) return reply(status,500,"Adapter selection could not be saved; control is blocked.");
        c->query=false; snprintf(c->selected,sizeof c->selected,"%s",d->id);
        message(c,"monitor","Selected %s. No motor command sent.",*d->label?d->label:d->name);
        return reply(status,200,c->message);
    }
    if (!strcmp(route,"/api/reconnect")) {
        c->query=false; can_bus_reconnect(&c->bus);
        /* Reopen invalidates identity/limit feedback. Refresh it with bounded
           information reads on the selected adapter only; never resume motion. */
        const char *why=start_query(c,can_bus_find(&c->bus,c->selected),now);
        if (why) message(c,"monitor","CAN sockets reopened. %s Read firmware & limits once connected.",why);
        return reply(status,200,c->message);
    }
    if (!d||strcmp(text_field(body,"device_id"),d->id)) return reply(status,409,"Selected adapter changed; refresh before commanding.");
    double generation;
    if (!numeric(body,"generation",1,UINT32_MAX,&generation)||generation!=d->generation) return reply(status,409,"CAN connection changed; refresh before commanding.");
    if (!strcmp(route,"/api/query")) {
        if (c->query) return reply(status,409,"An information query is already active.");
        const char *why=start_query(c,d,now);
        return reply(status,why?409:202,why?why:c->message);
    }
    if (strcmp(route,"/api/command")) return reply(status,404,"Unknown API route.");
    if (c->query) return reply(status,409,"Wait for the information query to complete.");
    const char *action=text_field(body,"action"),*owner=text_field(body,"owner");
    if (strlen(owner)<16||strlen(owner)>80) return reply(status,400,"owner must be a 16–80 character client identifier.");
    if (!yes(body,"standalone")) return reply(status,400,"Confirm that this arm has no linked leader or competing controller.");
    if (c->storage_failed) return reply(status,409,"Persistent control-state storage failed; repair storage and restart.");
    if (!strcmp(action,"teach-return")) {
        if (!yes(body,"button_off")||!yes(body,"zero_pose_confirmed")||!yes(body,"clear")||!yes(body,"supported"))
            return reply(status,400,"Confirm physical zero pose, teach-button light off, a clear workspace and support for the arm during the standby/CAN transition.");
        const struct piper_sample *s=&d->feedback.arm[0];
        if (s->control<=1&&!teaching_active(s)) {
            const char *why=health(d,now,CHECK_CONTROL); if (why) return reply(status,409,why);
            return reply(status,200,"The arm already reports standby/CAN control. No handoff command was sent.");
        }
        if (s->control!=2) return reply(status,409,"This action only returns a completed onboard teaching state to CAN control.");
        const char *why=teaching_return_guard(c,d,now); if (why) return reply(status,409,why);
        begin(c,d,owner,OP_TEACH_RETURN,now); c->teach_return_stage=1; c->sent_at=now;
        struct can_frame frame=piper_mode(1); frame.data[0]=0; /* Documented standby, then CAN; never ctrl_mode=2. */
        if (!can_bus_send(&c->bus,d,c->generation,&frame,1)) {
            abandon_teaching_return(c,"Standby request could not be transmitted."); return reply(status,409,c->message);
        }
        message(c,"teaching-return","Standby requested at verified zero. Waiting for fresh acknowledgement before requesting CAN; keep the arm supported.");
        cJSON *result=reply(status,202,c->message); num(result,"request_id",c->request_id); return result;
    }
    if (!strcmp(action,"verify-current")) {
        if (!yes(body,"clear") || !yes(body,"supported")) return reply(status,400,"Confirm that it is safe to use the current robot state.");
        const char *why=health(d,now,CHECK_CONTROL); if (why) return reply(status,409,why);
        if (reset_pending(c,d->id)) return reply(status,409,"A reset is pending; verify completed reset recovery first.");
        if ((!enabled(d,true,0)&&!enabled(d,false,0)) || !d->stationary_since || now-d->stationary_since<.4)
            return reply(status,409,"Wait for healthy, stationary feedback and a consistent motor power state.");
        if (!clear_latch(c,d->id)) return reply(status,500,"Recovery state could not be saved.");
        message(c,"monitor","Current robot state verified. No motor power or position command was sent.");
        return reply(status,200,c->message);
    }
    if (!strcmp(action,"disable")||!strcmp(action,"reset")||!strcmp(action,"verify-reset")||!strcmp(action,"verify-disabled")) {
        if (!yes(body,"supported")) return reply(status,400,"Physically support the arm before removing motor power or recovering it.");
        const char *why=health(d,now,true); if (why) return reply(status,409,why);
        if (!strcmp(action,"verify-reset")||!strcmp(action,"verify-disabled")) {
            bool reset_verification=!strcmp(action,"verify-reset");
            if (reset_verification&&!yes(body,"reset_completed")) return reply(status,409,"Confirm that the reset or power cycle has completed.");
            if (!reset_verification&&reset_pending(c,d->id)) return reply(status,409,"A controller reset is pending. Use Verify completed recovery after it finishes.");
            if (!enabled(d,false,0)||health(d,now,false)||!d->stationary_since||now-d->stationary_since<.4)
                return reply(status,409,"All six motors must report disabled, healthy and stationary before recovery can be cleared.");
            if (!clear_latch(c,d->id)) return reply(status,500,"Recovery state could not be saved.");
            message(c,"monitor","Recovery cleared: all six motors verified off, healthy and stationary. Enable motors when ready; no motor command was sent.");
            return reply(status,200,c->message);
        }
        if (!strcmp(action,"reset")) {
            struct piper_sample *s=&d->feedback.arm[0];
            if (reset_pending(c,d->id)) return reply(status,409,"A reset is already pending; verify its completion rather than resending.");
            if (s->control>2||s->motion_mode>1||(s->teach!=0&&s->teach!=2&&s->teach!=6)) return reply(status,409,"Reset is supported only for stopped/idle MoveJ or completed teaching states.");
            if (!d->stationary_since||now-d->stationary_since<.4) return reply(status,409,"Wait for a stationary arm before reset recovery.");
        }
        bool reset=!strcmp(action,"reset");
        c->disable_keeps_recovery=blocked(c,d->id)||health(d,now,false)!=NULL;
        if (!latch(c,d->id,reset?"Reset pending verification":"Motors disabled; recovery verification required",reset||reset_pending(c,d->id))) return reply(status,500,"Could not persist the recovery record; no command sent.");
        begin(c,d,owner,reset?OP_RESET:OP_DISABLE,now); c->sent_at=now;
        struct can_frame f=piper_reset_frame();
        bool sent=reset?can_bus_send(&c->bus,d,c->generation,&f,1):send_power(c,d,false);
        if (!sent) { c->op=OP_IDLE; c->owner[0]=0; message(c,"blocked","Power/recovery request was not fully transmitted. Recovery remains required."); return reply(status,409,c->message); }
        message(c,reset?"resetting":"disabling","Request queued; checking fresh disabled-motor feedback.");
    } else {
        const char *why=controller_blocker(c,d,now); if (why) return reply(status,409,why);
        if (!yes(body,"clear")) return reply(status,400,"Confirm that the full motion path is clear and the arm is attended.");
        /* The MCU's target-arrival bit can remain unset after a disabled arm
           settles away from its last target. Require measured stillness. */
        if (!d->stationary_since||now-d->stationary_since<.3) return reply(status,409,"Wait for a stationary arm before beginning another action.");
        memcpy(c->target,d->feedback.arm[0].deg,sizeof c->target);
        for (unsigned i=0;i<6;++i) {
            struct piper_limit *l=&d->feedback.limit[i];
            if (c->target[i]>=l->min-limit_feedback_slop_deg&&c->target[i]<=l->max+limit_feedback_slop_deg)
                c->target[i]=fmin(l->max,fmax(l->min,c->target[i]));
        }
        enum operation op=OP_MOVE;
        bool staged_range=false, zero_destination=!strcmp(action,"move-to-zero");
        int selected_joint=-1;
        double requested_target[6], requested_speed=0, requested_percent=0;
        c->speed=3; c->tolerance=.2;
        double ceiling=1e9;
        for (unsigned i=0;i<6;++i) ceiling=fmin(ceiling,d->feedback.limit[i].speed*180/3.141592653589793);
        bool percentage=field(body,"speed_percent")!=NULL;
        if (percentage) {
            if (field(body,"speed_deg_s")) return reply(status,400,"Specify speed_percent or speed_deg_s, not both.");
            if (!numeric(body,"speed_percent",0,100,&c->speed_percent)) return reply(status,400,"speed_percent must be 0–100.");
            c->speed=ceiling*c->speed_percent/100;
        } else if (field(body,"speed_deg_s")&&!numeric(body,"speed_deg_s",.1,15,&c->speed)) return reply(status,400,"Legacy speed_deg_s must be 0.1–15; use speed_percent for reported limits.");
        if (field(body,"tolerance_deg")&&!numeric(body,"tolerance_deg",.05,1,&c->tolerance)) return reply(status,400,"tolerance_deg must be 0.05–1.0.");
        if (!strcmp(action,"enable")) {
            if (!yes(body,"supported")) return reply(status,400,"Support the arm before changing motor power.");
            if (!enabled(d,false,0)) return reply(status,409,"All six motors must be disabled before this enable operation.");
            op=OP_ENABLE;
        } else if (!strcmp(action,"align")) {
            double distance=range_distance(d);
            if (!isfinite(distance)||distance<=0) return reply(status,409,"All joints are already within the reported command ranges.");
            if (distance>3) return reply(status,409,"A joint is more than 3 degrees outside its command range. Inspect the pose and calibration before motion.");
            for (unsigned i=0;i<6;++i) c->target[i]=range_target(d,i);
            op=OP_ALIGN; c->speed=1;
        } else if (!strcmp(action,"cycle")) {
            /* Existing H-V1.2-1 near-upright preset from the vendor DH model.
               Preserve base heading and tool roll; capture the return pose now. */
            c->target[1]=82; c->target[2]=-160; c->target[3]=0; c->target[4]=-7;
            op=OP_CYCLE; c->speed=3; c->tolerance=.2;
        } else if (!strcmp(action,"follow-start")) {
            /* Begin at fresh measurements. No queued target is replayed on entry. */
        } else if (!strcmp(action,"move-to-zero")) {
            memset(c->target,0,sizeof c->target);
        } else if (!strcmp(action,"pose")) {
            const cJSON *angles=field(body,"degrees");
            if (!cJSON_IsArray(angles)||cJSON_GetArraySize(angles)!=6) return reply(status,400,"degrees must contain six finite angles.");
            zero_destination=true;
            for (unsigned i=0;i<6;++i) { cJSON *n=cJSON_GetArrayItem(angles,(int)i); if (!cJSON_IsNumber(n)||!isfinite(n->valuedouble)) return reply(status,400,"Every angle must be finite."); c->target[i]=n->valuedouble; if (c->target[i]!=0) zero_destination=false; }
        } else if (!strcmp(action,"joint")||!strcmp(action,"jog")||!strcmp(action,"zero")) {
            double joint;
            if (!numeric(body,"joint",1,6,&joint)||floor(joint)!=joint) return reply(status,400,"joint must be 1–6.");
            unsigned index=(unsigned)joint-1;
            selected_joint=(int)index;
            double degrees=0;
            if (strcmp(action,"zero")&&!numeric(body,"degrees",!strcmp(action,"jog")?-5:-360,!strcmp(action,"jog")?5:360,&degrees)) return reply(status,400,"Enter a finite angle (jog increments are limited to ±5°).");
            /* An inward jog starts from the nearest legal boundary, as it
               already does for sub-tolerance encoder drift. It cannot request
               another invalid negative/outward pose. */
            if (!strcmp(action,"jog")) c->target[index]=fmin(d->feedback.limit[index].max,fmax(d->feedback.limit[index].min,c->target[index]));
            c->target[index]=!strcmp(action,"jog")?c->target[index]+degrees:degrees;
        } else if (!strcmp(action,"gripper")) {
            if (!d->feedback.gripper_seen||!piper_recent(d->feedback.gripper_at,now)||d->feedback.gripper_fault) return reply(status,409,"Fresh, fault-free gripper feedback is required.");
            struct piper_feedback *f=&d->feedback;
            if (!configuration_current(d,f->gripper_config_seen,f->gripper_config_at,now)||(f->gripper_max_mm!=70&&f->gripper_max_mm!=100)) return reply(status,409,"Read gripper configuration: a verified 70 or 100 mm range is required for this connection.");
            if (!numeric(body,"width_mm",0,f->gripper_max_mm,&c->gripper_target)) return reply(status,400,"width_mm must be between zero and the controller-reported maximum opening.");
            c->gripper_effort=.1;
            if (field(body,"effort_nm")&&!numeric(body,"effort_nm",.02,.5,&c->gripper_effort)) return reply(status,400,"effort_nm must be 0.02–0.5 N·m.");
            c->gripper_entry=f->gripper_mm<0;
            if (!isfinite(f->gripper_mm)||f->gripper_mm < -2||f->gripper_mm>f->gripper_max_mm) return reply(status,409,"Gripper feedback is outside the supported range; inspect calibration.");
            if (c->gripper_entry&&(!yes(body,"allow_closed_offset")||c->gripper_target<5)) return reply(status,409,"A small negative closed reading requires allow_closed_offset=true and an opening target of at least 5 mm. This does not change encoder zero.");
            c->gripper_max_mm=f->gripper_max_mm;
            c->gripper_command=d->feedback.gripper_mm; op=OP_GRIPPER;
        } else return reply(status,400,"Unknown control action.");
        if (op!=OP_ENABLE&&!enabled(d,true,0)) return reply(status,409,"Enable all six arm motors first so the unselected joints remain supported.");
        double correction=range_distance(d);
        if ((op==OP_MOVE||op==OP_CYCLE)&&correction>0) {
            bool inward_joint=selected_joint>=0&&outside_range_count(d)==1&&joint_outside_range(d,(unsigned)selected_joint);
            if (op!=OP_MOVE||(!zero_destination&&!inward_joint)) return reply(status,409,range_reason(c,d));
            if (correction>3) return reply(status,409,"Range entry requires more than 3 degrees of correction. Inspect the physical pose and calibration before moving.");
            staged_range=true;
        }
        if ((op==OP_MOVE||op==OP_ALIGN||op==OP_CYCLE)&&!valid_target(c,d,c->target)) return reply(status,400,c->error);
        if (percentage&&c->speed_percent==0&&(op==OP_MOVE||op==OP_CYCLE)) return reply(status,409,"Speed is 0%; choose a nonzero speed before moving. This setting applies to the next move, not an active stop.");
        /* Alignment and the optional scripted cycle retain their explicit rates. */
        if (!percentage||op==OP_ALIGN||op==OP_CYCLE) c->speed_percent=fmin(100,100*c->speed/ceiling);
        if (op==OP_CYCLE&&!valid_target(c,d,d->feedback.arm[0].deg)) return reply(status,400,c->error);
        for (unsigned i=0;i<6;++i) if (c->speed>d->feedback.limit[i].speed*180/3.141592653589793) return reply(status,400,"Requested speed exceeds a controller-reported joint speed limit.");
        if (staged_range) {
            memcpy(requested_target,c->target,sizeof requested_target);
            requested_speed=c->speed; requested_percent=c->speed_percent;
            for (unsigned i=0;i<6;++i) c->target[i]=joint_outside_range(d,i)?range_target(d,i):
                fmin(d->feedback.limit[i].max,fmax(d->feedback.limit[i].min,d->feedback.arm[0].deg[i]));
            if (!valid_target(c,d,c->target)) return reply(status,400,c->error);
            op=OP_ALIGN; c->speed=ceiling/100; c->speed_percent=1;
        }
        if (!latch(c,d->id,"Control action interrupted before verified completion",false)) return reply(status,500,"Could not persist action intent; no command sent.");
        begin(c,d,owner,op,now);
        if (staged_range) {
            c->range_followup=true; memcpy(c->range_followup_target,requested_target,sizeof requested_target);
            c->range_followup_speed=requested_speed; c->range_followup_percent=requested_percent;
        }
        if (!strcmp(action,"follow-start")) { c->following=true; c->follow_updated=now; }
        if (op==OP_CYCLE) { c->cycle_stage=1; cycle_leg(c,d,now); }
        if (op==OP_MOVE&&(zero_destination||(!strcmp(action,"pose")&&yes(body,"synchronized")))) { c->synchronized_pose=true; cycle_leg(c,d,now); }
        message(c,op==OP_ENABLE?"enabling":op==OP_GRIPPER?"gripper":op==OP_ALIGN?"aligning":op==OP_CYCLE?"raising":"moving","%s",op==OP_ENABLE?"Enabling motor drivers only; no position target is sent.":op==OP_ALIGN?"Moving 0.5 degrees inside the command range at 1% controller speed (maximum correction 3 degrees).":op==OP_CYCLE?"Raising to upright at up to 3 degrees/second. The measured starting pose is saved for return.":"Executing target with a live control lease and feedback checks.");
        if (staged_range) message(c,"aligning","Explicit destination accepted. First correcting bounded range overrun at 1%% controller speed; then moving to the requested target under the same live lease.");
    }
    cJSON *result=reply(status,202,c->message); num(result,"request_id",c->request_id); return result;
}

void controller_tick(struct controller *c,double now)
{
    can_bus_receive(&c->bus,now);
    struct can_device *d=can_bus_find(&c->bus,c->selected);
    relinquish_to_teaching(c,d,now);
    /* Recover transport and refresh information without a browser. Never
       replay control commands, clear recovery, or change motor power here. */
    if (!c->bus.simulated && c->op==OP_IDLE && !c->query) {
        if ((!d || !d->present || !piper_recent(d->last_rx,now) || *d->problem) && now-c->auto_reconnect_at>8) {
            c->auto_reconnect_at=now; can_bus_reconnect(&c->bus);
            d=can_bus_find(&c->bus,c->selected);
            c->auto_query_at=now;
            if (d && d->present) start_query(c,d,now);
        } else if (d && d->present && piper_recent(d->last_rx,now) &&
                   !arm_configuration_received(d,now) && now-c->auto_query_at>5) {
            c->auto_query_at=now; start_query(c,d,now);
        }
    }
    if (c->query) {
        if (!d||d->generation!=c->query_generation||!d->present) { c->query=false; message(c,"blocked","Connection changed during information query."); }
        else if (now-c->query_start>5) {
            c->query=false;
            c->auto_query_at=now; /* Retry only missing arm information in five seconds. */
            if (blocked(c,d->id)) message(c,"blocked","Information query incomplete. Recovery remains required: %s",text_field(record(c,d->id),"reason"));
            else message(c,"monitor","Information query ended. Missing replies remain unknown; inspect diagnostics.");
        }
        else if (now>=c->query_next && d->last_rx>=c->query_start) {
            if (c->query_joint<12) {
                unsigned request=c->query_joint++;
                struct can_frame f={.can_id=0x472,.len=8,.data={(uint8_t)(request%6+1),(uint8_t)(request<6?1:2)}};
                if (!can_bus_send(&c->bus,d,d->generation,&f,1)) { c->query=false; message(c,"blocked","Limit query could not be transmitted."); }
                c->query_next=now+.08;
            } else {
                bool complete=arm_configuration_received(d,now);
                if (complete) {
                    c->query=false;
                    if (blocked(c,d->id)) message(c,"blocked","Readings refreshed. Recovery remains required: %s",text_field(record(c,d->id),"reason"));
                    else message(c,"monitor","Arm firmware and all six angle, speed and acceleration limits received.%s",d->feedback.gripper_config_seen?"":" Standard CAN gripper configuration unavailable; Pika is independent.");
                } else {
                    /* Retry missing read-only replies within the existing
                       five-second deadline. Never retry motor commands. */
                    struct can_frame f={.len=8}; bool needed=false;
                    for (unsigned attempt=0;attempt<14&&!needed;attempt++) {
                      unsigned request=(c->query_joint++-12)%14;
                      f=(struct can_frame){.len=8};
                      if (request<12) {
                        unsigned joint=request%6;
                        needed=request<6 ? (!d->feedback.limit[joint].seen||d->feedback.limit[joint].at<c->query_start) :
                            (!d->feedback.acceleration[joint].seen||d->feedback.acceleration[joint].at<c->query_start);
                        f.can_id=0x472; f.data[0]=(uint8_t)(joint+1); f.data[1]=(uint8_t)(request<6?1:2);
                      } else if (request==12) {
                        needed=!*d->feedback.version; f.can_id=0x4af; f.data[0]=1;
                      } else {
                        needed=!d->feedback.gripper_config_seen||d->feedback.gripper_config_at<c->query_start;
                        f.can_id=0x477; f.data[0]=4; f.data[4]=3;
                      }
                    }
                    if (needed&&!can_bus_send(&c->bus,d,d->generation,&f,1)) {
                        c->query=false; message(c,"blocked","Information retry could not be transmitted.");
                    }
                    c->query_next=now+.08;
                }
            }
        }
    }
    if (c->op==OP_IDLE) return;
    if (c->op==OP_TEACH_RETURN) {
        if (!d||d->generation!=c->generation||!d->present) { abandon_teaching_return(c,"Adapter connection changed."); return; }
        struct piper_sample *s=&d->feedback.arm[0];
        /* A hand-guided reentry relinquishes control. Unexpected motion after
           the controller accepts standby/CAN is different: request STOP rather
           than abandon a possibly moving controller with no position target. */
        if (piper_fresh(s,now)&&s->control<=1&&!teaching_active(s)) {
            for (unsigned i=0;i<6;++i) if (fabs(s->deg[i]-c->initial[i])>.5) {
                c->obstruction_joint=i+1;
                controller_stop(c,"Unexpected measured joint movement during teaching-to-CAN handoff; no position target was requested."); return;
            }
        }
        if (now-c->heartbeat>=.8) { abandon_teaching_return(c,"Control lease expired."); return; }
        const char *why=teaching_return_guard(c,d,now);
        if (why) { abandon_teaching_return(c,why); return; }
        for (unsigned i=0;i<6;++i) if (fabs(d->feedback.arm[0].deg[i]-c->initial[i])>.5) {
            abandon_teaching_return(c,"Measured pose changed during the mode transition."); return;
        }
        if (now-c->sent_at>1.5) { abandon_teaching_return(c,"Mode acknowledgement timed out; no automatic retry or reset will run."); return; }
        if ((c->teach_return_stage==1&&s->control!=2&&s->control!=0)||(c->teach_return_stage==2&&s->control!=0&&s->control!=1)) {
            abandon_teaching_return(c,"Controller mode changed outside the requested standby/CAN sequence."); return;
        }
        if (s->status_at<=c->sent_at) return;
        if (c->teach_return_stage==1&&s->control==0) {
            struct can_frame frame=piper_mode(1); c->sent_at=now;
            if (!can_bus_send(&c->bus,d,c->generation,&frame,1)) { abandon_teaching_return(c,"CAN request could not be transmitted."); return; }
            c->teach_return_stage=2;
            message(c,"teaching-return","Standby acknowledged. CAN requested; waiting for fresh controller feedback. Keep the arm supported.");
        } else if (c->teach_return_stage==2&&s->control==1&&s->motion_mode==1) {
            c->teach_return_stage=0;
            finish(c,d,"CAN control acknowledged at zero. No reset, motor-power, homing or position command was sent; old targets were discarded.",false);
        }
        return;
    }
    if (!d||d->generation!=c->generation||!d->present) { controller_stop(c,"Adapter connection changed."); return; }
    if (c->following&&now-c->follow_updated>=.25) {
        follow_hold(c,d,now,"Following input expired after 250 ms. Holding measured pose; no target replay."); return;
    }
    if (now-c->heartbeat>=.8) { controller_stop(c,"Control heartbeat expired."); return; }
    if (!c->following&&now-c->started>(c->op==OP_CYCLE?180:c->op==OP_MOVE||c->op==OP_ALIGN||c->op==OP_GRIPPER?120:c->op==OP_ENABLE?8:3)) {
        if (c->op==OP_ENABLE) {
            char reason[200]="Enable timed out; motors still disabled:";
            for (unsigned i=0;i<6;++i) if (!d->feedback.motor[i].enabled) {
                size_t used=strlen(reason); snprintf(reason+used,sizeof reason-used," J%u",i+1);
            }
            controller_stop(c,reason);
        } else controller_stop(c,"Control action timed out.");
        return;
    }
    bool recovering=c->op==OP_RESET||c->op==OP_DISABLE;
    const char *why=health(d,now,c->op==OP_RESET?CHECK_RESET:recovering?CHECK_POWER:CHECK_CONTROL);
    if (why) { controller_stop(c,why); return; }
    struct piper_sample *s=&d->feedback.arm[0];
    if (recovering) {
        bool fresh=enabled(d,false,c->sent_at);
        for (unsigned i=0;i<6;++i) fresh=fresh&&s->joint_at[i]>c->sent_at;
        if (fresh&&s->status_at>c->sent_at&&!s->error&&(s->arm_status==0||(c->op==OP_DISABLE&&c->disable_keeps_recovery))&&d->stationary_since&&now-d->stationary_since>=.4) {
            if (!c->settled_at) c->settled_at=now;
            if (now-c->settled_at>=.4) finish(c,d,c->op==OP_RESET?"Reset verified: motors disabled. Enable explicitly before motion.":c->disable_keeps_recovery?"All six motors verified disabled. Use Verify motors off to clear the previous recovery record.":"All six motors verified disabled. Ready to enable again.",c->op==OP_RESET||!c->disable_keeps_recovery);
        } else c->settled_at=0;
        return;
    }
    double dt=now-c->last_tick; if (dt<.02) return; c->last_tick=now; dt=fmin(dt,.05);
    if (c->op==OP_ENABLE) {
        for (unsigned i=0;i<6;++i) if (fabs(s->deg[i]-c->initial[i])>.5) { controller_stop(c,"Unexpected joint movement while enabling."); return; }
        if (enabled(d,true,c->started)&&s->status_at>c->started) {
            finish(c,d,"All six motors report enabled. No position target was sent.",true);
            return;
        }
        /* Wait for each driver's acknowledgement before advancing. Retry a
           dropped request at 150 ms intervals within an 8 s startup deadline;
           never burst commands or send a position target while enabling. */
        if (c->enable_count&&now-c->sent_at<.15) return;
        for (unsigned i=0;i<6;++i) {
            struct piper_motor *motor=&d->feedback.motor[i];
            if (motor->enabled&&motor->at>c->started) continue;
            struct can_frame frame=piper_power(i+1,true);
            if (!can_bus_send(&c->bus,d,c->generation,&frame,1)) { controller_stop(c,"Enable command was not fully transmitted."); return; }
            ++c->enable_count; c->sent_at=piper_now();
            break;
        }
        return;
    }
    if (!enabled(d,true,0)) { controller_stop(c,"Motor enable changed during motion."); return; }
    if (c->op!=OP_GRIPPER&&(s->control!=1||s->motion_mode!=1)&&c->sent_at&&now-c->sent_at>.5) { controller_stop(c,"Controller did not enter CAN MoveJ mode."); return; }
    double error=0;
    if (c->op==OP_MOVE||c->op==OP_ALIGN||c->op==OP_CYCLE) {
        if (!valid_target(c,d,c->target)) { controller_stop(c,c->error); return; }
        if (c->arrival_hold&&(!valid_target(c,d,c->arrival_hold_pose)||now-c->arrival_hold_at>2)) {
            controller_stop(c,"Measured arrival hold did not verify stable fresh feedback within two seconds."); return;
        }
        if (!c->arrival_hold&&(c->op==OP_CYCLE||c->synchronized_pose)) {
            if (c->op==OP_CYCLE&&!valid_target(c,d,c->initial)) { controller_stop(c,"The saved return pose no longer satisfies joint limits."); return; }
            double fraction=c->leg_span>0?fmin(1,c->leg_fraction+c->speed*dt/c->leg_span):1;
            for (unsigned i=0;i<6;++i) {
                double delta=c->target[i]-c->leg_start[i];
                if (fabs(delta)>.000001) {
                    double lead=(s->deg[i]+copysign(1,delta)-c->leg_start[i])/delta;
                    fraction=fmin(fraction,fmax(c->leg_fraction,lead));
                }
            }
            c->leg_fraction=fraction;
        }
        for (unsigned i=0;i<6;++i) {
            if (fabs(c->command[i]-s->deg[i])>3) { controller_stop(c,"Measured joint deviated more than 3 degrees from the commanded path."); return; }
            if (c->arrival_hold) {
                c->command[i]=c->arrival_hold_pose[i];
            } else if (c->op==OP_CYCLE||c->synchronized_pose) {
                c->command[i]=c->leg_start[i]+(c->target[i]-c->leg_start[i])*c->leg_fraction;
            } else if (c->op==OP_ALIGN) {
                /* An out-of-range intermediate target is not valid. Entry is
                   explicit, limited to 3 degrees, and uses 1% MCU speed. */
                if (fabs(s->deg[i]-c->initial[i])>fabs(c->target[i]-c->initial[i])+.5) { controller_stop(c,"Unexpected joint movement during range entry."); return; }
                c->command[i]=c->target[i];
            } else {
                double remaining=c->target[i]-c->command[i];
                c->command[i]+=copysign(fmin(fabs(remaining),c->speed*dt),remaining);
                if (fabs(c->command[i]-s->deg[i])>1) c->command[i]=s->deg[i]+copysign(1,c->command[i]-s->deg[i]);
            }
            error=fmax(error,fabs(c->target[i]-s->deg[i]));
            c->command[i]=fmin(d->feedback.limit[i].max,fmax(d->feedback.limit[i].min,c->command[i]));
        }
        for (unsigned i=0;i<6;++i) {
            if (obstruction_stalled(&c->obstruction[i],now,s->deg[i],c->command[i],c->target[i],c->tolerance)) {
                char reason[200]; c->obstruction_joint=i+1;
                snprintf(reason,sizeof reason,"Possible obstruction at J%u: no measured progress for 0.5 seconds (actual %.3f, commanded %.3f, target %.3f degrees).",i+1,s->deg[i],c->command[i],c->target[i]);
                controller_stop(c,reason); return;
            }
        }
        if (!send_pose(c,d,c->command)) { controller_stop(c,"Joint command was not fully transmitted."); return; }
        if (!c->sent_at) c->sent_at=now;
    } else {
        if (!d->feedback.gripper_seen||!piper_recent(d->feedback.gripper_at,now)||d->feedback.gripper_fault) { controller_stop(c,"Gripper feedback missing or faulted."); return; }
        for (unsigned i=0;i<6;++i) if (fabs(s->deg[i]-c->initial[i])>.5) { controller_stop(c,"Arm joint moved during gripper control."); return; }
        double remaining=c->gripper_target-c->gripper_command,actual=d->feedback.gripper_mm;
        if (!configuration_current(d,d->feedback.gripper_config_seen,d->feedback.gripper_config_at,now)||d->feedback.gripper_max_mm!=c->gripper_max_mm) { controller_stop(c,"Gripper configuration changed or became unavailable."); return; }
        if (!isfinite(actual)||actual<(c->gripper_entry?-2:0)||actual>c->gripper_max_mm) { controller_stop(c,"Gripper reading left the supported range."); return; }
        if (c->gripper_entry&&actual<0) {
            /* Explicit bounded opening from a small closed encoder offset.
               Never transmit a negative target or rewrite the encoder zero. */
            c->gripper_command=1;
        } else {
            c->gripper_entry=false;
            c->gripper_command+=copysign(fmin(fabs(remaining),5*dt),remaining);
            if (fabs(c->gripper_command-actual)>1) c->gripper_command=actual+copysign(1,c->gripper_command-actual);
        }
        struct can_frame f;
        if (!piper_gripper(c->gripper_command,c->gripper_effort,true,&f)||!can_bus_send(&c->bus,d,c->generation,&f,1)) { controller_stop(c,"Gripper command failed."); return; }
        if (!c->sent_at) c->sent_at=now;
        error=fabs(c->gripper_target-actual);
    }
    // Retargeting never resets the per-joint obstruction watches above.
    if (c->following) return;
    if (error<c->best_error-.02) { c->best_error=error; c->progress_at=now; }
    else if (error>c->tolerance&&now-c->progress_at>5) { controller_stop(c,"No measured progress toward the target for five seconds."); return; }
    bool final_command=true, post_target=true;
    if (c->op==OP_GRIPPER) {
        final_command=fabs(c->gripper_command-c->gripper_target)<.0005;
        post_target=d->feedback.gripper_at>c->final_sent_at&&d->feedback.gripper_enabled;
    } else for (unsigned i=0;i<6;++i) {
        final_command=final_command&&(c->arrival_hold||fabs(c->command[i]-c->target[i])<.0005);
        post_target=post_target&&s->joint_at[i]>c->final_sent_at;
    }
    if (final_command&&!c->final_sent_at) { c->final_sent_at=now; post_target=false; }
    if (!final_command) c->final_sent_at=0;
    bool independent_arrival=(c->op==OP_MOVE||c->op==OP_ALIGN)&&measured_stationary(d,now,c->final_sent_at);
    double measured_hold[6];
    for (unsigned i=0;i<6&&independent_arrival;++i) {
        struct piper_limit *l=&d->feedback.limit[i];
        if (s->deg[i]<l->min-limit_feedback_slop_deg||s->deg[i]>l->max+limit_feedback_slop_deg) independent_arrival=false;
        measured_hold[i]=fmin(l->max,fmax(l->min,s->deg[i]));
        if (c->arrival_hold&&fabs(s->deg[i]-c->arrival_hold_pose[i])>fmax(.03,fmin(c->tolerance,.1))) independent_arrival=false;
    }
    bool arrival_ack=c->arrival_hold?independent_arrival&&(s->motion_status==0||s->motion_status==1):
        s->motion_status==0||(s->motion_status==1&&independent_arrival);
    if (c->final_sent_at&&post_target&&s->status_at>c->final_sent_at&&error<=(c->op==OP_GRIPPER?.5:c->tolerance)&&arrival_ack) {
        if (!c->settled_at) c->settled_at=now;
        if (now-c->settled_at>=.4) {
            /* One packet that remains younger than 500ms is not evidence of a
               full settling interval. Busy-bit fallback and its hold must be
               covered by new encoder/status/velocity samples at the endpoint. */
            if ((c->arrival_hold||s->motion_status==1)&&!measured_through(d,c->settled_at+.4)) return;
            if (s->motion_status==1&&!c->arrival_hold) {
                /* Some MCUs retain busy for a sub-resolution residual already
                   inside the operator's tolerance. Independent fresh encoder
                   stillness is required first; then remove the old demand by
                   sending a legal measured hold and verify new feedback. */
                if (!valid_target(c,d,measured_hold)) { controller_stop(c,c->error); return; }
                memcpy(c->arrival_hold_pose,measured_hold,sizeof measured_hold);
                c->arrival_hold=true; c->arrival_hold_at=now;
                memcpy(c->command,measured_hold,sizeof measured_hold);
                if (!send_pose(c,d,measured_hold)) { controller_stop(c,"Measured arrival hold could not be transmitted."); return; }
                c->final_sent_at=now; c->settled_at=0;
                message(c,"settling","Inside requested tolerance; verifying a stationary measured hold because the MCU still reports busy.");
                return;
            }
            if (c->op==OP_ALIGN&&c->range_followup) {
                if (range_distance(d)>0||!valid_target(c,d,c->range_followup_target)) {
                    controller_stop(c,"Range entry did not verify a legal pose for the pending destination; that destination was discarded."); return;
                }
                memcpy(c->target,c->range_followup_target,sizeof c->target);
                c->range_followup=false; c->op=OP_MOVE; c->synchronized_pose=true;
                c->speed=c->range_followup_speed; c->speed_percent=c->range_followup_percent;
                cycle_leg(c,d,now);
                message(c,"moving","Range entry verified. Moving to the explicitly requested destination; Stop movement cancels it.");
            } else if (c->op==OP_CYCLE&&c->cycle_stage==1) {
                c->cycle_stage=2; c->pause_until=now+2;
                message(c,"upright","Upright reached. Pausing for two seconds before returning.");
            } else if (c->op==OP_CYCLE&&c->cycle_stage==2) {
                if (now>=c->pause_until) {
                    memcpy(c->target,c->initial,sizeof c->target); c->cycle_stage=3; cycle_leg(c,d,now);
                    message(c,"returning","Returning to the saved starting pose.");
                }
            } else finish(c,d,c->op==OP_CYCLE?"Complete movement finished: upright and back to the starting pose within 0.2 degrees. Motors remain enabled.":c->op==OP_GRIPPER?"Gripper opening reached within 0.5 mm; gripper remains enabled.":c->arrival_hold?"Joint target reached within the selected tolerance; fresh stationary measured hold verified despite retained MCU busy. Motors remain enabled.":"Joint target reached within the selected tolerance; motors remain enabled.",true);
        }
    } else { c->settled_at=0; if (c->op==OP_CYCLE&&c->cycle_stage==2) c->pause_until=now+2; }
}

cJSON *controller_state(struct controller *c,const char *token)
{
    double now=piper_now(); cJSON *root=cJSON_CreateObject(),*devices=cJSON_CreateArray();
    str(root,"kind","piper_direct_c"); str(root,"api_version","1.0"); flag(root,"simulated",c->bus.simulated);
    str(root,"token",token); str(root,"selected",c->selected); str(root,"message",c->message); str(root,"phase",c->phase);
    flag(root,"query_active",c->query); flag(root,"storage_failed",c->storage_failed);
    cJSON *motion=cJSON_CreateObject(); flag(motion,"active",c->op!=OP_IDLE); str(motion,"owner",c->owner);
    num(motion,"request_id",c->request_id); num(motion,"speed_deg_s",c->speed); num(motion,"tolerance_deg",c->tolerance);
    num(motion,"speed_percent",c->speed_percent);
    flag(motion,"following",c->op!=OP_IDLE&&c->following);
    flag(motion,"finishing_target",c->op==OP_MOVE&&c->follow_committed);
    num(motion,"follow_sequence",c->follow_sequence);
    num(motion,"teaching_return_stage",c->op==OP_TEACH_RETURN?c->teach_return_stage:0);
    num(motion,"elapsed_s",c->op!=OP_IDLE?now-c->started:0); put(root,"motion",motion);
    str(motion,"sequence_stage",c->op==OP_CYCLE?(c->cycle_stage==1?"raising":c->cycle_stage==2?"upright":"returning"):"");
    if (c->range_followup) str(motion,"sequence_stage","range-entry-before-destination");
    flag(motion,"range_destination_pending",c->range_followup);
    flag(motion,"verifying_arrival_hold",c->arrival_hold);
    cJSON *return_pose=cJSON_CreateArray();
    if (c->op==OP_CYCLE) for (unsigned i=0;i<6;++i) cJSON_AddItemToArray(return_pose,cJSON_CreateNumber(c->initial[i]));
    put(motion,"return_degrees",return_pose);
    unsigned connected=0;
    for (unsigned n=0;n<CANBUS_MAX;++n) {
        struct can_device *d=&c->bus.devices[n]; if (!*d->id) continue;
        struct piper_feedback *f=&d->feedback; struct piper_sample *s=&f->arm[0];
        cJSON *j=cJSON_CreateObject(); str(j,"id",d->id); str(j,"name",*d->label?d->label:d->name);
        str(j,"interface",d->name); str(j,"usb_path",d->path); str(j,"connection_error",d->problem);
        flag(j,"present",d->present); flag(j,"up",d->up); flag(j,"stable_identity",d->stable); num(j,"generation",d->generation);
        num(j,"rx_frames",(double)d->rx); num(j,"tx_frames",(double)d->tx); num(j,"local_frames",(double)d->local); num(j,"can_errors",(double)d->errors);
        flag(j,"link_verified",d->link_verified); num(j,"bitrate",d->link_verified?d->bitrate:NAN); num(j,"bus_state",d->link_verified?d->bus_state:NAN);
        num(j,"kernel_rx_packets",d->kernel_rx); num(j,"kernel_tx_packets",d->kernel_tx);
        num(j,"last_rx_age_s",d->last_rx?now-d->last_rx:NAN);
        bool fresh=piper_fresh(s,now); flag(j,"fresh",fresh); if (fresh) ++connected;
        bool in_action=c->op!=OP_IDLE&&!strcmp(d->id,c->selected);
        const char *why=in_action?"Control action in progress.":controller_blocker(c,d,now); str(j,"blocker",why?why:""); flag(j,"recovery_required",blocked(c,d->id)&&!in_action);
        double range_correction=range_distance(d);
        unsigned outside_count=outside_range_count(d);
        num(j,"range_entry_deg",range_correction);
        const char *range_blocker=why;
        if (!range_blocker&&strcmp(d->id,c->selected)) range_blocker="Activate this robot's assigned CAN adapter before controlling it.";
        if (!range_blocker&&c->query) range_blocker="Wait for the information query to complete.";
        if (!range_blocker&&!enabled(d,true,0)) range_blocker="Enable all six arm motors before position control.";
        if (!range_blocker&&(!d->stationary_since||now-d->stationary_since<.3)) range_blocker="Wait for stationary feedback before starting a new move.";
        bool zero_legal=true;
        for (unsigned i=0;i<6;++i) if (!f->limit[i].seen||f->limit[i].min>0||f->limit[i].max<0) zero_legal=false;
        cJSON *range=cJSON_CreateObject();
        flag(range,"required",isfinite(range_correction)&&range_correction>0);
        num(range,"correction_deg",range_correction); num(range,"maximum_correction_deg",3);
        str(range,"action","move-to-zero");
        flag(range,"can_move_to_zero",!range_blocker&&isfinite(range_correction)&&range_correction<=3&&zero_legal);
        str(range,"reason",range_blocker?range_blocker:!isfinite(range_correction)?"Read current joint limits first.":range_correction>3?
            "Range entry requires more than 3 degrees of correction. Inspect the pose and calibration.":!zero_legal?"Zero is outside a reported joint command range.":
            range_correction>0?range_reason(c,d):"");
        put(j,"range_recovery",range);
        cJSON *r=record(c,d->id); str(j,"stop_reason",yes(r,"blocked")?text_field(r,"reason"):text_field(legacy_stop(c,d->id),"reason"));
        put(j,"last_fault",field(r,"last_fault")?cJSON_Duplicate(field(r,"last_fault"),true):cJSON_CreateNull());
        put(j,"stop_event",field(r,"stop_event")?cJSON_Duplicate(field(r,"stop_event"),true):cJSON_CreateNull());
        flag(j,"reset_pending",reset_pending(c,d->id));
        str(j,"firmware",f->version); str(j,"hardware",f->hardware); str(j,"build",f->build); flag(j,"firmware_supported",piper_supported(f));
        cJSON *status=cJSON_CreateObject(); num(status,"control",s->status_seen?s->control:NAN); num(status,"arm_status",s->status_seen?s->arm_status:NAN);
        num(status,"motion_mode",s->status_seen?s->motion_mode:NAN); num(status,"error",s->status_seen?s->error:NAN); num(status,"age_s",s->status_seen?now-s->status_at:NAN); put(j,"status",status);
        num(status,"motion_status",s->status_seen?s->motion_status:NAN);
        num(status,"teach_state",s->status_seen?s->teach:NAN);
        num(status,"joint_communication_fault_mask",s->status_seen?s->error&0x3f:NAN);
        num(status,"joint_angle_limit_fault_mask",s->status_seen?(s->error>>8)&0x3f:NAN);
        num(status,"stationary_s",d->stationary_since?now-d->stationary_since:0);
        cJSON *teaching=cJSON_CreateObject();
        const char *teach_phase=fresh?teaching_phase(s):"unknown";
        bool active_teaching=fresh&&teaching_active(s), return_required=fresh&&(s->control==2||active_teaching);
        const char *return_reason=teaching_return_guard(c,d,now);
        str(teaching,"phase",teach_phase); flag(teaching,"active",active_teaching);
        flag(teaching,"return_required",return_required);
        flag(teaching,"can_return",c->op==OP_IDLE&&fresh&&s->control==2&&!return_reason);
        flag(teaching,"zero_within_tolerance",fresh&&teaching_zero(d)); num(teaching,"zero_tolerance_deg",1);
        str(teaching,"return_action","teach-return"); str(teaching,"return_reason",return_reason?return_reason:"");
        flag(teaching,"handoff_in_progress",c->op==OP_TEACH_RETURN&&!strcmp(d->id,c->selected));
        str(teaching,"label",!fresh?"Waiting for fresh teaching feedback":!strcmp(teach_phase,"hand-guiding")?"Hand-guiding / onboard teaching record":
            !strcmp(teach_phase,"playback")?"Onboard teaching playback":!strcmp(teach_phase,"playback-paused")?"Onboard teaching playback paused":
            !strcmp(teach_phase,"ended")?"Teaching ended; return to CAN required":!strcmp(teach_phase,"off")?"CAN / standby control":"Unknown teaching state; controls withheld");
        put(j,"teaching",teaching);
        cJSON *joints=cJSON_CreateArray();
        for (unsigned i=0;i<6;++i) {
            cJSON *joint=cJSON_CreateObject(); struct piper_motor *m=&f->motor[i]; struct piper_limit *l=&f->limit[i];
            num(joint,"index",i+1); num(joint,"actual_deg",s->joint_seen[i]?s->deg[i]:NAN);
            bool active=(c->op==OP_MOVE||c->op==OP_ALIGN||c->op==OP_CYCLE)&&!strcmp(d->id,c->selected);
            num(joint,"target_deg",active?c->target[i]:NAN); num(joint,"age_s",s->joint_seen[i]?now-s->joint_at[i]:NAN);
            num(joint,"command_deg",active?c->command[i]:NAN);
            flag(joint,"fresh",s->joint_seen[i]&&piper_recent(s->joint_at[i],now));
            if (m->seen&&piper_recent(m->at,now)) flag(joint,"enabled",m->enabled); else put(joint,"enabled",cJSON_CreateNull());
            num(joint,"fault",m->seen?m->fault:NAN); num(joint,"voltage",m->seen?m->voltage:NAN); num(joint,"temperature",m->seen?m->temperature:NAN);
            num(joint,"motor_position_raw",m->high_seen&&piper_recent(m->high_at,now)?m->position_raw:NAN);
            num(joint,"motor_position_deg",m->high_seen&&piper_recent(m->high_at,now)?m->position_raw*180.0/(1000.0*3.141592653589793):NAN);
            num(joint,"current_a",m->high_seen&&piper_recent(m->high_at,now)?m->current:NAN); num(joint,"velocity_rad_s",m->high_seen&&piper_recent(m->high_at,now)?m->velocity:NAN);
            num(joint,"min_deg",l->seen?l->min:NAN); num(joint,"max_deg",l->seen?l->max:NAN); num(joint,"max_speed_rad_s",l->seen?l->speed:NAN);
            num(joint,"range_entry_target_deg",l->seen&&s->joint_seen[i]?range_target(d,i):NAN);
            bool outside=joint_outside_range(d,i);
            bool can_move=!range_blocker&&isfinite(range_correction)&&(range_correction==0||(outside_count==1&&outside&&range_correction<=3));
            flag(joint,"outside_command_range",outside);
            flag(joint,"can_move",can_move);
            flag(joint,"can_move_inward",can_move&&(s->deg[i]<l->min||s->deg[i]>l->max));
            flag(joint,"can_move_negative",can_move&&s->deg[i]>l->min);
            flag(joint,"can_move_positive",can_move&&s->deg[i]<l->max);
            str(joint,"motion_blocker",can_move?"":range_blocker?range_blocker:range_reason(c,d));
            flag(joint,"limits_fresh",configuration_current(d,l->seen,l->at,now)); cJSON_AddItemToArray(joints,joint);
            num(joint,"limits_age_s",l->seen?now-l->at:NAN);
            struct piper_acceleration *a=&f->acceleration[i];
            num(joint,"max_acceleration_rad_s2",a->seen?a->max:NAN);
            num(joint,"acceleration_age_s",a->seen?now-a->at:NAN);
            flag(joint,"acceleration_fresh",configuration_current(d,a->seen,a->at,now));
        }
        put(j,"joints",joints);
        cJSON *grip=cJSON_CreateObject(); num(grip,"width_mm",f->gripper_seen?f->gripper_mm:NAN);
        num(grip,"max_width_mm",f->gripper_config_seen?f->gripper_max_mm:NAN);
        num(grip,"configuration_age_s",f->gripper_config_seen?now-f->gripper_config_at:NAN);
        flag(grip,"configuration_fresh",configuration_current(d,f->gripper_config_seen,f->gripper_config_at,now)&&(f->gripper_max_mm==70||f->gripper_max_mm==100));
        flag(grip,"fresh",f->gripper_seen&&piper_recent(f->gripper_at,now)); flag(grip,"enabled",f->gripper_enabled);
        num(grip,"effort_nm",f->gripper_seen&&piper_recent(f->gripper_at,now)?f->gripper_effort:NAN);
        bool gripping=c->op==OP_GRIPPER&&!strcmp(d->id,c->selected);
        num(grip,"command_mm",gripping?c->gripper_command:NAN);
        num(grip,"effort_limit_nm",gripping?c->gripper_effort:NAN);
        num(grip,"fault",f->gripper_seen?f->gripper_fault:NAN); put(j,"gripper",grip);
        cJSON_AddItemToArray(devices,j);
    }
    put(root,"devices",devices); cJSON *discovery=cJSON_CreateObject(); num(discovery,"connected",connected); put(root,"discovery",discovery);
    return root;
}

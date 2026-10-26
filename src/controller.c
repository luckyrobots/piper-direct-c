#define _GNU_SOURCE
#include "controller.h"
#include <errno.h>
#include <fcntl.h>
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>
#include <linux/can/netlink.h>

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
    if (old) str(r,"legacy_ack",text_field(old,"at"));
    return persist(c);
}
static void message(struct controller *c,const char *phase,const char *format,...)
{
    snprintf(c->phase,sizeof c->phase,"%s",phase);
    va_list args; va_start(args,format); vsnprintf(c->message,sizeof c->message,format,args); va_end(args);
    fprintf(stderr,"%s: %s\n",phase,c->message);
}
enum health_check { CHECK_CONTROL, CHECK_POWER, CHECK_RESET };
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
    if (check!=CHECK_RESET&&(s->error||(check==CHECK_CONTROL&&s->arm_status)||s->arm_status>1)) {
        if (s->arm_status==5||(s->error&0x3f)) return "Robot reports a joint-drive communication fault. Motion stopped; inspect the saved fault before recovery.";
        return "The arm reports a fault or stop. Inspect status and recover before control.";
    }
    if (check==CHECK_CONTROL&&(s->control>1||(s->teach!=0&&s->teach!=2)||(s->motion_mode!=1&&!(s->control==0&&s->motion_mode==0)))) return "Arm must be in standby/CAN MoveJ mode with teaching inactive; use supported reset recovery if needed.";
    for (unsigned i=0;i<6;++i) {
        if (!isfinite(s->deg[i])||fabs(s->deg[i])>360) return "Joint feedback is outside the supported physical range.";
        if (!f->motor[i].seen||!piper_recent(f->motor[i].at,now)) return "Waiting for fresh feedback from all six motor drivers.";
        if (f->motor[i].fault) return "A motor driver reports a fault.";
    }
    if (!*f->hardware||!*f->version) return "Firmware has not been read on this connection. Click Read firmware & limits.";
    if (!piper_supported(f)) return "The reported hardware/firmware is not supported for motion. See the detected versions in Arm connection.";
    return NULL;
}
const char *controller_blocker(struct controller *c,struct can_device *d,double now)
{
    const char *why=health(d,now,false); if (why) return why;
    if (c->storage_failed) return "Control-state storage failed. Repair storage and restart; motion remains blocked.";
    if (blocked(c,d->id)) return "Recovery required after a recorded stop/interrupted action. Support the arm and use Recovery.";
    for (unsigned i=0;i<6;++i) {
        struct piper_limit *l=&d->feedback.limit[i];
        struct piper_acceleration *a=&d->feedback.acceleration[i];
        if (!l->seen||now-l->at>60) return "Read limits: all six controller limit replies must be less than 60 seconds old.";
        if (!isfinite(l->min)||!isfinite(l->max)||l->min>=l->max||l->min < -360||l->max>360||l->speed<=0||l->speed>3)
            return "Controller returned invalid joint limits or maximum speed.";
        if (!a->seen||now<a->at||now-a->at>60) return "Read limits: fresh acceleration replies from all six joints are required.";
        if (!isfinite(a->max)||a->max<=0||a->max>5) return "An acceleration limit is outside the supported 0–5 rad/s² range. Inspect firmware settings before motion.";
    }
    return NULL;
}
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
        distance=fmax(distance,fabs(range_target(d,i)-angle));
    }
    return distance;
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
static bool send_pose(struct controller *c,struct can_device *d,const double target[6])
{
    struct can_frame f[4]; f[0]=piper_mode(c->op==OP_ALIGN?1:10);
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
        /* Save the robot's fault before a stop changes its reported state. */
        struct piper_sample *s=&d->feedback.arm[0];
        if (s->status_seen&&piper_recent(s->status_at,piper_now())&&(s->arm_status||s->error)) {
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
    c->op=OP_IDLE; c->cycle_stage=0; c->owner[0]=0; c->query=false;
    message(c,"stopped","%s %s",reason,sent?"Stop queued; verify the arm has stopped. This is not a power cut.":"Stop delivery unavailable; physical stop may be needed.");
}
static void finish(struct controller *c,struct can_device *d,const char *result,bool clear)
{
    c->op=OP_IDLE; c->cycle_stage=0; c->owner[0]=0;
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
    if (mkdir(directory,0700)<0&&errno!=EEXIST) return false;
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
    c->op=op; c->generation=d->generation; c->started=c->heartbeat=c->last_tick=c->progress_at=now;
    c->sent_at=c->settled_at=c->final_sent_at=0; c->best_error=1e9; c->enable_count=0; ++c->request_id;
    c->cycle_stage=0;
    c->synchronized_pose=false;
    snprintf(c->owner,sizeof c->owner,"%s",owner);
    memcpy(c->initial,d->feedback.arm[0].deg,sizeof c->initial);
    memcpy(c->command,c->initial,sizeof c->command);
}
static void cycle_leg(struct controller *c,struct can_device *d,double now)
{
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
    d->feedback.firmware_len=0; d->feedback.version[0]=0;
    d->feedback.hardware[0]=0; d->feedback.build[0]=0;
    d->feedback.gripper_config_seen=false;
    if (!can_bus_send(&c->bus,d,d->generation,f,2)) return "Information requests could not be queued.";
    c->query=true; c->query_generation=d->generation; c->query_joint=0;
    c->query_start=now; c->query_next=now+.1;
    message(c,"querying","Reading firmware, gripper configuration and joint limits. Waiting for robot replies.");
    return NULL;
}
cJSON *controller_action(struct controller *c,const char *route,const cJSON *body,int *status)
{
    double now=piper_now(); struct can_device *d=can_bus_find(&c->bus,c->selected);
    if (!strcmp(route,"/api/stop")) {
        if (*text_field(body,"device_id")&&(!d||strcmp(text_field(body,"device_id"),d->id))) return reply(status,409,"Stop belongs to a different selected adapter.");
        if (field(body,"request_id")) {
            double request;
            if (c->op==OP_IDLE||strcmp(c->owner,text_field(body,"owner"))||!numeric(body,"request_id",1,UINT32_MAX,&request)||request!=c->request_id)
                return reply(status,409,"No matching active action for this automatic stop.");
        }
        controller_stop(c,"Operator stop."); return reply(status,200,c->message);
    }
    if (!strcmp(route,"/api/heartbeat")) {
        double request;
        if (c->op!=OP_IDLE&&now-c->heartbeat>=.8) { controller_stop(c,"Control heartbeat expired."); return reply(status,409,"Control lease expired; recovery required."); }
        if (c->op==OP_IDLE||strcmp(c->owner,text_field(body,"owner"))||!numeric(body,"request_id",1,UINT32_MAX,&request)||request!=c->request_id)
            return reply(status,409,"No matching active control lease.");
        c->heartbeat=now; return reply(status,200,"Control lease renewed.");
    }
    if (c->op!=OP_IDLE) {
        if (!strcmp(route,"/api/command")&&!strcmp(text_field(body,"action"),"hold")&&c->op==OP_GRIPPER&&!strcmp(c->owner,text_field(body,"owner"))) {
            const char *why=health(d,now,CHECK_CONTROL); if (why) return reply(status,409,why);
            if (d->generation!=c->generation||!enabled(d,true,0)||!d->feedback.gripper_enabled) return reply(status,409,"Connection or motor enable changed during gripper motion.");
            if (now-c->heartbeat>=.8||!d->feedback.gripper_seen||!piper_recent(d->feedback.gripper_at,now)||d->feedback.gripper_fault) return reply(status,409,"Fresh gripper feedback and an active lease are required to hold.");
            if (!c->sent_at) return reply(status,409,"No gripper target has been transmitted yet.");
            finish(c,d,"Gripper ramp stopped at its last transmitted target and effort limit. Contact or a secure grasp is not verified.",true);
            return reply(status,200,c->message);
        }
        if (!strcmp(route,"/api/command")&&!strcmp(text_field(body,"action"),"hold")&&(c->op==OP_MOVE||c->op==OP_CYCLE)&&!strcmp(c->owner,text_field(body,"owner"))) {
            const char *why=health(d,now,false); if (why) return reply(status,409,why);
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
        enum operation op=OP_MOVE;
        c->speed=3; c->tolerance=.2;
        if (field(body,"speed_deg_s")&&!numeric(body,"speed_deg_s",.1,15,&c->speed)) return reply(status,400,"speed_deg_s must be 0.1–15.");
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
        } else if (!strcmp(action,"pose")) {
            const cJSON *angles=field(body,"degrees");
            if (!cJSON_IsArray(angles)||cJSON_GetArraySize(angles)!=6) return reply(status,400,"degrees must contain six finite angles.");
            for (unsigned i=0;i<6;++i) { cJSON *n=cJSON_GetArrayItem(angles,(int)i); if (!cJSON_IsNumber(n)||!isfinite(n->valuedouble)) return reply(status,400,"Every angle must be finite."); c->target[i]=n->valuedouble; }
        } else if (!strcmp(action,"joint")||!strcmp(action,"jog")||!strcmp(action,"zero")) {
            double joint;
            if (!numeric(body,"joint",1,6,&joint)||floor(joint)!=joint) return reply(status,400,"joint must be 1–6.");
            unsigned index=(unsigned)joint-1;
            double degrees=0;
            if (strcmp(action,"zero")&&!numeric(body,"degrees",!strcmp(action,"jog")?-5:-360,!strcmp(action,"jog")?5:360,&degrees)) return reply(status,400,"Enter a finite angle (jog increments are limited to ±5°).");
            c->target[index]=!strcmp(action,"jog")?c->target[index]+degrees:degrees;
        } else if (!strcmp(action,"gripper")) {
            if (!d->feedback.gripper_seen||!piper_recent(d->feedback.gripper_at,now)||d->feedback.gripper_fault) return reply(status,409,"Fresh, fault-free gripper feedback is required.");
            struct piper_feedback *f=&d->feedback;
            if (!f->gripper_config_seen||now-f->gripper_config_at<0||now-f->gripper_config_at>60||(f->gripper_max_mm!=70&&f->gripper_max_mm!=100)) return reply(status,409,"Read gripper configuration: a verified 70 or 100 mm range is required.");
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
        if ((op==OP_MOVE||op==OP_CYCLE)&&range_distance(d)>0) return reply(status,409,"A joint rests outside its command range. Enable motors, then use Move into range before starting a move.");
        if ((op==OP_MOVE||op==OP_ALIGN||op==OP_CYCLE)&&!valid_target(c,d,c->target)) return reply(status,400,c->error);
        if (op==OP_CYCLE&&!valid_target(c,d,d->feedback.arm[0].deg)) return reply(status,400,c->error);
        for (unsigned i=0;i<6;++i) if (c->speed>d->feedback.limit[i].speed*180/3.141592653589793) return reply(status,400,"Requested speed exceeds a controller-reported joint speed limit.");
        if (!latch(c,d->id,"Control action interrupted before verified completion",false)) return reply(status,500,"Could not persist action intent; no command sent.");
        begin(c,d,owner,op,now);
        if (op==OP_CYCLE) { c->cycle_stage=1; cycle_leg(c,d,now); }
        if (op==OP_MOVE&&!strcmp(action,"pose")&&yes(body,"synchronized")) { c->synchronized_pose=true; cycle_leg(c,d,now); }
        message(c,op==OP_ENABLE?"enabling":op==OP_GRIPPER?"gripper":op==OP_ALIGN?"aligning":op==OP_CYCLE?"raising":"moving","%s",op==OP_ENABLE?"Enabling motor drivers only; no position target is sent.":op==OP_ALIGN?"Moving 0.5 degrees inside the command range at 1% controller speed (maximum correction 3 degrees).":op==OP_CYCLE?"Raising to upright at up to 3 degrees/second. The measured starting pose is saved for return.":"Executing target with a live control lease and feedback checks.");
    }
    cJSON *result=reply(status,202,c->message); num(result,"request_id",c->request_id); return result;
}

void controller_tick(struct controller *c,double now)
{
    can_bus_receive(&c->bus,now);
    struct can_device *d=can_bus_find(&c->bus,c->selected);
    if (c->query) {
        if (!d||d->generation!=c->query_generation||!d->present) { c->query=false; message(c,"blocked","Connection changed during information query."); }
        else if (now-c->query_start>3) {
            c->query=false;
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
                bool complete=*d->feedback.version!=0&&d->feedback.gripper_config_seen&&d->feedback.gripper_config_at>=c->query_start;
                for (unsigned i=0;i<6;++i) complete=complete&&d->feedback.limit[i].seen&&d->feedback.limit[i].at>=c->query_start&&d->feedback.acceleration[i].seen&&d->feedback.acceleration[i].at>=c->query_start;
                if (complete) {
                    c->query=false;
                    if (blocked(c,d->id)) message(c,"blocked","Readings refreshed. Recovery remains required: %s",text_field(record(c,d->id),"reason"));
                    else message(c,"monitor","Firmware, gripper configuration and all six angle, speed and acceleration limits received.");
                }
            }
        }
    }
    if (c->op==OP_IDLE) return;
    if (!d||d->generation!=c->generation||!d->present) { controller_stop(c,"Adapter connection changed."); return; }
    if (now-c->heartbeat>=.8) { controller_stop(c,"Control heartbeat expired."); return; }
    if (now-c->started>(c->op==OP_CYCLE?180:c->op==OP_MOVE||c->op==OP_ALIGN||c->op==OP_GRIPPER?120:3)) { controller_stop(c,"Control action timed out."); return; }
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
        if (c->enable_count<2) {
            if (c->enable_count&&now-c->sent_at<.05) return;
            if (!send_power(c,d,true)) { controller_stop(c,"Enable command was not fully transmitted."); return; }
            ++c->enable_count; c->sent_at=piper_now();
        } else if (enabled(d,true,c->sent_at)&&s->status_at>c->sent_at) {
            finish(c,d,"All six motors report enabled. No position target was sent.",true);
        }
        return;
    }
    if (!enabled(d,true,0)) { controller_stop(c,"Motor enable changed during motion."); return; }
    if (c->op!=OP_GRIPPER&&(s->control!=1||s->motion_mode!=1)&&c->sent_at&&now-c->sent_at>.5) { controller_stop(c,"Controller did not enter CAN MoveJ mode."); return; }
    double error=0;
    if (c->op==OP_MOVE||c->op==OP_ALIGN||c->op==OP_CYCLE) {
        if (!valid_target(c,d,c->target)) { controller_stop(c,c->error); return; }
        if (c->op==OP_CYCLE||c->synchronized_pose) {
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
            if (c->op==OP_CYCLE||c->synchronized_pose) {
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
                c->command[i]=fmin(d->feedback.limit[i].max,fmax(d->feedback.limit[i].min,c->command[i]));
            }
            error=fmax(error,fabs(c->target[i]-s->deg[i]));
        }
        if (!send_pose(c,d,c->command)) { controller_stop(c,"Joint command was not fully transmitted."); return; }
        if (!c->sent_at) c->sent_at=now;
    } else {
        if (!d->feedback.gripper_seen||!piper_recent(d->feedback.gripper_at,now)||d->feedback.gripper_fault) { controller_stop(c,"Gripper feedback missing or faulted."); return; }
        for (unsigned i=0;i<6;++i) if (fabs(s->deg[i]-c->initial[i])>.5) { controller_stop(c,"Arm joint moved during gripper control."); return; }
        double remaining=c->gripper_target-c->gripper_command,actual=d->feedback.gripper_mm;
        if (!d->feedback.gripper_config_seen||now-d->feedback.gripper_config_at<0||now-d->feedback.gripper_config_at>60||d->feedback.gripper_max_mm!=c->gripper_max_mm) { controller_stop(c,"Gripper configuration changed or expired."); return; }
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
    if (error<c->best_error-.02) { c->best_error=error; c->progress_at=now; }
    else if (error>c->tolerance&&now-c->progress_at>5) { controller_stop(c,"No measured progress toward the target for five seconds."); return; }
    bool final_command=true, post_target=true;
    if (c->op==OP_GRIPPER) {
        final_command=fabs(c->gripper_command-c->gripper_target)<.0005;
        post_target=d->feedback.gripper_at>c->final_sent_at&&d->feedback.gripper_enabled;
    } else for (unsigned i=0;i<6;++i) {
        final_command=final_command&&fabs(c->command[i]-c->target[i])<.0005;
        post_target=post_target&&s->joint_at[i]>c->final_sent_at;
    }
    if (final_command&&!c->final_sent_at) { c->final_sent_at=now; post_target=false; }
    if (!final_command) c->final_sent_at=0;
    if (c->final_sent_at&&post_target&&s->status_at>c->final_sent_at&&error<=(c->op==OP_GRIPPER?.5:c->tolerance)&&s->motion_status==0) {
        if (!c->settled_at) c->settled_at=now;
        if (now-c->settled_at>=.4) {
            if (c->op==OP_CYCLE&&c->cycle_stage==1) {
                c->cycle_stage=2; c->pause_until=now+2;
                message(c,"upright","Upright reached. Pausing for two seconds before returning.");
            } else if (c->op==OP_CYCLE&&c->cycle_stage==2) {
                if (now>=c->pause_until) {
                    memcpy(c->target,c->initial,sizeof c->target); c->cycle_stage=3; cycle_leg(c,d,now);
                    message(c,"returning","Returning to the saved starting pose.");
                }
            } else finish(c,d,c->op==OP_CYCLE?"Complete movement finished: upright and back to the starting pose within 0.2 degrees. Motors remain enabled.":c->op==OP_GRIPPER?"Gripper opening reached within 0.5 mm; gripper remains enabled.":"Joint target reached within the selected tolerance; motors remain enabled.",true);
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
    num(motion,"elapsed_s",c->op!=OP_IDLE?now-c->started:0); put(root,"motion",motion);
    str(motion,"sequence_stage",c->op==OP_CYCLE?(c->cycle_stage==1?"raising":c->cycle_stage==2?"upright":"returning"):"");
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
        num(j,"range_entry_deg",range_distance(d));
        cJSON *r=record(c,d->id); str(j,"stop_reason",yes(r,"blocked")?text_field(r,"reason"):text_field(legacy_stop(c,d->id),"reason"));
        put(j,"last_fault",field(r,"last_fault")?cJSON_Duplicate(field(r,"last_fault"),true):cJSON_CreateNull());
        flag(j,"reset_pending",reset_pending(c,d->id));
        str(j,"firmware",f->version); str(j,"hardware",f->hardware); str(j,"build",f->build); flag(j,"firmware_supported",piper_supported(f));
        cJSON *status=cJSON_CreateObject(); num(status,"control",s->status_seen?s->control:NAN); num(status,"arm_status",s->status_seen?s->arm_status:NAN);
        num(status,"motion_mode",s->status_seen?s->motion_mode:NAN); num(status,"error",s->status_seen?s->error:NAN); num(status,"age_s",s->status_seen?now-s->status_at:NAN); put(j,"status",status);
        num(status,"motion_status",s->status_seen?s->motion_status:NAN);
        num(status,"teach_state",s->status_seen?s->teach:NAN);
        num(status,"joint_communication_fault_mask",s->status_seen?s->error&0x3f:NAN);
        num(status,"joint_angle_limit_fault_mask",s->status_seen?(s->error>>8)&0x3f:NAN);
        num(status,"stationary_s",d->stationary_since?now-d->stationary_since:0);
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
            num(joint,"current_a",m->high_seen&&piper_recent(m->high_at,now)?m->current:NAN); num(joint,"velocity_rad_s",m->high_seen&&piper_recent(m->high_at,now)?m->velocity:NAN);
            num(joint,"min_deg",l->seen?l->min:NAN); num(joint,"max_deg",l->seen?l->max:NAN); num(joint,"max_speed_rad_s",l->seen?l->speed:NAN);
            num(joint,"range_entry_target_deg",l->seen&&s->joint_seen[i]?range_target(d,i):NAN);
            flag(joint,"limits_fresh",l->seen&&now-l->at<=60); cJSON_AddItemToArray(joints,joint);
            num(joint,"limits_age_s",l->seen?now-l->at:NAN);
            struct piper_acceleration *a=&f->acceleration[i];
            num(joint,"max_acceleration_rad_s2",a->seen?a->max:NAN);
            num(joint,"acceleration_age_s",a->seen?now-a->at:NAN);
            flag(joint,"acceleration_fresh",a->seen&&now>=a->at&&now-a->at<=60);
        }
        put(j,"joints",joints);
        cJSON *grip=cJSON_CreateObject(); num(grip,"width_mm",f->gripper_seen?f->gripper_mm:NAN);
        num(grip,"max_width_mm",f->gripper_config_seen?f->gripper_max_mm:NAN);
        num(grip,"configuration_age_s",f->gripper_config_seen?now-f->gripper_config_at:NAN);
        flag(grip,"configuration_fresh",f->gripper_config_seen&&now-f->gripper_config_at>=0&&now-f->gripper_config_at<=60&&(f->gripper_max_mm==70||f->gripper_max_mm==100));
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

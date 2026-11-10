#define _GNU_SOURCE
#include "controller_internal.h"
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
#endif

/* Named control timeouts (seconds). */
static const double lease_timeout_s=.8;        /* heartbeat lease */
static const double follow_input_timeout_s=.25; /* live following input */
static const double stationary_dwell_s=.4;     /* strict stillness before power/reset/teach/verify */
static const double pose_dwell_s=.3;           /* quiet measured pose before ordinary motion */
static const double settle_hold_s=.4;          /* verified completion window */

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
/* Persistent recovery latch (C-C): one JSON record per device id.
     "state": "clear" -> "latched"        an action intent, STOP or power cleanup
              "clear"|"latched" -> "reset_pending"   reset requested, unverified
              "latched"|"reset_pending" -> "clear"  only via clear_latch after a
                                                     verified recovery
     "kind":  why it is latched (intent, stop, obstruction, power, reset); empty
              when clear. Recovery eligibility uses kind, never message text.
              While latched it only moves toward the stricter value
              (intent < obstruction < power < stop < reset): cleanup after an
              operator STOP cannot turn the record back into an obstruction.
     "reason": the operator-facing text of the latch; empty when clear.
     "kind_reason": the reason that "kind" was set for. This build writes
              reason and kind_reason together, so a mismatch means an older
              build rewrote reason and kind must be derived again.
     "stop_event": the first STOP's evidence (reason, kind, joints, transport).
     "stop_event_active": unacknowledged STOP evidence; it implies a latch,
              so normalizing a clear record with active evidence latches it.
   A separate legacy motion-stops.json entry also blocks until acknowledged.
   The "blocked"/"reset_pending" booleans are still written so an older build
   reading this file stays blocked. Records written before "state"/"kind"
   existed are normalized once from those booleans and reason strings. */
#define RECORD_CLEAR "clear"
#define RECORD_LATCHED "latched"
#define RECORD_RESET_PENDING "reset_pending"
#define KIND_INTENT "intent"
#define KIND_STOP "stop"
#define KIND_OBSTRUCTION "obstruction"
#define KIND_POWER "power"
#define KIND_RESET "reset"
static const char intent_reason[]="Control action interrupted before verified completion";
static const char obstruction_prefix[]="Possible obstruction at J";
static bool record_state_valid(const char *state)
{
    return !strcmp(state,RECORD_CLEAR)||!strcmp(state,RECORD_LATCHED)||!strcmp(state,RECORD_RESET_PENDING);
}
/* Strictness of a kind: 0 for a clear record's empty kind, then the known
   kinds from the one with the most recovery paths to the one with the fewest.
   -1 is not a kind this build knows. */
static int kind_rank(const char *kind)
{
    const char *order[]={"",KIND_INTENT,KIND_OBSTRUCTION,KIND_POWER,KIND_STOP,KIND_RESET};
    for (int i=0;i<(int)(sizeof order/sizeof order[0]);++i) if (!strcmp(kind,order[i])) return i;
    return -1;
}
/* The kind a latched record keeps when another latch offers `offered`. A
   value this build does not know counts as the plain STOP kind. */
static const char *stricter_kind(const char *held,const char *offered)
{
    if (kind_rank(held)<0) held=KIND_STOP;
    if (kind_rank(offered)<0) offered=KIND_STOP;
    return kind_rank(held)>kind_rank(offered)?held:offered;
}
/* Legacy derivation only: string matching is confined to records that predate
   the explicit kind field. */
static const char *legacy_kind(const cJSON *r)
{
    const char *reason=text_field(r,"reason");
    if (!yes(r,"blocked")) return "";
    if (yes(r,"reset_pending")) return KIND_RESET;
    if (!strcmp(reason,intent_reason)) return KIND_INTENT;
    if (!strncmp(reason,obstruction_prefix,sizeof obstruction_prefix-1)) return KIND_OBSTRUCTION;
    if (!strcmp(reason,"Motors disabled; recovery verification required")) return KIND_POWER;
    return KIND_STOP;
}
/* An older build rewrites only the booleans and reason and keeps unknown
   fields, so after a rollback and upgrade "state"/"kind" can be stale.
   - state: any disagreement with the booleans resolves to the stricter
     reading (latched if either side says so, reset-pending likewise).
     Active STOP evidence also says latched, so "stop_event_active" without
     a latch cannot survive normalization.
   - kind: "kind_reason" records the reason kind was set for. A reason that
     no longer matches it was written by an older build, so kind is derived
     again from that text. Rewording messages in code never trips this,
     because this build always writes reason and kind_reason together. */
static void record_normalize(cJSON *r)
{
    const char *state=text_field(r,"state");
    bool known=record_state_valid(state);
    bool latched=yes(r,"blocked")||yes(r,"reset_pending")||yes(r,"stop_event_active")||(known&&strcmp(state,RECORD_CLEAR));
    bool pending=yes(r,"reset_pending")||(known&&!strcmp(state,RECORD_RESET_PENDING));
    const char *derived=!latched?RECORD_CLEAR:pending?RECORD_RESET_PENDING:RECORD_LATCHED;
    bool state_stale=!known||strcmp(state,derived)||yes(r,"blocked")!=latched||yes(r,"reset_pending")!=pending;
    if (state_stale) { str(r,"state",derived); flag(r,"blocked",latched); flag(r,"reset_pending",pending); }
    bool kind_stale=state_stale||!field(r,"kind")||(field(r,"kind_reason")&&strcmp(text_field(r,"kind_reason"),text_field(r,"reason")));
    if (kind_stale) str(r,"kind",legacy_kind(r));
    if (kind_stale||!field(r,"kind_reason")) str(r,"kind_reason",text_field(r,"reason"));
    cJSON *event=field(r,"stop_event");
    if (cJSON_IsObject(event)&&!field(event,"kind"))
        str(event,"kind",!strncmp(text_field(event,"reason"),obstruction_prefix,sizeof obstruction_prefix-1)?KIND_OBSTRUCTION:KIND_STOP);
}
static cJSON *record(struct controller *c,const char *id)
{
    cJSON *records=field(c->saved,"devices"),*r=field(records,id);
    if (!r) { r=cJSON_CreateObject(); cJSON_AddItemToObject(records,id,r); }
    record_normalize(r);
    return r;
}
static bool record_is(const cJSON *r,const char *state) { return !strcmp(text_field(r,"state"),state); }
/* The active incident's reason and kind, or the fallback when none is active. */
static const char *active_incident(struct controller *c,const char *id,const char *fallback,const char **kind)
{
    cJSON *r=record(c,id),*event=field(r,"stop_event");
    const char *reason=text_field(event,"reason");
    if (!yes(r,"stop_event_active")||!*reason) return fallback;
    *kind=text_field(event,"kind"); return reason;
}
static const cJSON *legacy_stop(struct controller *c,const char *id)
{
    const cJSON *item;
    cJSON_ArrayForEach(item,field(c->legacy_stops,"stops"))
        if (!strcmp(text_field(item,"device_id"),id)) return item;
    return NULL;
}
static bool legacy_unacknowledged(struct controller *c,const char *id)
{
    const cJSON *old=legacy_stop(c,id);
    return old&&strcmp(text_field(record(c,id),"legacy_ack"),text_field(old,"at"));
}
static bool blocked(struct controller *c,const char *id)
{
    return !record_is(record(c,id),RECORD_CLEAR)||legacy_unacknowledged(c,id);
}
static bool reset_pending(struct controller *c,const char *id)
{
    return record_is(record(c,id),RECORD_RESET_PENDING)||
        (legacy_unacknowledged(c,id)&&!strcmp(text_field(legacy_stop(c,id),"source"),"reset_intent"));
}
/* Latch the in-memory record. The caller persists it before reporting success. */
static void latch_record(struct controller *c,const char *id,const char *reason,const char *kind,bool pending)
{
    cJSON *r=record(c,id);
    const char *kept=record_is(r,RECORD_CLEAR)?kind:stricter_kind(text_field(r,"kind"),kind);
    char next[16]; snprintf(next,sizeof next,"%s",kept);
    str(r,"state",pending?RECORD_RESET_PENDING:RECORD_LATCHED); str(r,"kind",next); str(r,"reason",reason); str(r,"kind_reason",reason);
    flag(r,"blocked",true); flag(r,"reset_pending",pending);
}
static bool latch(struct controller *c,const char *id,const char *reason,const char *kind,bool pending)
{
    latch_record(c,id,reason,kind,pending); return persist(c);
}
static bool clear_latch(struct controller *c,const char *id)
{
    cJSON *r=record(c,id); const cJSON *old=legacy_stop(c,id);
    str(r,"state",RECORD_CLEAR); str(r,"kind",""); str(r,"reason",""); str(r,"kind_reason","");
    flag(r,"blocked",false); flag(r,"reset_pending",false);
    flag(r,"stop_event_active",false);
    if (old) str(r,"legacy_ack",text_field(old,"at"));
    return persist(c);
}
static void message(struct controller_lane *l,const char *phase,const char *format,...)
{
    snprintf(l->phase,sizeof l->phase,"%s",phase);
    va_list args; va_start(args,format); vsnprintf(l->message,sizeof l->message,format,args); va_end(args);
    fprintf(stderr,"%s: %s\n",phase,l->message);
}
/* The single exit from every lane operation. It retires the whole operation
   sub-state, so a later STOP never records stale follow/trajectory flags (B9).
   Lane identity, the last message/error, request identity, query state and
   the teaching-cycle evidence are separate machines and are kept. */
static void lane_go_idle(struct controller_lane *l)
{
    l->op=OP_IDLE; l->owner[0]=0;
    l->stage=STAGE_NONE; l->pose_phase=POSE_NONE;
    l->follow=FOLLOW_NONE; l->recovering_at_pose=false;
    l->settled_at=l->final_sent_at=0;
}
static bool cycle_stage(enum lane_stage stage) { return stage>=STAGE_CYCLE_RAISING&&stage<=STAGE_CYCLE_RETURNING; }
static bool teach_stage(enum lane_stage stage) { return stage>=STAGE_TEACH_STANDBY&&stage<=STAGE_TEACH_MODE; }
static bool teach_seamless(const struct controller_lane *l) { return l->stage==STAGE_TEACH_SEEDED||l->stage==STAGE_TEACH_MODE; }
/* The op each stage refines; STAGE_NONE refines none. */
static enum operation stage_op(enum lane_stage stage)
{
    switch (stage) {
    case STAGE_NONE: return OP_IDLE;
    case STAGE_CYCLE_RAISING: case STAGE_CYCLE_UPRIGHT: case STAGE_CYCLE_RETURNING: return OP_CYCLE;
    case STAGE_TEACH_STANDBY: case STAGE_TEACH_CAN: case STAGE_TEACH_SEEDED: case STAGE_TEACH_MODE: return OP_TEACH_RETURN;
    case STAGE_ALIGN_FOLLOWUP: return OP_ALIGN;
    case STAGE_MOVE_SYNCHRONIZED: case STAGE_MOVE_TRAJECTORY: return OP_MOVE;
    }
    return OP_IDLE;
}
static bool position_op(enum operation op) { return op==OP_MOVE||op==OP_ALIGN||op==OP_CYCLE; }
static bool arrival_holding(const struct controller_lane *l) { return l->pose_phase==POSE_ARRIVAL_HOLD; }
static bool trajectory_following(const struct controller_lane *l) { return l->stage==STAGE_MOVE_TRAJECTORY; }
/* Cycle legs and synchronized moves interpolate every joint along one leg. */
static bool leg_interpolated(const struct controller_lane *l) { return l->op==OP_CYCLE||l->stage==STAGE_MOVE_SYNCHRONIZED; }
/* A new leg or an operator hold retires a pending arrival hold. Seeding is
   kept: the frozen pose must still go out before any new destination. */
static void leave_arrival_hold(struct controller_lane *l)
{
    if (l->pose_phase==POSE_ARRIVAL_HOLD) l->pose_phase=POSE_TRACKING;
    l->arrival_hold_at=0;
}
/* The C-A invariants the enums cannot express. Returns the first broken one. */
static const char *lane_violation(const struct controller_lane *l)
{
    if (l->stage!=STAGE_NONE&&stage_op(l->stage)!=l->op) return "stage does not refine the active operation";
    if (l->op==OP_CYCLE&&!cycle_stage(l->stage)) return "cycle without a cycle stage";
    if (l->op==OP_TEACH_RETURN&&!teach_stage(l->stage)) return "teaching return without a teaching stage";
    if (l->follow!=FOLLOW_NONE&&l->op!=OP_MOVE) return "following outside a move";
    if (l->follow!=FOLLOW_NONE&&l->stage!=STAGE_NONE&&l->stage!=STAGE_MOVE_TRAJECTORY) return "following with a non-following move stage";
    if (trajectory_following(l)&&l->follow!=FOLLOW_LIVE) return "trajectory reference without live following";
    if ((l->pose_phase!=POSE_NONE)!=position_op(l->op)) return "pose phase does not match the operation";
    if (arrival_holding(l)&&l->op!=OP_MOVE&&l->op!=OP_ALIGN) return "arrival hold outside a move or range entry";
    if (l->pose_phase==POSE_SEEDING&&l->follow!=FOLLOW_NONE) return "seed pending during following";
    if (l->recovering_at_pose&&!(l->op==OP_TEACH_RETURN&&teach_seamless(l))) return "recover-at-pose outside a seamless teaching return";
    if (l->op==OP_IDLE&&(l->settled_at||l->final_sent_at||*l->owner)) return "idle lane keeps operation timers or owner";
    return NULL;
}
/* Lanes (C-F): every lane operation receives its struct controller_lane *
   explicitly; there is no selected-lane copy to commit or restore. lane_for
   creates a device's lane on first use (writers only); readers use lane_find
   and fall back to the shared idle presentation. */
static void lane_reset(struct controller_lane *l,const char *id,const char *text)
{
    memset(l,0,sizeof *l);
    snprintf(l->lane_device,sizeof l->lane_device,"%s",id);
    snprintf(l->phase,sizeof l->phase,"monitor");
    snprintf(l->message,sizeof l->message,"%s",text);
}
static const char lane_default_message[]="Monitoring this robot. No movement has been requested.";
static struct controller_lane *lane_find(struct controller *c,const char *id)
{
    if (!id||!*id) return NULL;
    for (unsigned i=0;i<CANBUS_MAX;++i) if (!strcmp(c->lanes[i].lane_device,id)) return &c->lanes[i];
    return NULL;
}
static struct controller_lane *lane_for(struct controller *c,const char *id)
{
    if (!id||!*id||strlen(id)>=sizeof c->lanes[0].lane_device) return NULL;
    struct controller_lane *lane=lane_find(c,id);
    if (lane) return lane;
    for (unsigned i=0;i<CANBUS_MAX;++i) if (!*c->lanes[i].lane_device) {
        lane_reset(&c->lanes[i],id,lane_default_message); return &c->lanes[i];
    }
    return NULL;
}
/* A read-only view of id's lane, or an idle presentation when it has none. */
static const struct controller_lane *lane_view(struct controller *c,const char *id,struct controller_lane *scratch)
{
    const struct controller_lane *lane=lane_find(c,id);
    if (lane) return lane;
    if (id&&*id) lane_reset(scratch,id,lane_default_message); else memset(scratch,0,sizeof *scratch);
    return scratch;
}
struct controller_lane *controller_lane(struct controller *c,const char *id) { return lane_for(c,id); }
static void discover_lanes(struct controller *c)
{
    for (unsigned i=0;i<CANBUS_MAX;++i) if (*c->bus.devices[i].id) lane_for(c,c->bus.devices[i].id);
    if (*c->selected) lane_for(c,c->selected);
}
bool controller_any_motion_active(const struct controller *c)
{
    for (unsigned i=0;i<CANBUS_MAX;++i) if (*c->lanes[i].lane_device&&c->lanes[i].op!=OP_IDLE) return true;
    return false;
}
bool controller_any_query_active(const struct controller *c)
{
    for (unsigned i=0;i<CANBUS_MAX;++i) if (*c->lanes[i].lane_device&&c->lanes[i].query) return true;
    return false;
}
/* These are feedback states, not permission to send ctrl_mode=2 (which is not
   a supported command in the current vendor SDK). The physical teach button
   retains priority. Read-only scene tracking works in every fresh state. */
enum teaching_phase { TEACH_UNKNOWN, TEACH_STOPPED, TEACH_HAND_GUIDING, TEACH_PLAYBACK, TEACH_PLAYBACK_PAUSED, TEACH_ENDED, TEACH_OFF };
static const struct { const char *name,*label; } teaching_phases[]={
    [TEACH_UNKNOWN]={"unknown","Unknown teaching state; controls withheld"},
    [TEACH_STOPPED]={"stopped","Controller STOP is latched; support the arm and inspect recovery before resuming"},
    [TEACH_HAND_GUIDING]={"hand-guiding","Hand-guiding / onboard teaching record"},
    [TEACH_PLAYBACK]={"playback","Onboard teaching playback"},
    [TEACH_PLAYBACK_PAUSED]={"playback-paused","Onboard teaching playback paused"},
    [TEACH_ENDED]={"ended","Teaching ended; return to CAN required"},
    [TEACH_OFF]={"off","CAN / standby control"},
};
static enum teaching_phase teaching_phase(const struct piper_sample *s)
{
    if (!s->status_seen) return TEACH_UNKNOWN;
    /* STOP can retain the last teach byte even after the physical light is
       off. Present the actual controller state, not a healthy teaching mode.
       The conservative raw teaching/admission guards below are unchanged. */
    if (s->arm_status==PIPER_ARM_STATUS_STOPPED) return TEACH_STOPPED;
    if (s->teach==PIPER_TEACH_RECORDING) return TEACH_HAND_GUIDING;
    if (piper_teach_playback(s->teach)) return TEACH_PLAYBACK;
    if (s->teach==PIPER_TEACH_PLAYBACK_PAUSED) return TEACH_PLAYBACK_PAUSED;
    if (s->teach>PIPER_TEACH_LAST_KNOWN) return TEACH_UNKNOWN;
    return s->control==PIPER_CONTROL_TEACH?TEACH_ENDED:TEACH_OFF;
}
static bool teaching_active(const struct piper_sample *s)
{
    return s->status_seen&&piper_teach_active(s->teach);
}
static bool teaching_status(const struct piper_sample *s)
{
    return (s->teach==PIPER_TEACH_RECORDING&&s->arm_status==PIPER_ARM_STATUS_TEACHING)||
        (piper_teach_playback(s->teach)&&s->arm_status==PIPER_ARM_STATUS_PLAYBACK)||
        (s->teach==PIPER_TEACH_PLAYBACK_PAUSED&&s->arm_status==PIPER_ARM_STATUS_PLAYBACK_PAUSED);
}
/* The first transport/link problem that blocks control, or NULL when ready. */
static const char *transport_problem(const struct can_device *d)
{
    if (!d->present) return "USB adapter is missing.";
    if (!d->up||!can_bus_transport_open(d)) return "CAN socket unavailable. Check interface state and bitrate.";
    if (!d->stable) return "A unique USB adapter identity is required for control.";
    if (!d->link_verified||d->bitrate!=1000000) return "CAN link must be verified at 1 Mbit/s before control.";
    if (d->bus_state>=CAN_STATE_BUS_OFF||(d->control_flags&(CAN_CTRLMODE_LOOPBACK|CAN_CTRLMODE_LISTENONLY|CAN_CTRLMODE_FD))) return "CAN controller is bus-off or configured in an unsupported mode.";
    return NULL;
}
static bool teaching_benign(const struct can_device *d,double now)
{
    const struct piper_sample *s=&d->feedback.arm[0];
    if (transport_problem(d)||!piper_fresh(s,now)||s->error||!teaching_active(s)) return false;
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
    const char *transport=transport_problem(d); if (transport) return transport;
    struct piper_feedback *f=&d->feedback; struct piper_sample *s=&f->arm[0];
    if (!piper_fresh(s,now)) return "Waiting for fresh controller and all six joint readings.";
    for (unsigned i=1;i<3;++i) if (f->arm[i].status_seen&&piper_recent(f->arm[i].status_at,now)) return "Multiple arm address ranges detected; one standalone arm per adapter is required.";
    if (f->foreign_seen&&now-f->foreign_at<2) return "Another controller is sending commands on this CAN bus.";
    /* During a requested reset, observe transient MCU status without sending
       another stop into the reset sequence. All transport/driver checks remain
       active, and completion below still requires zero error and normal status. */
    if (check!=CHECK_RESET&&(s->error||(!teaching_status(s)&&(((check==CHECK_CONTROL||check==CHECK_TEACH_RETURN)&&s->arm_status)||s->arm_status>1)))) {
        if (s->arm_status==PIPER_ARM_STATUS_JOINT_COMMUNICATION||(s->error&0x3f)) return "Robot reports a joint-drive communication fault. Motion stopped; inspect the saved fault before recovery.";
        return "The arm reports a fault or stop. Inspect status and recover before control.";
    }
    for (unsigned i=0;i<6;++i) {
        if (!isfinite(s->deg[i])||fabs(s->deg[i])>360) return "Joint feedback is outside the supported physical range.";
        if (!f->motor[i].seen||!piper_recent(f->motor[i].at,now)) return "Waiting for fresh feedback from all six motor drivers.";
        if (f->motor[i].fault) return "A motor driver reports a fault.";
        if ((check==CHECK_CONTROL||check==CHECK_TEACH_RETURN) && !d->simulated) {
            struct piper_motor *m=&f->motor[i];
            if (!m->high_seen || !piper_recent(m->high_at,now)) return "Waiting for fresh motor encoder feedback.";
            double encoder_deg=m->position_raw*180.0/(1000.0*PIPER_PI);
            if (fabs(encoder_deg-s->deg[i])>2) return "Joint feedback disagrees with motor encoders. Waiting for the actual measured pose.";
        }
    }
    if (!*f->hardware||!*f->version) return "Firmware has not been read on this connection. Click Read firmware & limits.";
    if (!piper_supported(f)) return "The reported hardware/firmware is not supported for motion. See the detected versions in Arm connection.";
    if (check==CHECK_CONTROL&&teaching_active(s)) return "Onboard teaching is active. Move the arm by hand while the scene follows; dashboard motion is paused.";
    if (check==CHECK_CONTROL&&s->control==2) return piper_seamless_teach_supported(f)?
        "Teaching ended. Turn the teach light off and hold still; return to CAN preserves the measured pose and discards old targets.":
        "Teaching ended with the controller still in teach mode. Return to physical zero, turn the teach light off, then use Return to CAN control.";
    if (check==CHECK_CONTROL&&(s->control>1||!piper_teach_idle(s->teach)||(s->motion_mode!=1&&!(s->control==0&&s->motion_mode==0)))) return "Arm must be in standby/CAN MoveJ mode with teaching inactive.";
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
static bool limit_valid(const struct piper_limit *l)
{
    return isfinite(l->min)&&isfinite(l->max)&&isfinite(l->speed)&&l->min<l->max&&l->min>=-360&&l->max<=360&&l->speed>0&&l->speed<=3;
}
static bool acceleration_valid(const struct piper_acceleration *a) { return isfinite(a->max)&&a->max>0&&a->max<=5; }
static bool limits_valid(const struct piper_limit *l,const struct piper_acceleration *a) { return limit_valid(l)&&acceleration_valid(a); }
const char *controller_blocker(struct controller *c,struct can_device *d,double now)
{
    /* Every freshness check below compares against a clock that has stopped. */
    if (piper_clock_failed()) return "The monotonic clock failed. Restart the service; motion remains blocked.";
    const char *why=health(d,now,CHECK_CONTROL); if (why) return why;
    if (c->storage_failed) return "Control-state storage failed. Repair storage and restart; motion remains blocked.";
    if (blocked(c,d->id)) return "Stopped earlier. Click Enable controls to verify the current robot state.";
    for (unsigned i=0;i<6;++i) {
        struct piper_limit *l=&d->feedback.limit[i];
        struct piper_acceleration *a=&d->feedback.acceleration[i];
        if (!configuration_current(d,l->seen,l->at,now)) return "Reading joint limits for this connection.";
        if (!limit_valid(l)) return "Controller returned invalid joint limits or maximum speed.";
        if (!configuration_current(d,a->seen,a->at,now)) return "Reading acceleration limits for this connection.";
        if (!acceleration_valid(a)) return "An acceleration limit is outside the supported 0–5 rad/s² range. Inspect firmware settings before motion.";
    }
    return NULL;
}
/* Encoder drift at a limit does not require a separate alignment action.
   This allowance applies only to feedback; every moving target stays in range.
   The one-shot entry seed preserves the unchanged measured pose before mode. */
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
/* Read paths call this, so it writes only the caller's buffer, never l->error. */
static const char *range_reason(const struct can_device *d,char *buffer,size_t size)
{
    for (unsigned i=0;i<6;++i) if (joint_outside_range(d,i)) {
        snprintf(buffer,size,"J%u reads %.3f degrees outside %.3f to %.3f. Move that joint inward or explicitly move all joints to zero; every CAN position command contains all six joints.",i+1,d->feedback.arm[0].deg[i],d->feedback.limit[i].min,d->feedback.limit[i].max);
        return buffer;
    }
    return "No range correction is required.";
}
static double clamp_limit(const struct piper_limit *l,double degrees) { return fmin(l->max,fmax(l->min,degrees)); }
/* The measured pose as a hold target: joints within the feedback slop of a
   limit are clamped onto it; a farther joint keeps its reading (and then
   fails valid_target). */
static void measured_hold_target(const struct can_device *d,double target[6])
{
    for (unsigned i=0;i<6;++i) {
        const struct piper_limit *l=&d->feedback.limit[i]; double angle=d->feedback.arm[0].deg[i];
        target[i]=angle>=l->min-limit_feedback_slop_deg&&angle<=l->max+limit_feedback_slop_deg?clamp_limit(l,angle):angle;
    }
}
static bool valid_target(struct controller_lane *l,struct can_device *d,const double targets[6])
{
    for (unsigned i=0;i<6;++i) {
        const struct piper_limit *lim=&d->feedback.limit[i];
        if (!isfinite(targets[i])||targets[i]<lim->min||targets[i]>lim->max) {
            snprintf(l->error,sizeof l->error,"J%u target %.3f° is outside %.1f° to %.1f°.",i+1,targets[i],lim->min,lim->max); return false;
        }
    }
    return true;
}
/* Every hold retargets the lane through here, so each one applies the same
   limit clamp. False leaves the reason in l->error; the caller stops. */
static bool hold_measured_pose(struct controller_lane *l,struct can_device *d)
{
    measured_hold_target(d,l->target);
    if (!valid_target(l,d,l->target)) return false;
    memcpy(l->command,l->target,sizeof l->command); return true;
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
    if (can_bus_strict_still_for(d,now)<stationary_dwell_s) return false;
    for (unsigned i=0;i<6;++i) {
        struct piper_motor *m=&d->feedback.motor[i];
        if (!m->high_seen||m->high_at<=after||!piper_recent(m->high_at,now)||!isfinite(m->velocity)||fabs(m->velocity)>.01) return false;
    }
    return true;
}
static bool position_control_stationary(struct can_device *d,double now)
{
    return can_bus_pose_still_for(d,now)>=pose_dwell_s;
}
static void forget_teaching_cycle(struct controller_lane *l)
{
    l->teaching_cycle=TEACH_CYCLE_NONE; l->teaching_cycle_generation=0;
    l->teaching_cycle_seen=l->teaching_cycle_completed=0;
}
static void observe_teaching_cycle(struct controller_lane *l,struct can_device *d,double now)
{
    /* The evidence lives in this device's own lane, so only the connection
       generation needs to match. */
    if (!d||l->teaching_cycle_generation!=d->generation) {
        forget_teaching_cycle(l);
        if (!d) return;
        l->teaching_cycle_generation=d->generation;
    }
    const struct piper_sample *s=&d->feedback.arm[0];
    /* Health includes fresh status, six joint/motor samples, encoder agreement,
       a unique bus and firmware identity. Observation never sends a packet. */
    if (health(d,now,CHECK_TEACH_RETURN)) { forget_teaching_cycle(l); return; }
    if (s->control==PIPER_CONTROL_TEACH&&s->teach==PIPER_TEACH_RECORDING&&s->arm_status==PIPER_ARM_STATUS_TEACHING&&teaching_benign(d,now)) {
        l->teaching_cycle=TEACH_CYCLE_SEEN; l->teaching_cycle_seen=s->status_at; l->teaching_cycle_completed=0; return;
    }
    if (s->control==PIPER_CONTROL_TEACH&&!s->arm_status&&piper_teach_idle(s->teach)) {
        if (l->teaching_cycle==TEACH_CYCLE_SEEN) {
            if (s->status_at>l->teaching_cycle_seen&&piper_recent(l->teaching_cycle_seen,now)) {
                l->teaching_cycle=TEACH_CYCLE_COMPLETED; l->teaching_cycle_completed=s->status_at;
            } else if (!piper_recent(l->teaching_cycle_seen,now)) forget_teaching_cycle(l);
        }
        if (l->teaching_cycle==TEACH_CYCLE_COMPLETED&&now-l->teaching_cycle_completed>120) forget_teaching_cycle(l);
        return;
    }
    forget_teaching_cycle(l);
}
static bool teaching_zero(const struct can_device *d)
{
    for (unsigned i=0;i<6;++i) if (!isfinite(d->feedback.arm[0].deg[i])||fabs(d->feedback.arm[0].deg[i])>1) return false;
    return true;
}
static const char *teaching_return_guard(struct controller *c,const struct controller_lane *l,struct can_device *d,double now,bool recovery)
{
    if (c->storage_failed) return "Control-state storage failed; repair storage before teaching handoff.";
    const char *why=health(d,now,CHECK_TEACH_RETURN); if (why) return why;
    struct piper_sample *s=&d->feedback.arm[0];
    if (!piper_teach_idle(s->teach)) return "Turn off the physical teach button and finish onboard teaching/playback before returning to CAN.";
    if (s->control>2||s->motion_mode>1) return "Only a completed onboard teaching state can return to CAN through this action.";
    if (reset_pending(c,d->id)) return "A reset is pending; complete its separate recovery flow before returning to CAN.";
    if (recovery) {
        if (!piper_seamless_teach_supported(&d->feedback)) return "This firmware cannot restore a measured-pose hold without a reset; use its separate recovery flow.";
        cJSON *r=record(c,d->id),*event=field(r,"stop_event"),*transport=field(event,"transport");
        bool obstruction=record_is(r,RECORD_LATCHED)&&yes(r,"stop_event_active")&&!legacy_unacknowledged(c,d->id)&&
            !strcmp(text_field(r,"kind"),KIND_OBSTRUCTION)&&!strcmp(text_field(event,"kind"),KIND_OBSTRUCTION)&&
            !field(r,"last_fault")&&!*d->problem&&!*text_field(transport,"failure")&&!*text_field(transport,"connection_error")&&
            cJSON_IsNumber(field(event,"error"))&&field(event,"error")->valuedouble==0&&
            cJSON_IsNumber(field(event,"arm_status"))&&field(event,"arm_status")->valuedouble==0;
        if (!obstruction) return "This stop is not a software obstruction with healthy saved evidence; inspect its separate recovery instructions.";
        if (!(l->recovering_at_pose&&l->op==OP_TEACH_RETURN)) {
            if (l->teaching_cycle_generation!=d->generation||
                l->teaching_cycle!=TEACH_CYCLE_COMPLETED||now-l->teaching_cycle_completed>120)
                return "Single-click the physical teach button, hand-guide to a clear pose, then single-click it off. The plugin must observe that cycle on this connection.";
            if (s->control!=2) return "A completed physical hand-guiding cycle is required before restoring the measured pose.";
        }
        if (range_distance(d)>0) return "Hand-guide every joint inside its reported range before restoring the measured pose.";
    } else if (blocked(c,d->id)) return "A recorded stop still requires its separate recovery flow before teaching handoff.";
    if (!arm_configuration_received(d,now)) return "Read current firmware and joint limits before returning to CAN.";
    for (unsigned i=0;i<6;++i) {
        if (!limits_valid(&d->feedback.limit[i],&d->feedback.acceleration[i])) return "Robot configuration is invalid; inspect it before teaching handoff.";
    }
    bool modern=piper_seamless_teach_supported(&d->feedback);
    if (modern) {
        /* This exception permits only the already measured hold pose. Ordinary
           targets still pass valid_target and must stay inside reported limits.
           Match the existing bounded range-entry allowance, without snapping,
           moving outward, homing, or changing the encoder's zero. */
        if (range_distance(d)>3) return "A joint requires more than 3 degrees of range correction; inspect the physical pose before teaching handoff.";
        if (!enabled(d,true,0)) return "All six motors must report enabled for a same-pose teaching handoff. This action never changes motor power.";
        if (l->op!=OP_TEACH_RETURN&&!d->simulated&&!measured_stationary(d,now,0)) return "Hold the arm still until all six motor encoders confirm stationary feedback for 0.4 seconds.";
    } else {
        if (!teaching_zero(d)) return "Return every physical joint to its zero pose before switching from teaching to CAN (application guard: within 1 degree).";
        if (range_distance(d)>0) return "A joint is outside its reported command range; reposition it by hand before returning to CAN.";
        if (!enabled(d,true,0)&&!enabled(d,false,0)) return "Wait for a consistent measured motor power state before teaching handoff.";
    }
    /* Establish stillness before the first packet. During the bounded mode
       exchange, fresh zero/range feedback and the 0.5-degree initial-pose guard
       permit minor supported settling without restarting this timer. */
    if (l->op!=OP_TEACH_RETURN&&can_bus_strict_still_for(d,now)<stationary_dwell_s) return "Keep the measured pose stationary for 0.4 seconds before teaching handoff.";
    return NULL;
}
/* A hardware button may preempt a leased move. Do not fight that button with a
   STOP/reset or preserve an old target for later replay. Only this operation's
   ordinary interrupted-action intent can be cleared; real incidents stay. */
static bool relinquish_to_teaching(struct controller *c,struct controller_lane *l,struct can_device *d,double now)
{
    if (l->op==OP_IDLE||l->op==OP_TEACH_RETURN||!d||d->generation!=l->generation||!teaching_benign(d,now)) return false;
    /* Reset/status packets can retain the last teach flag after the button is
       off. Only an actual teaching status may preempt a requested recovery;
       otherwise keep observing its fresh acknowledgement instead of silently
       retiring the reset with reset_pending still latched. */
    if ((l->op==OP_RESET||l->op==OP_DISABLE)&&!teaching_status(&d->feedback.arm[0])) return false;
    cJSON *r=record(c,d->id);
    bool own_intent=!strcmp(text_field(r,"kind"),KIND_INTENT)&&!yes(r,"stop_event_active")&&!reset_pending(c,d->id);
    lane_go_idle(l);
    if (own_intent&&!clear_latch(c,d->id)) message(l,"blocked","Teaching took over, but state storage failed; previous target discarded.");
    else message(l,"teaching","Onboard teaching took control. The previous movement and lease were discarded; measured scene tracking continues.");
    return true;
}
static void abandon_teaching_return(struct controller_lane *l,const char *reason)
{
    bool modern=teach_seamless(l);
    lane_go_idle(l);
    message(l,"teaching","Return to CAN cancelled: %s %s",reason,modern?
        "Only the measured hold pose was requested; no reset, power change, homing or replay.":"No reset, power or position command was sent.");
}
static bool send_pose(struct controller *c,struct controller_lane *l,struct can_device *d,const double target[6])
{
    unsigned speed=l->op==OP_ALIGN||arrival_holding(l)?1:(unsigned)fmax(1,ceil(l->speed_percent));
    struct can_frame f[4]; f[0]=piper_mode(speed);
    if (!piper_targets(target,f+1)) { l->pose_tx_valid=false; return false; }
    int32_t encoded[6]; bool same=true;
    for (unsigned i=0;i<6;++i) { encoded[i]=(int32_t)llround(target[i]*1000); same=same&&encoded[i]==l->pose_tx_millidegrees[i]; }
    struct piper_sample *s=&d->feedback.arm[0];
    /* A Ready follower can receive the same reference indefinitely. Its lease,
       health, tracking and reference limits are still checked on every tick;
       do not fill USB queues with an already completely issued identical pose.
       A new session, generation, mode, speed or encoded angle always transmits. */
    if (l->follow!=FOLLOW_NONE&&l->pose_tx_valid&&l->pose_tx_generation==d->generation&&
        l->pose_tx_speed==speed&&l->pose_tx_mode==f[0].data[3]&&same&&piper_fresh(s,piper_now())&&s->control==1&&s->motion_mode==1&&
        !s->error&&!s->arm_status&&!teaching_active(s)&&enabled(d,true,0)) return true;
    l->pose_tx_valid=false;
    if (!can_bus_send(&c->bus,d,l->generation,f,4)) return false;
    l->pose_tx_valid=true; l->pose_tx_generation=d->generation; l->pose_tx_speed=speed;
    l->pose_tx_mode=f[0].data[3];
    l->pose_tx_at=piper_now();
    memcpy(l->pose_tx_millidegrees,encoded,sizeof encoded); return true;
}
/* Deliver one lane's STOP: capture the evidence, send the frame, latch the
   in-memory record and idle the lane. Nothing is written to disk here, so a
   STOP for several arms reaches every arm before the first fsync. Returns
   whether a record changed and must be persisted. kind is KIND_STOP or
   KIND_OBSTRUCTION and is kept with the event. */
static bool lane_stop_deliver(struct controller *c,struct controller_lane *l,const char *kind,const char *reason)
{
    forget_teaching_cycle(l); l->recovering_at_pose=false;
    l->stop_delivered=false;
    struct can_device *d=can_bus_find(&c->bus,l->lane_device);
    bool had_job=l->op!=OP_IDLE, sent=false;
    unsigned generation=had_job?l->generation:(d?d->generation:0);
    if (d) {
        cJSON *r=record(c,d->id);
        /* The first STOP's evidence is kept. It is marked active only once
           the record is latched below, so the record never reads as active
           evidence on a clear state. */
        bool incident=yes(r,"stop_event_active");
        if (!incident) {
            cJSON_DeleteItemFromObjectCaseSensitive(r,"last_fault");
            cJSON *event=cJSON_CreateObject(), *joints=cJSON_CreateArray();
            str(event,"reason",reason); str(event,"kind",kind); num(event,"unix_time",(double)time(NULL));
            num(event,"joint",l->obstruction_joint); num(event,"threshold_s",.5); num(event,"tolerance_deg",l->tolerance);
            num(event,"request_id",l->request_id); num(event,"speed_deg_s",l->speed);
            flag(event,"following",l->follow==FOLLOW_LIVE); flag(event,"finishing_target",l->follow==FOLLOW_COMMITTED);
            flag(event,"trajectory_follow",trajectory_following(l));
            num(event,"follow_sequence",l->follow_sequence);
            str(event,"phase_before_stop",l->phase);
            if (l->follow!=FOLLOW_NONE) {
                double observed=piper_now();
                num(event,"follow_input_age_s",observed-l->follow_updated);
                num(event,"heartbeat_age_s",observed-l->heartbeat); num(event,"tick_age_s",observed-l->last_tick);
            }
            num(event,"arm_status",d->feedback.arm[0].arm_status);
            num(event,"error",d->feedback.arm[0].error);
            cJSON *transport=cJSON_CreateObject();
            str(transport,"connection_error",d->problem); num(transport,"generation",d->generation);
            num(transport,"rx",(double)d->rx); num(transport,"tx",(double)d->tx);
            num(transport,"local",(double)d->local); num(transport,"errors",(double)d->errors);
            struct can_tx_failure *failure=&d->tx_failure;
            if (failure->seen&&failure->generation==generation&&(!had_job||failure->at>=l->started)) {
                str(transport,"failure",failure->problem); num(transport,"failed_frame_index",failure->frame_index);
                num(transport,"batch_frames",failure->batch_count); num(transport,"failed_can_id",failure->can_id);
                num(transport,"os_error",failure->os_error); num(transport,"transport_error",failure->transport_error);
                num(transport,"failed_generation",failure->generation); num(transport,"failure_monotonic_s",failure->at);
                num(transport,"rx_at_failure",(double)failure->rx); num(transport,"tx_at_failure",(double)failure->tx);
                num(transport,"local_at_failure",(double)failure->local); num(transport,"errors_at_failure",(double)failure->errors);
            }
            put(event,"transport",transport); /* Persist before STOP/reconnect can overwrite transport status. */
            for (unsigned i=0;i<6;++i) {
                cJSON *j=cJSON_CreateObject(); num(j,"joint",i+1);
                num(j,"actual_deg",d->feedback.arm[0].deg[i]);
                num(j,"command_deg",had_job?l->command[i]:NAN);
                num(j,"target_deg",had_job?l->target[i]:NAN);
                num(j,"current_a",d->feedback.motor[i].high_seen&&piper_recent(d->feedback.motor[i].high_at,piper_now())?d->feedback.motor[i].current:NAN);
                num(j,"driver_fault",d->feedback.motor[i].fault);
                num(j,"no_progress_s",had_job?piper_now()-l->obstruction[i].progress_at:NAN);
                num(j,"progress_reference_deg",had_job?l->obstruction[i].progress_position:NAN);
                cJSON_AddItemToArray(joints,j);
            }
            put(event,"joints",joints); put(r,"stop_event",event);
        }
        /* Save the robot's fault before a stop changes its reported state. */
        struct piper_sample *s=&d->feedback.arm[0];
        if (!field(r,"last_fault")&&s->status_seen&&piper_recent(s->status_at,piper_now())&&(s->arm_status||s->error)) {
            cJSON *fault=cJSON_CreateObject(); num(fault,"arm_status",s->arm_status);
            num(fault,"error",s->error); num(fault,"teach_state",s->teach);
            num(fault,"control",s->control); num(fault,"observed_monotonic_s",s->status_at);
            put(r,"last_fault",fault);
        }
        struct can_frame f=piper_stop_frame();
        sent=can_bus_send(&c->bus,d,generation,&f,1);
        if (l->op==OP_GRIPPER) { piper_gripper(0,.1,false,&f); sent=can_bus_send(&c->bus,d,generation,&f,1)&&sent; }
        /* Removing power is recovery cleanup, not a replacement for the
           incident that made cleanup necessary. Keep its primary evidence
           and reason while retaining the latest cleanup failure separately. */
        bool cleanup=l->op==OP_DISABLE;
        if(cleanup)str(r,"last_cleanup_error",reason);
        const char *latched_kind=kind,*latched=cleanup?active_incident(c,d->id,reason,&latched_kind):reason;
        latch_record(c,d->id,latched,latched_kind,reset_pending(c,d->id));
        flag(r,"stop_event_active",true);
    }
    lane_go_idle(l); l->query=QUERY_IDLE;
    l->stop_delivered=sent;
    message(l,"stopped","%s %s",reason,sent?"Controller STOP requested; holding torque may be lost.":"Stop delivery unavailable; physical stop may be needed.");
    return d!=NULL;
}
/* STOP one lane and save its incident. */
static void lane_stop(struct controller *c,struct controller_lane *l,const char *kind,const char *reason)
{
    if (lane_stop_deliver(c,l,kind,reason)) persist(c);
}
static void stop_lane(struct controller *c,struct controller_lane *l,const char *reason) { lane_stop(c,l,KIND_STOP,reason); }
/* The public STOP acts on the selected device's lane. */
void controller_stop(struct controller *c,const char *reason)
{
    struct controller_lane *l=lane_for(c,c->selected); if (l) lane_stop(c,l,KIND_STOP,reason);
}
void controller_stop_obstruction(struct controller *c,const char *reason)
{
    struct controller_lane *l=lane_for(c,c->selected); if (l) lane_stop(c,l,KIND_OBSTRUCTION,reason);
}
/* Runs after every action and tick on a live arm. A broken C-A invariant
   fails safe: it is logged, the lane is stopped and the recovery record is
   latched. It never aborts the daemon. Returns false when it stopped the lane. */
static bool lane_check(struct controller *c,struct controller_lane *l)
{
    const char *broken=lane_violation(l);
    if (!broken) return true;
    char reason[200]; snprintf(reason,sizeof reason,"Internal lane state is inconsistent (%s); action stopped.",broken);
    fprintf(stderr,"lane %s: invariant broken: %s\n",l->lane_device,broken);
    bool known=can_bus_find(&c->bus,l->lane_device)!=NULL;
    stop_lane(c,l,reason);
    /* lane_stop latches through the device record; a lane without a device
       still latches its own id so nothing resumes from this state. */
    if (!known&&*l->lane_device) latch(c,l->lane_device,reason,KIND_STOP,reset_pending(c,l->lane_device));
    return false;
}
static void finish(struct controller *c,struct controller_lane *l,struct can_device *d,const char *result,bool clear)
{
    lane_go_idle(l);
    if (clear&&!clear_latch(c,d->id)) message(l,"blocked","Action completed, but state could not be saved. Motion remains blocked.");
    else message(l,"complete","%s",result);
}
static void finish_teaching_return(struct controller *c,struct controller_lane *l,struct can_device *d)
{
    bool recovery=l->recovering_at_pose;
    forget_teaching_cycle(l);
    if (recovery) {
        cJSON *before=cJSON_Duplicate(c->saved,true);
        if (!before||!clear_latch(c,d->id)) {
            /* A verified physical hold can remain powered if disk I/O fails, but the
               software incident must remain latched in memory as well as on disk. */
            if (before) { cJSON_Delete(c->saved); c->saved=before; } else c->storage_failed=true;
            lane_go_idle(l);
            message(l,"blocked","The measured hold is acknowledged, but recovery could not be saved. The obstruction remains latched; previous targets were discarded.");
            return;
        }
        cJSON_Delete(before);
    }
    finish(c,l,d,recovery?"Obstruction cleared at the verified measured pose. CAN control restored; no reset, motor-power change, homing or replay.":
        "CAN control restored at the measured pose. Previous targets discarded; no reset, motor-power change, homing or replay.",false);
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
static bool controller_init_devices(struct controller *c,bool simulation,unsigned count,const char *directory,const char *legacy)
{
    memset(c,0,sizeof *c); can_bus_init(&c->bus,simulation);
    if (simulation&&!can_bus_init_simulated(&c->bus,count)) return false;
    if (strlen(directory)>=sizeof c->state_dir||strlen(legacy)>=sizeof c->state_dir) return false; /* Both feed 1024-byte file paths. */
    snprintf(c->state_dir,sizeof c->state_dir,"%s",directory);
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
        const char *keys[]={"blocked","reset_pending","stop_event_active"};
        for (unsigned i=0;i<3;++i) if (field(saved_record,keys[i])&&!cJSON_IsBool(field(saved_record,keys[i]))) return false;
        if (field(saved_record,"state")&&(!cJSON_IsString(field(saved_record,"state"))||!record_state_valid(text_field(saved_record,"state")))) return false;
        if (field(saved_record,"kind")&&(!cJSON_IsString(field(saved_record,"kind"))||kind_rank(text_field(saved_record,"kind"))<0)) return false;
        record_normalize(saved_record);
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
    discover_lanes(c);
    struct controller_lane *l=lane_find(c,c->selected);
    if (l) message(l,"monitor","Monitoring only. Read firmware and limits, then choose a control action.");
    return persist(c);
}
bool controller_init(struct controller *c,bool simulation,const char *directory,const char *legacy)
{
    return controller_init_devices(c,simulation,1,directory,legacy);
}
bool controller_init_simulated(struct controller *c,unsigned count,const char *directory,const char *legacy)
{
    return controller_init_devices(c,true,count,directory,legacy);
}
void controller_close(struct controller *c)
{
    discover_lanes(c);
    for (unsigned i=0;i<CANBUS_MAX;++i) if (*c->lanes[i].lane_device) {
        struct controller_lane *l=&c->lanes[i];
        relinquish_to_teaching(c,l,can_bus_find(&c->bus,l->lane_device),piper_now());
        if (l->op==OP_TEACH_RETURN) abandon_teaching_return(l,"Controller closing.");
        if (l->op!=OP_IDLE) stop_lane(c,l,"Controller shutting down.");
    }
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
static void begin(struct controller *c,struct controller_lane *l,struct can_device *d,const char *owner,enum operation op,double now)
{
    l->obstruction_joint=0;
    for (unsigned i=0;i<6;++i) obstruction_reset(&l->obstruction[i],now);
    l->op=op; l->generation=d->generation; l->started=l->heartbeat=l->last_tick=l->progress_at=now;
    l->sent_at=l->settled_at=l->final_sent_at=0; l->best_error=1e9; l->enable_count=0; l->request_id=++c->next_request_id;
    l->power_cursor=0;
    l->pose_tx_valid=false;
    memset(l->power_requested,0,sizeof l->power_requested);
    memset(l->power_acknowledged,0,sizeof l->power_acknowledged);
    memset(l->power_first_sent_at,0,sizeof l->power_first_sent_at);
    memset(l->power_last_sent_at,0,sizeof l->power_last_sent_at);
    memset(l->enable_baseline,0,sizeof l->enable_baseline);
    l->stage=STAGE_NONE;
    /* Admission moves a position op to POSE_SEEDING unless it follows. */
    l->pose_phase=position_op(op)?POSE_TRACKING:POSE_NONE; l->arrival_hold_at=0;
    l->recovering_at_pose=false;
    l->follow=FOLLOW_NONE; l->follow_updated=0; l->follow_sequence=0;
    l->trajectory_previous_dt=0;
    memset(l->trajectory_velocity,0,sizeof l->trajectory_velocity);
    snprintf(l->owner,sizeof l->owner,"%s",owner);
    memcpy(l->initial,d->feedback.arm[0].deg,sizeof l->initial);
    memcpy(l->command,l->initial,sizeof l->command);
}
static void cycle_leg(struct controller_lane *l,struct can_device *d,double now)
{
    leave_arrival_hold(l);
    for (unsigned i=0;i<6;++i) obstruction_reset(&l->obstruction[i],now);
    memcpy(l->leg_start,d->feedback.arm[0].deg,sizeof l->leg_start);
    memcpy(l->command,l->leg_start,sizeof l->command);
    l->leg_span=l->leg_fraction=0;
    for (unsigned i=0;i<6;++i) l->leg_span=fmax(l->leg_span,fabs(l->target[i]-l->leg_start[i]));
    l->settled_at=l->final_sent_at=l->sent_at=0;
    l->best_error=1e9; l->progress_at=l->last_tick=now;
}
static const char *start_query(struct controller *c,struct controller_lane *l,struct can_device *d,double now)
{
    if (!d||!d->present||!d->up||!can_bus_transport_open(d)) return "CAN interface is unavailable.";
    struct can_frame f[2]={piper_firmware_query(),piper_gripper_config_query()};
    can_bus_invalidate_configuration(d,now);
    if (!can_bus_send(&c->bus,d,d->generation,f,2)) return "Information requests could not be queued.";
    l->query=QUERY_INITIAL; l->query_generation=d->generation; l->query_cursor=0;
    l->query_start=now; l->query_next=now+.1; l->auto_query_at=now;
    message(l,"querying","Reading firmware, gripper configuration and joint limits. Waiting for robot replies.");
    return NULL;
}
/* Reopen only this lane's adapter, then refresh identity and limits with
   bounded read-only requests. Never resumes motion or changes power. */
static const char *reconnect_and_query(struct controller *c,struct controller_lane *l,struct can_device *d,double now)
{
    l->query=QUERY_IDLE; can_bus_reconnect_device(&c->bus,d);
    return start_query(c,l,can_bus_find(&c->bus,l->lane_device),now);
}
static bool follow_hold(struct controller *c,struct controller_lane *l,struct can_device *d,double now,const char *reason)
{
    const char *why=health(d,now,CHECK_CONTROL);
    if (why||!enabled(d,true,0)||d->generation!=l->generation) {
        char failure[512]; snprintf(failure,sizeof failure,"%s Measured-pose hold could not be verified: %s",reason,
            why?why:"Motor enable or adapter changed during following.");
        stop_lane(c,l,failure); return false;
    }
    if (!hold_measured_pose(l,d)||!send_pose(c,l,d,l->target)) {
        char failure[512];
        snprintf(failure,sizeof failure,"%s Measured-pose hold failed: %s",reason,*d->problem?d->problem:l->error);
        stop_lane(c,l,failure); return false;
    }
    finish(c,l,d,reason,true);
    return true;
}
/* This bounds the transmitted position-reference intervals, not the MCU's
   physical acceleration or contact forces. The normal STOP/deadline measured
   hold remains immediate and is deliberately outside this shaping contract. */
static const double trajectory_tracking_lead_deg=2; /* Soft shaping wall; the independent hard tracking guard remains3 degrees. */
/* Admission stores this per joint and every step recomputes it identically. */
static double trajectory_speed_limit(const struct piper_limit *l,double speed_percent) { return RAD2DEG(l->speed)*speed_percent/100; }
static double trajectory_braking_speed(double distance,double acceleration,double dt)
{
    return sqrt(2*acceleration*fmax(0,distance)+acceleration*acceleration*dt*dt)-acceleration*dt;
}
enum trajectory_step_result trajectory_reference_step(struct controller_lane *l,struct can_device *d,double now,double dt)
{
    double next[6],velocity[6],midpoint_dt=l->trajectory_previous_dt>0?(dt+l->trajectory_previous_dt)/2:dt;
    /* lane_tick returns below 20 ms and holds above 50 ms before calling, so
       this cannot fire today. It stays a runtime check, not an assert: in the
       motion loop the fail-safe answer is to stop following, never to abort
       the daemon while the arm may be moving. */
    if (!isfinite(dt)||dt<.02||dt>.050001||!isfinite(midpoint_dt)||midpoint_dt<=0) {
        snprintf(l->error,sizeof l->error,"Trajectory reference interval is invalid; following stopped."); return TRAJECTORY_STEP_INVALID;
    }
    for (unsigned i=0;i<6;++i) {
        const struct piper_limit *lim=&d->feedback.limit[i];
        struct piper_acceleration *a=&d->feedback.acceleration[i];
        if (!configuration_current(d,lim->seen,lim->at,now)||!configuration_current(d,a->seen,a->at,now)||
            !isfinite(lim->speed)||!isfinite(a->max)||lim->speed<=0||a->max<=0||
            lim->min!=l->trajectory_minimum[i]||lim->max!=l->trajectory_maximum[i]||
            trajectory_speed_limit(lim,l->speed_percent)!=l->trajectory_speed[i]||
            RAD2DEG(a->max)!=l->trajectory_acceleration[i]) {
            snprintf(l->error,sizeof l->error,"J%u trajectory limits changed or are unavailable; following cancelled.",i+1); return TRAJECTORY_STEP_INVALID;
        }
        double maximum_speed=l->trajectory_speed[i],acceleration=l->trajectory_acceleration[i];
        double remaining=l->target[i]-l->command[i];
        /* Conservative stopping envelope for discrete position intervals.
           Retargeting can require braking through a new target; never snap the
           reference to that target or to a measured-position lead boundary. */
        /* Rounding a braking interval toward a feasible millidegree can lose
           at most one velocity grid step. Reserve its worst-case acceleration
           at the minimum 20-ms interval rather than assuming ideal braking. */
        double braking_acceleration=acceleration-.001/(.02*.02);
        /* Admission requires finite limits with acceleration above the
           2.5 deg/s^2 grid reserve, health requires finite feedback, and every
           committed reference/velocity below is checked finite. */
        /* Runtime check rather than assert for the same fail-safe reason. */
        if (!isfinite(l->command[i])||!isfinite(l->trajectory_velocity[i])||!isfinite(d->feedback.arm[0].deg[i])||
            !isfinite(maximum_speed)||!isfinite(braking_acceleration)||braking_acceleration<=0) {
            snprintf(l->error,sizeof l->error,"J%u trajectory state is not finite; following stopped.",i+1); return TRAJECTORY_STEP_INVALID;
        }
        double braking=trajectory_braking_speed(fabs(remaining),braking_acceleration,dt);
        double wanted=copysign(fmin(maximum_speed,fmin(fabs(remaining)/dt,braking)),remaining);
        double lower=fmax(-maximum_speed,l->trajectory_velocity[i]-acceleration*midpoint_dt);
        double upper=fmin(maximum_speed,l->trajectory_velocity[i]+acceleration*midpoint_dt);
        /* Joint frames encode integer millidegrees. Select a feasible grid
           point, so encoding/rounding cannot silently violate these bounds. */
        double minimum=fmax(lim->min,d->feedback.arm[0].deg[i]-trajectory_tracking_lead_deg);
        double maximum=fmin(lim->max,d->feedback.arm[0].deg[i]+trajectory_tracking_lead_deg);
        /* A target may keep moving beyond the measured lead wall. Reserve
           stopping distance at both walls before acceleration makes the next
           interval infeasible, even if the encoder makes no further progress. */
        lower=fmax(lower,-trajectory_braking_speed(l->command[i]-minimum,braking_acceleration,dt));
        upper=fmin(upper,trajectory_braking_speed(maximum-l->command[i],braking_acceleration,dt));
        double low_grid=ceil(fmax(minimum,l->command[i]+lower*dt)*1000-1e-8);
        double high_grid=floor(fmin(maximum,l->command[i]+upper*dt)*1000+1e-8);
        if (low_grid>high_grid) {
            snprintf(l->error,sizeof l->error,"J%u trajectory boundary is infeasible (reference %.3f, velocity %.3f, measured %.3f, dt %.4f); queued targets retired.",
                i+1,l->command[i],l->trajectory_velocity[i],d->feedback.arm[0].deg[i],dt); return TRAJECTORY_STEP_BOUNDARY;
        }
        double grid=round(l->command[i]*1000)+trunc(wanted*dt*1000);
        next[i]=fmin(high_grid,fmax(low_grid,grid))/1000;
        velocity[i]=(next[i]-l->command[i])/dt;
        if (!isfinite(next[i])||!isfinite(velocity[i])||fabs(velocity[i])>maximum_speed+1e-7||
            fabs(velocity[i]-l->trajectory_velocity[i])>acceleration*midpoint_dt+1e-7) {
            snprintf(l->error,sizeof l->error,"J%u trajectory reference could not be bounded; following stopped.",i+1); return TRAJECTORY_STEP_INVALID;
        }
    }
    memcpy(l->command,next,sizeof next); memcpy(l->trajectory_velocity,velocity,sizeof velocity);
    l->trajectory_previous_dt=dt;
    return TRAJECTORY_STEP_OK;
}
/* The HTTP routes and /api/command actions, parsed once at the boundary. */
enum api_route { ROUTE_UNKNOWN, ROUTE_SELECT, ROUTE_STOP, ROUTE_RECONNECT, ROUTE_HEARTBEAT, ROUTE_QUERY, ROUTE_COMMAND };
enum control_action {
    ACTION_UNKNOWN, ACTION_HOLD, ACTION_FOLLOW_START, ACTION_FOLLOW_UPDATE, ACTION_FOLLOW_COMMIT, ACTION_FOLLOW_END,
    ACTION_TEACH_RETURN, ACTION_RECOVER_AT_POSE, ACTION_VERIFY_CURRENT,
    ACTION_DISABLE, ACTION_RESET, ACTION_VERIFY_RESET, ACTION_VERIFY_DISABLED,
    ACTION_ENABLE, ACTION_ALIGN, ACTION_CYCLE, ACTION_MOVE_TO_ZERO, ACTION_POSE, ACTION_JOINT, ACTION_JOG, ACTION_ZERO, ACTION_GRIPPER
};
static enum api_route parse_route(const char *route)
{
    static const struct { const char *path; enum api_route route; } routes[]={
        {"/api/select",ROUTE_SELECT},{"/api/stop",ROUTE_STOP},{"/api/reconnect",ROUTE_RECONNECT},
        {"/api/heartbeat",ROUTE_HEARTBEAT},{"/api/query",ROUTE_QUERY},{"/api/command",ROUTE_COMMAND}};
    for (size_t i=0;i<sizeof routes/sizeof *routes;++i) if (!strcmp(route,routes[i].path)) return routes[i].route;
    return ROUTE_UNKNOWN;
}
static enum control_action parse_action(const char *action)
{
    static const struct { const char *name; enum control_action action; } actions[]={
        {"hold",ACTION_HOLD},{"follow-start",ACTION_FOLLOW_START},{"follow-update",ACTION_FOLLOW_UPDATE},
        {"follow-commit",ACTION_FOLLOW_COMMIT},{"follow-end",ACTION_FOLLOW_END},{"teach-return",ACTION_TEACH_RETURN},
        {"recover-at-pose",ACTION_RECOVER_AT_POSE},{"verify-current",ACTION_VERIFY_CURRENT},{"disable",ACTION_DISABLE},
        {"reset",ACTION_RESET},{"verify-reset",ACTION_VERIFY_RESET},{"verify-disabled",ACTION_VERIFY_DISABLED},
        {"enable",ACTION_ENABLE},{"align",ACTION_ALIGN},{"cycle",ACTION_CYCLE},{"move-to-zero",ACTION_MOVE_TO_ZERO},
        {"pose",ACTION_POSE},{"joint",ACTION_JOINT},{"jog",ACTION_JOG},{"zero",ACTION_ZERO},{"gripper",ACTION_GRIPPER}};
    for (size_t i=0;i<sizeof actions/sizeof *actions;++i) if (!strcmp(action,actions[i].name)) return actions[i].action;
    return ACTION_UNKNOWN;
}
/* follow-update, follow-commit or follow-end for the lane's live session. */
static cJSON *follow_command(struct controller *c,struct controller_lane *l,struct can_device *d,const cJSON *body,enum control_action action,double now,int *status)
{
    double request,generation,sequence;
    if (!d||strcmp(l->owner,text_field(body,"owner"))||strcmp(d->id,text_field(body,"device_id"))||
        !numeric(body,"request_id",1,UINT32_MAX,&request)||request!=l->request_id||
        !numeric(body,"generation",1,UINT32_MAX,&generation)||generation!=l->generation||d->generation!=l->generation)
        return reply(status,409,"Following session no longer matches this controller.");
    if (l->follow==FOLLOW_LIVE&&now-l->follow_updated>=follow_input_timeout_s) {
        follow_hold(c,l,d,now,"Following input expired after 250 ms. Holding measured pose; no target replay.");
        return reply(status,409,l->message);
    }
    if (action==ACTION_FOLLOW_END) {
        bool held=follow_hold(c,l,d,now,"Movement stopped. Holding measured pose; motors remain enabled.");
        return reply(status,held?200:409,l->message);
    }
    if (l->follow!=FOLLOW_LIVE) return reply(status,409,"This drag was already released. Start a new gesture to change its target.");
    if (trajectory_following(l)&&action==ACTION_FOLLOW_COMMIT)
        return reply(status,409,"Trajectory following requires live samples; finish with follow-end to hold the measured pose.");
    if (!numeric(body,"sequence",1,UINT32_MAX,&sequence)||sequence!=floor(sequence)||sequence<=l->follow_sequence)
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
    if (!valid_target(l,d,target)) return reply(status,400,l->error);
    memcpy(l->target,target,sizeof target); l->follow_sequence=(unsigned)sequence;
    l->follow_updated=l->heartbeat=now;
    if (action==ACTION_FOLLOW_COMMIT) {
        l->follow=FOLLOW_COMMITTED;
        l->started=l->progress_at=now; l->best_error=1e9;
        l->settled_at=l->final_sent_at=0;
        message(l,"moving","Movement in progress to released target. Stop movement holds the measured pose.");
        return reply(status,202,l->message);
    }
    return reply(status,202,"Latest following target accepted.");
}
static cJSON *lane_heartbeat(struct controller *c,struct controller_lane *l,struct can_device *d,const cJSON *body,double now,int *status)
{
    double request;
    if (!d||d->generation!=l->generation) return reply(status,409,"The control lease belongs to another CAN connection generation.");
    if (l->op!=OP_IDLE&&now-l->heartbeat>=lease_timeout_s) {
        if (l->op==OP_TEACH_RETURN) abandon_teaching_return(l,"Control lease expired.");
        else stop_lane(c,l,"Control heartbeat expired.");
        return reply(status,409,l->message);
    }
    if (l->op==OP_IDLE||strcmp(l->owner,text_field(body,"owner"))||!numeric(body,"request_id",1,UINT32_MAX,&request)||request!=l->request_id)
        return reply(status,409,"No matching active control lease.");
    l->heartbeat=now; return reply(status,200,"Control lease renewed.");
}
/* The owner's hold on an active lane: ALIGN cancels with STOP, GRIPPER stops
   at its last target, MOVE and CYCLE hold the measured pose. */
static cJSON *lane_hold(struct controller *c,struct controller_lane *l,struct can_device *d,const cJSON *body,double now,int *status)
{
    switch (l->op) {
    case OP_ALIGN: {
        double request,generation;
        if (!d||strcmp(d->id,text_field(body,"device_id"))||
            !numeric(body,"generation",1,UINT32_MAX,&generation)||generation!=l->generation||d->generation!=l->generation||
            !numeric(body,"request_id",1,UINT32_MAX,&request)||request!=l->request_id)
            return reply(status,409,"Range-entry cancellation belongs to another controller action or CAN connection.");
        /* A full measured pose outside its limits cannot be encoded as a
           legal hold target. Cancel the pending destination with STOP. */
        stop_lane(c,l,"Operator cancelled range entry; the pending destination was discarded.");
        return reply(status,200,l->message);
    }
    case OP_GRIPPER: {
        const char *why=health(d,now,CHECK_CONTROL); if (why) return reply(status,409,why);
        if (d->generation!=l->generation||!enabled(d,true,0)||!d->feedback.gripper_enabled) return reply(status,409,"Connection or motor enable changed during gripper motion.");
        if (now-l->heartbeat>=lease_timeout_s||!d->feedback.gripper_seen||!piper_recent(d->feedback.gripper_at,now)||d->feedback.gripper_fault) return reply(status,409,"Fresh gripper feedback and an active lease are required to hold.");
        if (!l->sent_at) return reply(status,409,"No gripper target has been transmitted yet.");
        finish(c,l,d,"Gripper ramp stopped at its last transmitted target and effort limit. Contact or a secure grasp is not verified.",true);
        return reply(status,200,l->message);
    }
    case OP_MOVE: case OP_CYCLE: {
        double request;
        if (field(body,"request_id")&&(!numeric(body,"request_id",1,UINT32_MAX,&request)||request!=l->request_id))
            return reply(status,409,"Hold belongs to an older movement.");
        if (l->follow!=FOLLOW_NONE) {
            bool held=follow_hold(c,l,d,now,"Following paused. Holding measured pose; motors remain enabled.");
            return reply(status,held?200:409,l->message);
        }
        const char *why=health(d,now,CHECK_CONTROL); if (why) return reply(status,409,why);
        leave_arrival_hold(l);
        if (!hold_measured_pose(l,d)) {
            char failure[512]; snprintf(failure,sizeof failure,"Measured-pose hold failed: %s",l->error);
            stop_lane(c,l,failure); return reply(status,409,l->message);
        }
        bool was_cycle=l->op==OP_CYCLE; l->op=OP_MOVE; l->stage=STAGE_NONE;
        if (was_cycle) { l->started=l->progress_at=now; l->best_error=1e9; }
        l->settled_at=l->final_sent_at=0; l->heartbeat=now;
        message(l,"holding","%s",was_cycle?"Upright/return sequence cancelled. Holding here; no automatic return will run.":"Holding the current measured pose.");
        return reply(status,202,l->message);
    }
    default:
        return reply(status,409,"Another control action is active. Hold or stop it first.");
    }
}
/* Only the owner's hold and the live session's follow commands reach an
   active lane. Every other request is refused without a state change. */
static cJSON *active_lane_request(struct controller *c,struct controller_lane *l,struct can_device *d,enum control_action action,const cJSON *body,double now,int *status)
{
    switch (action) {
    case ACTION_FOLLOW_UPDATE: case ACTION_FOLLOW_COMMIT: case ACTION_FOLLOW_END:
        if (l->follow!=FOLLOW_NONE) return follow_command(c,l,d,body,action,now,status);
        break;
    case ACTION_HOLD:
        if (!strcmp(l->owner,text_field(body,"owner"))) return lane_hold(c,l,d,body,now,status);
        break;
    default:
        break;
    }
    return reply(status,409,"Another control action is active. Hold or stop it first.");
}
/* The 202 reply for an admitted power or motion operation. */
static cJSON *admitted(const struct controller_lane *l,int *status)
{
    cJSON *result=reply(status,202,l->message); num(result,"request_id",l->request_id);
    str(result,"trajectory_transport","position-velocity");
    flag(result,"trajectory_transport_validated",false);
    flag(result,"trajectory_follow",trajectory_following(l)); return result;
}
/* teach-return, or recover-at-pose when recovery is true. */
static cJSON *admit_teach_return(struct controller *c,struct controller_lane *l,struct can_device *d,const cJSON *body,const char *owner,bool recovery,double now,int *status)
{
    bool modern=piper_seamless_teach_supported(&d->feedback);
    if (recovery&&!modern) return reply(status,409,"This firmware cannot restore a measured-pose hold without a reset; use its separate recovery flow.");
    if (!yes(body,"button_off")||!yes(body,modern?"same_pose_confirmed":"zero_pose_confirmed")||!yes(body,"clear")||!yes(body,"supported"))
        return reply(status,400,modern?"Confirm teach light off, a clear workspace and the stationary measured hold pose; no homing or motor-power change will run.":
            "Confirm physical zero pose, teach-button light off, a clear workspace and support for the arm during the standby/CAN transition.");
    const struct piper_sample *s=&d->feedback.arm[0];
    if (!recovery&&s->control<=1&&!teaching_active(s)) {
        const char *why=health(d,now,CHECK_CONTROL); if (why) return reply(status,409,why);
        return reply(status,200,"The arm already reports standby/CAN control. No handoff command was sent.");
    }
    if (s->control!=2) return reply(status,409,"This action only returns a completed onboard teaching state to CAN control.");
    const char *why=teaching_return_guard(c,l,d,now,recovery); if (why) return reply(status,409,why);
    begin(c,l,d,owner,OP_TEACH_RETURN,now); l->stage=modern?STAGE_TEACH_SEEDED:STAGE_TEACH_STANDBY; l->sent_at=now;
    l->recovering_at_pose=recovery;
    if (recovery) forget_teaching_cycle(l); /* One attempt per observed cycle. */
    memcpy(l->target,l->initial,sizeof l->target); /* Retire every previous destination. */
    if (modern) {
        /* Seed the frozen full pose while still in completed teaching.
           Return to the receive loop before the bounded mode+pose batch;
           seven synchronous USB writes must not starve receive draining. */
        struct can_frame frames[3];
        if (!piper_targets(l->initial,frames)) {
            abandon_teaching_return(l,"Measured hold could not be encoded."); return reply(status,409,l->message);
        }
        if (!can_bus_send(&c->bus,d,l->generation,frames,3)) {
            stop_lane(c,l,"Same-pose teaching handoff seed could not finish transmitting; previous targets discarded."); return reply(status,409,l->message);
        }
        l->sent_at=piper_now();
        message(l,"teaching-return","Measured hold pose seeded. Checking fresh feedback before requesting CAN MoveJ; no motor-power change or homing.");
        cJSON *result=reply(status,202,l->message); num(result,"request_id",l->request_id); return result;
    }
    struct can_frame frame=piper_mode(1); frame.data[0]=0; /* Documented standby, then CAN; never ctrl_mode=2. */
    if (!can_bus_send(&c->bus,d,l->generation,&frame,1)) {
        abandon_teaching_return(l,"Standby request could not be transmitted."); return reply(status,409,l->message);
    }
    message(l,"teaching-return","Standby requested at verified zero. Waiting for fresh acknowledgement before requesting CAN; keep the arm supported.");
    cJSON *result=reply(status,202,l->message); num(result,"request_id",l->request_id); return result;
}
static cJSON *verify_current(struct controller *c,struct controller_lane *l,struct can_device *d,const cJSON *body,double now,int *status)
{
    if (!yes(body,"clear") || !yes(body,"supported")) return reply(status,400,"Confirm that it is safe to use the current robot state.");
    const char *why=health(d,now,CHECK_CONTROL); if (why) return reply(status,409,why);
    if (reset_pending(c,d->id)) return reply(status,409,"A reset is pending; verify completed reset recovery first.");
    if ((!enabled(d,true,0)&&!enabled(d,false,0)) || can_bus_strict_still_for(d,now)<stationary_dwell_s)
        return reply(status,409,"Wait for healthy, stationary feedback and a consistent motor power state.");
    if (!clear_latch(c,d->id)) return reply(status,500,"Recovery state could not be saved.");
    message(l,"monitor","Current robot state verified. No motor power or position command was sent.");
    return reply(status,200,l->message);
}
/* disable, reset, verify-reset or verify-disabled. */
static cJSON *admit_power(struct controller *c,struct controller_lane *l,struct can_device *d,const cJSON *body,const char *owner,enum control_action action,double now,int *status)
{
    if (!yes(body,"supported")) return reply(status,400,"Physically support the arm before removing motor power or recovering it.");
    const char *why=health(d,now,CHECK_POWER); if (why) return reply(status,409,why);
    switch (action) {
    case ACTION_VERIFY_RESET: case ACTION_VERIFY_DISABLED: {
        bool reset_verification=action==ACTION_VERIFY_RESET;
        if (reset_verification&&!yes(body,"reset_completed")) return reply(status,409,"Confirm that the reset or power cycle has completed.");
        if (!reset_verification&&reset_pending(c,d->id)) return reply(status,409,"A controller reset is pending. Use Verify completed recovery after it finishes.");
        if (!enabled(d,false,0)||health(d,now,CHECK_CONTROL)||can_bus_strict_still_for(d,now)<stationary_dwell_s)
            return reply(status,409,"All six motors must report disabled, healthy and stationary before recovery can be cleared.");
        if (!clear_latch(c,d->id)) return reply(status,500,"Recovery state could not be saved.");
        message(l,"monitor","Recovery cleared: all six motors verified off, healthy and stationary. Enable motors when ready; no motor command was sent.");
        return reply(status,200,l->message);
    }
    case ACTION_RESET: {
        struct piper_sample *s=&d->feedback.arm[0];
        if (!yes(body,"clear")) return reply(status,400,"Confirm the workspace is clear before requesting reset recovery.");
        bool stopped_off=s->arm_status==PIPER_ARM_STATUS_STOPPED&&!s->error&&enabled(d,false,0);
        /* An in-flight reset was rejected above with every other active
           action. A retired/timed-out reset can be requested explicitly
           again only at a fresh, stationary, error-free STOP with every
           motor off. Never retry on refresh, while running, or merely
           because an acknowledgement has not arrived yet. */
        if (reset_pending(c,d->id)&&!stopped_off)
            return reply(status,409,"A reset is pending. Verify completed recovery, or request reset again only after a fresh stationary stop with all six motors off.");
        bool residual_teach=stopped_off&&s->teach==PIPER_TEACH_RECORDING;
        if (s->control>2||s->motion_mode>1||(!residual_teach&&!piper_teach_idle(s->teach))) return reply(status,409,"Reset is supported only for stopped/idle MoveJ or completed teaching states.");
        if (can_bus_strict_still_for(d,now)<stationary_dwell_s) return reply(status,409,"Wait for a stationary arm before reset recovery.");
        break;
    }
    default:
        break;
    }
    bool reset=action==ACTION_RESET;
    l->disable_keeps_recovery=blocked(c,d->id)||health(d,now,CHECK_CONTROL)!=NULL;
    const char *kind=reset?KIND_RESET:KIND_POWER;
    const char *intent=reset?"Reset pending verification":active_incident(c,d->id,"Motors disabled; recovery verification required",&kind);
    if (!latch(c,d->id,intent,kind,reset||reset_pending(c,d->id))) return reply(status,500,"Could not persist the recovery record; no command sent.");
    begin(c,l,d,owner,reset?OP_RESET:OP_DISABLE,now); l->sent_at=now;
    struct can_frame f=piper_reset_frame();
    bool sent=!reset||can_bus_send(&c->bus,d,l->generation,&f,1);
    if (!sent) { lane_go_idle(l); message(l,"blocked","Power/recovery request was not fully transmitted. Recovery remains required."); return reply(status,409,l->message); }
    message(l,reset?"resetting":"disabling","Request queued; checking fresh disabled-motor feedback.");
    return admitted(l,status);
}
/* enable, gripper and every position action (align, cycle, follow-start,
   move-to-zero, pose, joint, jog, zero). An unknown action is rejected after
   the shared motion guards, so its reply does not change. */
static cJSON *admit_motion(struct controller *c,struct controller_lane *l,struct can_device *d,const cJSON *body,const char *owner,enum control_action action,double now,int *status)
{
    const char *why=controller_blocker(c,d,now); if (why) return reply(status,409,why);
    if (field(body,"trajectory")&&(!cJSON_IsBool(field(body,"trajectory"))||action!=ACTION_FOLLOW_START))
        return reply(status,400,"trajectory is a boolean available only on follow-start.");
    bool trajectory=yes(body,"trajectory");
    if (!yes(body,"clear")) return reply(status,400,"Confirm that the full motion path is clear and the arm is attended.");
    /* The MCU's target-arrival bit can remain unset after a disabled arm
       settles away from its last target. Require measured stillness. */
    if (action==ACTION_ENABLE) {
        if (can_bus_strict_still_for(d,now)<pose_dwell_s) return reply(status,409,"Wait for a stationary arm before changing motor power.");
    } else if (!position_control_stationary(d,now)) return reply(status,409,"Wait for a stable measured pose before beginning another action.");
    measured_hold_target(d,l->target);
    enum operation op=OP_MOVE;
    bool staged_range=false, zero_destination=action==ACTION_MOVE_TO_ZERO;
    int selected_joint=-1;
    double requested_target[6], requested_speed=0, requested_percent=0;
    l->speed=3; l->tolerance=.2;
    double ceiling=1e9;
    for (unsigned i=0;i<6;++i) ceiling=fmin(ceiling,RAD2DEG(d->feedback.limit[i].speed));
    bool percentage=field(body,"speed_percent")!=NULL;
    if (percentage) {
        if (field(body,"speed_deg_s")) return reply(status,400,"Specify speed_percent or speed_deg_s, not both.");
        if (!numeric(body,"speed_percent",0,100,&l->speed_percent)) return reply(status,400,"speed_percent must be 0–100.");
        l->speed=ceiling*l->speed_percent/100;
    } else if (field(body,"speed_deg_s")&&!numeric(body,"speed_deg_s",.1,15,&l->speed)) return reply(status,400,"Legacy speed_deg_s must be 0.1–15; use speed_percent for reported limits.");
    if (field(body,"tolerance_deg")&&!numeric(body,"tolerance_deg",.05,1,&l->tolerance)) return reply(status,400,"tolerance_deg must be 0.05–1.0.");
    switch (action) {
    case ACTION_ENABLE:
        if (!yes(body,"supported")) return reply(status,400,"Support the arm before changing motor power.");
        if (!enabled(d,false,0)) return reply(status,409,"All six motors must be disabled before this enable operation.");
        op=OP_ENABLE;
        break;
    case ACTION_ALIGN: {
        double distance=range_distance(d);
        if (!isfinite(distance)||distance<=0) return reply(status,409,"All joints are already within the reported command ranges.");
        if (distance>3) return reply(status,409,"A joint is more than 3 degrees outside its command range. Inspect the pose and calibration before motion.");
        for (unsigned i=0;i<6;++i) l->target[i]=range_target(d,i);
        op=OP_ALIGN; l->speed=1;
        break;
    }
    case ACTION_CYCLE:
        /* Existing H-V1.2-1 near-upright preset from the vendor DH model.
           Preserve base heading and tool roll; capture the return pose now. */
        l->target[1]=82; l->target[2]=-160; l->target[3]=0; l->target[4]=-7;
        op=OP_CYCLE; l->speed=3; l->tolerance=.2;
        break;
    case ACTION_FOLLOW_START:
        /* Begin at fresh measurements. No queued target is replayed on entry. */
        break;
    case ACTION_MOVE_TO_ZERO:
        memset(l->target,0,sizeof l->target);
        break;
    case ACTION_POSE: {
        const cJSON *angles=field(body,"degrees");
        if (!cJSON_IsArray(angles)||cJSON_GetArraySize(angles)!=6) return reply(status,400,"degrees must contain six finite angles.");
        zero_destination=true;
        for (unsigned i=0;i<6;++i) { cJSON *n=cJSON_GetArrayItem(angles,(int)i); if (!cJSON_IsNumber(n)||!isfinite(n->valuedouble)) return reply(status,400,"Every angle must be finite."); l->target[i]=n->valuedouble; if (l->target[i]!=0) zero_destination=false; }
        break;
    }
    case ACTION_JOINT: case ACTION_JOG: case ACTION_ZERO: {
        double joint;
        if (!numeric(body,"joint",1,6,&joint)||floor(joint)!=joint) return reply(status,400,"joint must be 1–6.");
        unsigned index=(unsigned)joint-1;
        selected_joint=(int)index;
        double degrees=0;
        bool jog=action==ACTION_JOG;
        if (action!=ACTION_ZERO&&!numeric(body,"degrees",jog?-5:-360,jog?5:360,&degrees)) return reply(status,400,"Enter a finite angle (jog increments are limited to ±5°).");
        /* An inward jog starts from the nearest legal boundary, as it
           already does for sub-tolerance encoder drift. It cannot request
           another invalid negative/outward pose. */
        if (jog) l->target[index]=clamp_limit(&d->feedback.limit[index],l->target[index]);
        l->target[index]=jog?l->target[index]+degrees:degrees;
        break;
    }
    case ACTION_GRIPPER: {
        if (!d->feedback.gripper_seen||!piper_recent(d->feedback.gripper_at,now)||d->feedback.gripper_fault) return reply(status,409,"Fresh, fault-free gripper feedback is required.");
        struct piper_feedback *f=&d->feedback;
        if (!configuration_current(d,f->gripper_config_seen,f->gripper_config_at,now)||(f->gripper_max_mm!=70&&f->gripper_max_mm!=100)) return reply(status,409,"Read gripper configuration: a verified 70 or 100 mm range is required for this connection.");
        if (!numeric(body,"width_mm",0,f->gripper_max_mm,&l->gripper_target)) return reply(status,400,"width_mm must be between zero and the controller-reported maximum opening.");
        l->gripper_effort=.1;
        if (field(body,"effort_nm")&&!numeric(body,"effort_nm",.02,.5,&l->gripper_effort)) return reply(status,400,"effort_nm must be 0.02–0.5 N·m.");
        l->gripper_entry=f->gripper_mm<0;
        if (!isfinite(f->gripper_mm)||f->gripper_mm < -2||f->gripper_mm>f->gripper_max_mm) return reply(status,409,"Gripper feedback is outside the supported range; inspect calibration.");
        if (l->gripper_entry&&(!yes(body,"allow_closed_offset")||l->gripper_target<5)) return reply(status,409,"A small negative closed reading requires allow_closed_offset=true and an opening target of at least 5 mm. This does not change encoder zero.");
        l->gripper_max_mm=f->gripper_max_mm;
        l->gripper_command=d->feedback.gripper_mm; op=OP_GRIPPER;
        break;
    }
    default:
        return reply(status,400,"Unknown control action.");
    }
    if (op!=OP_ENABLE&&!enabled(d,true,0)) return reply(status,409,"Enable all six arm motors first so the unselected joints remain supported.");
    double correction=range_distance(d);
    if ((op==OP_MOVE||op==OP_CYCLE)&&correction>0) {
        bool inward_joint=selected_joint>=0&&outside_range_count(d)==1&&joint_outside_range(d,(unsigned)selected_joint);
        if (op!=OP_MOVE||(!zero_destination&&!inward_joint)) { char reason[256]; return reply(status,409,range_reason(d,reason,sizeof reason)); }
        if (correction>3) return reply(status,409,"Range entry requires more than 3 degrees of correction. Inspect the physical pose and calibration before moving.");
        staged_range=true;
    }
    if (position_op(op)&&!valid_target(l,d,l->target)) return reply(status,400,l->error);
    if (percentage&&l->speed_percent==0&&(op==OP_MOVE||op==OP_CYCLE)) return reply(status,409,"Speed is 0%; choose a nonzero speed before moving. This setting applies to the next move, not an active stop.");
    /* Alignment and the optional scripted cycle retain their explicit rates. */
    if (!percentage||op==OP_ALIGN||op==OP_CYCLE) l->speed_percent=fmin(100,100*l->speed/ceiling);
    if (trajectory) {
        for (unsigned i=0;i<6;++i) {
            if (d->feedback.arm[0].deg[i]<d->feedback.limit[i].min||d->feedback.arm[0].deg[i]>d->feedback.limit[i].max)
                return reply(status,409,"Trajectory following starts only from an in-range measured pose; range recovery is a separate action.");
            double speed=trajectory_speed_limit(&d->feedback.limit[i],l->speed_percent);
            double acceleration=RAD2DEG(d->feedback.acceleration[i].max);
            if (speed*.02<.001-1e-10||acceleration*.02*.02<=.001+1e-10)
                return reply(status,409,"Reported limits or selected speed are below the 1-millidegree/20-ms trajectory reference resolution; no motion started.");
        }
    }
    if (op==OP_CYCLE&&!valid_target(l,d,d->feedback.arm[0].deg)) return reply(status,400,l->error);
    for (unsigned i=0;i<6;++i) if (l->speed>RAD2DEG(d->feedback.limit[i].speed)) return reply(status,400,"Requested speed exceeds a controller-reported joint speed limit.");
    if (staged_range) {
        memcpy(requested_target,l->target,sizeof requested_target);
        requested_speed=l->speed; requested_percent=l->speed_percent;
        for (unsigned i=0;i<6;++i) l->target[i]=joint_outside_range(d,i)?range_target(d,i):
            clamp_limit(&d->feedback.limit[i],d->feedback.arm[0].deg[i]);
        if (!valid_target(l,d,l->target)) return reply(status,400,l->error);
        op=OP_ALIGN; l->speed=ceiling/100; l->speed_percent=1;
    }
    if (!latch(c,d->id,intent_reason,KIND_INTENT,false)) return reply(status,500,"Could not persist action intent; no command sent.");
    begin(c,l,d,owner,op,now);
    bool following=action==ACTION_FOLLOW_START;
    if (position_op(op)&&!following) l->pose_phase=POSE_SEEDING;
    if (staged_range) {
        l->stage=STAGE_ALIGN_FOLLOWUP; memcpy(l->range_followup_target,requested_target,sizeof requested_target);
        l->range_followup_speed=requested_speed; l->range_followup_percent=requested_percent;
    }
    if (following) { l->follow=FOLLOW_LIVE; l->follow_updated=now; }
    if (trajectory) {
        l->stage=STAGE_MOVE_TRAJECTORY;
        for (unsigned i=0;i<6;++i) {
            l->trajectory_minimum[i]=d->feedback.limit[i].min; l->trajectory_maximum[i]=d->feedback.limit[i].max;
            l->trajectory_speed[i]=trajectory_speed_limit(&d->feedback.limit[i],l->speed_percent);
            l->trajectory_acceleration[i]=RAD2DEG(d->feedback.acceleration[i].max);
        }
    }
    if (op==OP_CYCLE) { l->stage=STAGE_CYCLE_RAISING; cycle_leg(l,d,now); }
    if (op==OP_MOVE&&(zero_destination||(action==ACTION_POSE&&yes(body,"synchronized")))) { l->stage=STAGE_MOVE_SYNCHRONIZED; cycle_leg(l,d,now); }
    message(l,op==OP_ENABLE?"enabling":op==OP_GRIPPER?"gripper":op==OP_ALIGN?"aligning":op==OP_CYCLE?"raising":"moving","%s",op==OP_ENABLE?"Enabling motor drivers only; no position target is sent.":op==OP_ALIGN?"Moving 0.5 degrees inside the command range at 1% controller speed (maximum correction 3 degrees).":op==OP_CYCLE?"Raising to upright at up to 3 degrees/second. The measured starting pose is saved for return.":"Executing target with a live control lease and feedback checks.");
    if (staged_range) message(l,"aligning","Explicit destination accepted. First correcting bounded range overrun at 1%% controller speed; then moving to the requested target under the same live lease.");
    return admitted(l,status);
}
/* One request for one lane. action is ACTION_UNKNOWN except on ROUTE_COMMAND. */
static cJSON *lane_action(struct controller *c,struct controller_lane *l,enum api_route route,enum control_action action,const cJSON *body,int *status)
{
    double now=piper_now(); struct can_device *d=can_bus_find(&c->bus,l->lane_device);
    const char *owner=text_field(body,"owner");
    if (route==ROUTE_COMMAND&&field(body,"trajectory_transport")&&
        (!cJSON_IsString(field(body,"trajectory_transport"))||action!=ACTION_FOLLOW_START||!yes(body,"trajectory")||
         strcmp(text_field(body,"trajectory_transport"),"position-velocity")))
        return reply(status,400,"trajectory_transport must be position-velocity, only on trajectory follow-start.");
    observe_teaching_cycle(l,d,now);
    relinquish_to_teaching(c,l,d,now);
    if (route==ROUTE_HEARTBEAT) return lane_heartbeat(c,l,d,body,now,status);
    if (l->op!=OP_IDLE) return active_lane_request(c,l,d,action,body,now,status);
    if (route==ROUTE_RECONNECT) {
        const char *why=reconnect_and_query(c,l,d,now);
        if (why) message(l,"monitor","CAN sockets reopened. %s Read firmware & limits once connected.",why);
        return reply(status,200,l->message);
    }
    if (!d||strcmp(text_field(body,"device_id"),d->id)) return reply(status,409,"Selected adapter changed; refresh before commanding.");
    double generation;
    if (!numeric(body,"generation",1,UINT32_MAX,&generation)||generation!=d->generation) return reply(status,409,"CAN connection changed; refresh before commanding.");
    switch (route) {
    case ROUTE_QUERY: {
        if (l->query) return reply(status,409,"An information query is already active.");
        const char *why=start_query(c,l,d,now);
        return reply(status,why?409:202,why?why:l->message);
    }
    case ROUTE_COMMAND: break;
    default: return reply(status,404,"Unknown API route.");
    }
    if (l->query) return reply(status,409,"Wait for the information query to complete.");
    if (strlen(owner)<16||strlen(owner)>80) return reply(status,400,"owner must be a 16–80 character client identifier.");
    if (!yes(body,"standalone")) return reply(status,400,"Confirm that this arm has no linked leader or competing controller.");
    if (c->storage_failed) return reply(status,409,"Persistent control-state storage failed; repair storage and restart.");
    switch (action) {
    case ACTION_TEACH_RETURN: case ACTION_RECOVER_AT_POSE:
        return admit_teach_return(c,l,d,body,owner,action==ACTION_RECOVER_AT_POSE,now,status);
    case ACTION_VERIFY_CURRENT:
        return verify_current(c,l,d,body,now,status);
    case ACTION_DISABLE: case ACTION_RESET: case ACTION_VERIFY_RESET: case ACTION_VERIFY_DISABLED:
        return admit_power(c,l,d,body,owner,action,now,status);
    default:
        return admit_motion(c,l,d,body,owner,action,now,status);
    }
}
static cJSON *select_adapter(struct controller *c,const char *requested,int *status)
{
    struct can_device *device=can_bus_find(&c->bus,requested);
    if (!device) return reply(status,404,"Adapter not found.");
    str(c->saved,"selected",device->id);
    str(record(c,device->id),"name",*device->label?device->label:device->name);
    if (!persist(c)) return reply(status,500,"Adapter selection could not be saved; control is blocked.");
    snprintf(c->selected,sizeof c->selected,"%s",device->id);
    discover_lanes(c);
    return reply(status,200,"Display selection changed. All device-qualified control lanes are unchanged.");
}
/* Why a STOP that names a known arm does not match what that arm is doing,
   or NULL. The arm is stopped either way; this is only reported. */
static const char *stop_scope_mismatch(const struct controller_lane *l,const struct can_device *d,const cJSON *body)
{
    double value;
    if (field(body,"generation")&&(!numeric(body,"generation",1,UINT32_MAX,&value)||value!=d->generation))
        return "its generation is not this adapter's current CAN connection";
    if (field(body,"request_id")&&(l->op==OP_IDLE||strcmp(l->owner,text_field(body,"owner"))||
        !numeric(body,"request_id",1,UINT32_MAX,&value)||value!=l->request_id))
        return "its request_id and owner do not match an active action on this arm";
    return NULL;
}
/* /api/stop. A STOP is never refused without stopping:
     device_id names a known arm    that arm stops, even when the request's
                                    generation, request_id or owner is stale
     no device_id (or "")           every arm stops: the operator's global STOP
     anything else                  the request cannot be tied to one arm
                                    (device_id not a string or not known, or
                                    scoping fields without a device), so every
                                    arm stops
   Every arm's STOP frame is sent before the incident records are saved, and
   they are saved before the reply. The reply lists what was stopped. It is
   200 only when the scope was honoured as asked, every frame was delivered
   and the records were saved; otherwise 409 with the reason. */
static cJSON *stop_request(struct controller *c,const cJSON *body,int *status)
{
    const cJSON *id=field(body,"device_id");
    const char *requested=text_field(body,"device_id");
    struct can_device *device=*requested?can_bus_find(&c->bus,requested):NULL;
    struct controller_lane *only=device?lane_for(c,device->id):NULL;
    const char *degraded=NULL;
    if (id&&!cJSON_IsString(id)) degraded="its device_id is not an adapter identity";
    else if (*requested&&!device) degraded="its device_id is not a known CAN adapter";
    else if (device&&!only) degraded="no control lane is available for its adapter";
    else if (only) degraded=stop_scope_mismatch(only,device,body);
    else if (field(body,"generation")||field(body,"request_id")) degraded="a scoped cleanup STOP needs its device_id";
    cJSON *stops=cJSON_CreateArray();
    bool changed=false,undelivered=false;
    if (only) {
        double now=piper_now();
        observe_teaching_cycle(only,device,now); relinquish_to_teaching(c,only,device,now);
    }
    for (unsigned i=0;i<CANBUS_MAX;++i) if (*c->bus.devices[i].id&&(!only||&c->bus.devices[i]==device)) {
        struct can_device *d=&c->bus.devices[i];
        /* discover_lanes makes one lane per device; if that ever fails,
           report this arm as unverified and keep stopping the others. */
        struct controller_lane *l=only?only:lane_for(c,d->id);
        if (l) changed|=lane_stop_deliver(c,l,KIND_STOP,only?"Operator stop.":"Operator global stop.");
        cJSON *entry=cJSON_CreateObject(); str(entry,"device_id",d->id); num(entry,"generation",d->generation);
        bool delivered=l&&l->stop_delivered; flag(entry,"ok",delivered);
        str(entry,"message",l?l->message:"No control lane for this arm; STOP was not sent.");
        cJSON_AddItemToArray(stops,entry); undelivered|=!delivered;
    }
    bool saved=!changed||persist(c);
    bool broken=only&&!lane_check(c,only);
    char text[640];
    const char *reached=only?"this arm":"every known arm";
    if (degraded)
        snprintf(text,sizeof text,"This STOP could not be scoped as asked: %s. STOP was sent to %s instead%s%s",degraded,reached,
            undelivered?", and one or more deliveries are unverified; inspect each arm's result":"",
            saved?".":". The incident record could not be saved.");
    else if (undelivered)
        snprintf(text,sizeof text,"%s",only?only->message:"One or more STOP deliveries are unverified; inspect each arm's result.");
    else if (!saved) snprintf(text,sizeof text,"STOP was sent to %s, but the incident record could not be saved.",reached);
    else snprintf(text,sizeof text,"%s",only?only->message:"STOP requested independently on every known arm. Previous targets were discarded.");
    cJSON *result=reply(status,broken?500:degraded||undelivered||!saved?409:200,broken?only->message:text);
    str(result,"stop_scope",only?"device":"all"); flag(result,"stop_scope_degraded",degraded!=NULL);
    if (degraded) str(result,"stop_scope_reason",degraded);
    put(result,"stops",stops);
    return result;
}
static cJSON *global_reconnect(struct controller *c,int *status)
{
    for (unsigned i=0;i<CANBUS_MAX;++i) c->lanes[i].query=QUERY_IDLE;
    can_bus_reconnect(&c->bus);discover_lanes(c);
    struct controller_lane scratch={0},*l=lane_for(c,c->selected);
    if (!l) l=&scratch; /* No selected arm: report on a detached lane. */
    const char *why=start_query(c,l,can_bus_find(&c->bus,c->selected),piper_now());
    if (why) message(l,"monitor","CAN sockets reopened. %s Read firmware & limits once connected.",why);
    return reply(status,200,l->message);
}
cJSON *controller_action(struct controller *c,const char *path,const cJSON *body,int *status)
{
    discover_lanes(c);
    enum api_route route=parse_route(path);
    const char *requested=text_field(body,"device_id");
    /* STOP resolves its own scope: it is never refused for a malformed one. */
    if (route==ROUTE_STOP) return stop_request(c,body,status);
    if (field(body,"device_id")&&!cJSON_IsString(field(body,"device_id"))) return reply(status,400,"device_id must be an explicit adapter identity.");
    /* Requests that act on the display or, without a device id, on every arm. */
    switch (route) {
    case ROUTE_SELECT: return select_adapter(c,requested,status);
    case ROUTE_QUERY: case ROUTE_RECONNECT:
        if (controller_any_motion_active(c)) return reply(status,409,"Finish all arm movements before opening or querying a CAN transport.");
        if (route==ROUTE_RECONNECT&&!*requested) return global_reconnect(c,status);
        break;
    default: break;
    }
    const char *id=*requested?requested:c->selected;
    struct can_device *device=can_bus_find(&c->bus,id);
    if (!device) return reply(status,409,"The requested CAN adapter is not known; no other arm was selected.");
    bool pinned_generation=route==ROUTE_COMMAND||route==ROUTE_QUERY||(route==ROUTE_HEARTBEAT&&*requested)||
        (route==ROUTE_RECONNECT&&field(body,"generation"));
    if (pinned_generation) {
        double generation;
        if (!numeric(body,"generation",1,UINT32_MAX,&generation)||generation!=device->generation)
            return reply(status,409,"The request belongs to a different CAN connection generation.");
    }
    struct controller_lane *l=lane_for(c,id);
    if (!l) return reply(status,409,"No control lane is available for this adapter.");
    enum control_action action=route==ROUTE_COMMAND?parse_action(text_field(body,"action")):ACTION_UNKNOWN;
    if (route==ROUTE_COMMAND) {
        bool disabling=action==ACTION_DISABLE;
        if (disabling) l->query=QUERY_IDLE;
        if (!disabling&&l->op==OP_IDLE&&controller_any_query_active(c))
            return reply(status,409,"Wait for all read-only configuration queries to finish before starting an arm action.");
        if (l->op==OP_IDLE&&c->next_request_id==UINT32_MAX&&action!=ACTION_HOLD)
            return reply(status,409,"The request identity counter is exhausted. Restart the idle backend before starting another action.");
    }
    cJSON *qualified=NULL;
    if (!*requested) { qualified=cJSON_Duplicate(body,true);str(qualified,"device_id",id); }
    cJSON *result=lane_action(c,l,route,action,qualified?qualified:body,status);
    cJSON_Delete(qualified);
    /* A broken invariant stops the lane; the reply must not report success. */
    if (!lane_check(c,l)) { cJSON_Delete(result); result=reply(status,500,l->message); }
    return result;
}

/* Which read-only request `request` (0..13) is, and whether its reply is
   still missing for the current query. */
static bool query_reply_missing(const struct controller_lane *l,const struct can_device *d,unsigned request,struct can_frame *f)
{
    if (request<12) {
        unsigned joint=request%6; bool acceleration=request>=6;
        *f=piper_limit_query(joint+1,acceleration);
        return acceleration?!d->feedback.acceleration[joint].seen||d->feedback.acceleration[joint].at<l->query_start:
            !d->feedback.limit[joint].seen||d->feedback.limit[joint].at<l->query_start;
    }
    if (request==12) { *f=piper_firmware_query(); return !*d->feedback.version; }
    *f=piper_gripper_config_query();
    return !d->feedback.gripper_config_seen||d->feedback.gripper_config_at<l->query_start;
}
/* One step of the read-only information query (C-E), at most one frame. */
static void query_step(struct controller *c,struct controller_lane *l,struct can_device *d,double now)
{
    if (!d||d->generation!=l->query_generation||!d->present) {
        l->query=QUERY_IDLE; message(l,"blocked","Connection changed during information query."); return;
    }
    if (now-l->query_start>5) {
        l->query=QUERY_IDLE;
        l->auto_query_at=now; /* Retry only missing arm information in five seconds. */
        if (blocked(c,d->id)) message(l,"blocked","Information query incomplete. Recovery remains required: %s",text_field(record(c,d->id),"reason"));
        else message(l,"monitor","Information query ended. Missing replies remain unknown; inspect diagnostics.");
        return;
    }
    if (now<l->query_next||d->last_rx<l->query_start) return;
    l->query_next=now+.08;
    if (l->query==QUERY_INITIAL) {
        unsigned request=l->query_cursor++;
        if (l->query_cursor==12) { l->query=QUERY_RETRY; l->query_cursor=0; }
        struct can_frame f=piper_limit_query(request%6+1,request>=6);
        if (!can_bus_send(&c->bus,d,d->generation,&f,1)) { l->query=QUERY_IDLE; message(l,"blocked","Limit query could not be transmitted."); }
        return;
    }
    if (arm_configuration_received(d,now)) {
        l->query=QUERY_IDLE;
        if (blocked(c,d->id)) message(l,"blocked","Readings refreshed. Recovery remains required: %s",text_field(record(c,d->id),"reason"));
        else message(l,"monitor","Arm firmware and all six angle, speed and acceleration limits received.%s",d->feedback.gripper_config_seen?"":" Standard CAN gripper configuration unavailable; Pika is independent.");
        return;
    }
    /* Retry missing read-only replies within the existing five-second
       deadline. Never retry motor commands. */
    for (unsigned attempt=0;attempt<14;++attempt) {
        struct can_frame f;
        if (!query_reply_missing(l,d,l->query_cursor++%14,&f)) continue;
        if (!can_bus_send(&c->bus,d,d->generation,&f,1)) { l->query=QUERY_IDLE; message(l,"blocked","Information retry could not be transmitted."); }
        return;
    }
}
/* One explicit motor per controller tick for ENABLE (on) or DISABLE (off):
   the first pass never starves later drivers behind one delayed
   acknowledgement, retries are paced at 150 ms, and an enable baseline is
   frozen at the first request. A failed transmit STOPs the lane. */
static void power_round_robin(struct controller *c,struct controller_lane *l,struct can_device *d,bool on,double now)
{
    for (unsigned offset=0;offset<6;++offset) {
        unsigned i=(l->power_cursor+offset)%6;
        if (l->power_acknowledged[i]||(l->power_requested[i]&&now-l->power_last_sent_at[i]<.15)) continue;
        bool first=!l->power_requested[i];
        if (first&&on) l->enable_baseline[i]=d->feedback.arm[0].deg[i];
        struct can_frame frame=piper_power(i+1,on);
        if (!can_bus_send(&c->bus,d,l->generation,&frame,1)) {
            stop_lane(c,l,on?"Enable command was not fully transmitted.":
                "Disable command was not fully transmitted; fresh all-motors-off acknowledgement is required.");
            return;
        }
        /* Samples pumped before the write completed cannot acknowledge it. */
        double completed=piper_now();
        l->power_requested[i]=true;
        if (first) l->power_first_sent_at[i]=completed;
        if (on) ++l->enable_count;
        l->sent_at=l->power_last_sent_at[i]=completed; l->power_cursor=(i+1)%6;
        return;
    }
}
static void lane_tick(struct controller *c,struct controller_lane *l,double now)
{
    struct can_device *d=can_bus_find(&c->bus,l->lane_device);
    observe_teaching_cycle(l,d,now);
    relinquish_to_teaching(c,l,d,now);
    /* Recover transport and refresh information without a browser. Never
       replay control commands, clear recovery, or change motor power here. */
    if (!c->bus.simulated && l->op==OP_IDLE && !l->query && !c->defer_transport_maintenance) {
        if ((!d || !d->present || !piper_recent(d->last_rx,now) || *d->problem) && now-l->auto_reconnect_at>8) {
            l->auto_reconnect_at=l->auto_query_at=now;
            (void)reconnect_and_query(c,l,d,now);
            d=can_bus_find(&c->bus,l->lane_device);
        } else if (d && d->present && piper_recent(d->last_rx,now) &&
                   !arm_configuration_received(d,now) && now-l->auto_query_at>5) {
            l->auto_query_at=now; start_query(c,l,d,now);
        }
    }
    if (l->query&&!c->defer_transport_maintenance) query_step(c,l,d,now);
    if (l->op==OP_IDLE) return;
    if (l->op==OP_TEACH_RETURN) {
        if (!d||d->generation!=l->generation||!d->present) { abandon_teaching_return(l,"Adapter connection changed."); return; }
        struct piper_sample *s=&d->feedback.arm[0];
        /* A hand-guided reentry relinquishes control. Unexpected motion after
           the controller accepts standby/CAN is different: request STOP rather
           than abandon a possibly moving controller with no position target. */
        if (piper_fresh(s,now)&&s->control<=1&&!teaching_active(s)) {
            for (unsigned i=0;i<6;++i) if (fabs(s->deg[i]-l->initial[i])>.5) {
                l->obstruction_joint=i+1;
                stop_lane(c,l,teach_seamless(l)?"Unexpected movement during same-pose teaching handoff; previous targets discarded.":
                    "Unexpected measured joint movement during teaching-to-CAN handoff; no position target was requested."); return;
            }
        }
        if (now-l->heartbeat>=lease_timeout_s) { abandon_teaching_return(l,"Control lease expired."); return; }
        const char *why=teaching_return_guard(c,l,d,now,l->recovering_at_pose);
        if (why) { abandon_teaching_return(l,why); return; }
        for (unsigned i=0;i<6;++i) if (fabs(d->feedback.arm[0].deg[i]-l->initial[i])>.5) {
            abandon_teaching_return(l,"Measured pose changed during the mode transition."); return;
        }
        if (now-(teach_seamless(l)?l->started:l->sent_at)>1.5) { abandon_teaching_return(l,"Mode acknowledgement timed out; no automatic retry or reset will run."); return; }
        if ((l->stage==STAGE_TEACH_STANDBY&&s->control!=2&&s->control!=0)||(l->stage==STAGE_TEACH_CAN&&s->control!=0&&s->control!=1)) {
            abandon_teaching_return(l,"Controller mode changed outside the requested standby/CAN sequence."); return;
        }
        if (s->status_at<=l->sent_at) return;
        if (l->stage==STAGE_TEACH_SEEDED) {
            if (s->control!=2) { abandon_teaching_return(l,"Controller left completed teaching before the CAN request."); return; }
            if (now-l->sent_at<.02) return;
            for (unsigned i=0;i<6;++i) if (s->joint_at[i]<=l->sent_at||d->feedback.motor[i].high_at<=l->sent_at) return;
            struct can_frame frames[4]; frames[0]=piper_mode(1);
            if (!piper_targets(l->initial,frames+1)||!can_bus_send(&c->bus,d,l->generation,frames,4)) {
                stop_lane(c,l,"Same-pose teaching handoff mode/hold could not finish transmitting; previous targets discarded."); return;
            }
            l->sent_at=piper_now(); l->stage=STAGE_TEACH_MODE;
            message(l,"teaching-return","CAN MoveJ requested with the frozen measured hold pose at 1%%. Waiting for fresh same-pose acknowledgement; no motor-power change or homing.");
            return;
        }
        if (l->stage==STAGE_TEACH_MODE&&s->control==1&&s->motion_mode==1) {
            /* New controller status alone cannot validate stale joint samples. */
            for (unsigned i=0;i<6;++i) if (s->joint_at[i]<=l->sent_at||(!d->simulated&&d->feedback.motor[i].high_at<=l->sent_at)) return;
            finish_teaching_return(c,l,d);
            return;
        }
        if (l->stage==STAGE_TEACH_STANDBY&&s->control==0) {
            struct can_frame frame=piper_mode(1); l->sent_at=now;
            if (!can_bus_send(&c->bus,d,l->generation,&frame,1)) { abandon_teaching_return(l,"CAN request could not be transmitted."); return; }
            l->stage=STAGE_TEACH_CAN;
            message(l,"teaching-return","Standby acknowledged. CAN requested; waiting for fresh controller feedback. Keep the arm supported.");
        } else if (l->stage==STAGE_TEACH_CAN&&s->control==1&&s->motion_mode==1) {
            finish(c,l,d,"CAN control acknowledged at zero. No reset, motor-power, homing or position command was sent; old targets were discarded.",false);
        }
        return;
    }
    if (!d||d->generation!=l->generation||!d->present) { stop_lane(c,l,"Adapter connection changed."); return; }
    if (l->follow==FOLLOW_LIVE&&now-l->follow_updated>=follow_input_timeout_s) {
        follow_hold(c,l,d,now,"Following input expired after 250 ms. Holding measured pose; no target replay."); return;
    }
    if (now-l->heartbeat>=lease_timeout_s) { stop_lane(c,l,"Control heartbeat expired."); return; }
    if (l->follow!=FOLLOW_LIVE&&now-l->started>(l->op==OP_CYCLE?180:l->op==OP_MOVE||l->op==OP_ALIGN||l->op==OP_GRIPPER?120:l->op==OP_ENABLE||l->op==OP_DISABLE?8:3)) {
        if (l->op==OP_ENABLE) {
            char reason[200]="Enable timed out; motors still disabled:";
            for (unsigned i=0;i<6;++i) if (!d->feedback.motor[i].enabled) {
                size_t used=strlen(reason); snprintf(reason+used,sizeof reason-used," J%u",i+1);
            }
            stop_lane(c,l,reason);
        } else if(l->op==OP_DISABLE) {
            char reason[240]="Disable timed out; motors not verified off:";bool missing=false;
            for(unsigned i=0;i<6;++i)if(!l->power_acknowledged[i]||d->feedback.motor[i].enabled) {
                size_t used=strlen(reason);snprintf(reason+used,sizeof reason-used," J%u",i+1);missing=true;
            }
            stop_lane(c,l,missing?reason:"Disable timed out waiting for fresh stationary all-motors-off feedback.");
        } else stop_lane(c,l,"Control action timed out.");
        return;
    }
    bool recovering=l->op==OP_RESET||l->op==OP_DISABLE;
    const char *why=health(d,now,l->op==OP_RESET?CHECK_RESET:recovering?CHECK_POWER:CHECK_CONTROL);
    if (why) { stop_lane(c,l,why); return; }
    struct piper_sample *s=&d->feedback.arm[0];
    if (recovering) {
        bool acknowledged=true;
        if(l->op==OP_DISABLE)for(unsigned i=0;i<6;++i) {
            struct piper_motor *motor=&d->feedback.motor[i];
            l->power_acknowledged[i]=l->power_requested[i]&&!motor->enabled&&
                motor->at>l->power_first_sent_at[i]&&s->joint_at[i]>l->power_first_sent_at[i];
            acknowledged=acknowledged&&l->power_acknowledged[i];
        }
        bool fresh=enabled(d,false,l->sent_at);
        for (unsigned i=0;i<6;++i) fresh=fresh&&s->joint_at[i]>l->sent_at;
        if (acknowledged&&fresh&&s->status_at>l->sent_at&&!s->error&&(s->arm_status==0||(l->op==OP_DISABLE&&l->disable_keeps_recovery))&&can_bus_strict_still_for(d,now)>=stationary_dwell_s) {
            if (!l->settled_at) l->settled_at=now;
            if (now-l->settled_at>=settle_hold_s) finish(c,l,d,l->op==OP_RESET?"Reset verified: motors disabled. Enable explicitly before motion.":l->disable_keeps_recovery?"All six motors verified disabled. Use Verify motors off to clear the previous recovery record.":"All six motors verified disabled. Ready to enable again.",l->op==OP_RESET||!l->disable_keeps_recovery);
        } else l->settled_at=0;
        if(l->op==OP_DISABLE&&now-l->last_tick>=.02) {
            l->last_tick=now;
            /* Retry only missing OFF acknowledgements, never enable, reset or
               transmit a pose while the supported arm is being powered off. */
            power_round_robin(c,l,d,false,now);
        }
        return;
    }
    double dt=now-l->last_tick; if (dt<.02) return;
    if (trajectory_following(l)&&dt>.050001) {
        follow_hold(c,l,d,now,"Trajectory reference tick exceeded 50 ms. Delayed references discarded; holding the measured pose. Start a new follow session to continue."); return;
    }
    l->last_tick=now; dt=fmin(dt,.05);
    if (l->op==OP_ENABLE) {
        bool all_acknowledged=true; double latest_request=0;
        for (unsigned i=0;i<6;++i) {
            struct piper_motor *motor=&d->feedback.motor[i];
            double baseline=l->power_requested[i]?l->enable_baseline[i]:l->initial[i];
            double allowed=l->power_requested[i]?.5:1;
            if (fabs(s->deg[i]-baseline)>allowed) {
                char reason[224];
                snprintf(reason,sizeof reason,"J%u moved %.3f degrees %s (baseline %.3f, measured %.3f; limit %.1f degrees).",i+1,
                    fabs(s->deg[i]-baseline),l->power_requested[i]?"after its enable request":"while still unpowered before its enable request",baseline,s->deg[i],allowed);
                l->obstruction_joint=i+1; stop_lane(c,l,reason); return;
            }
            if ((!l->power_requested[i]&&motor->enabled)||(l->power_acknowledged[i]&&!motor->enabled)) {
                char reason[160]; snprintf(reason,sizeof reason,"J%u motor power changed %s while enabling; no automatic re-enable.",i+1,
                    l->power_acknowledged[i]?"after its enabled acknowledgement":"before its enable request");
                l->obstruction_joint=i+1; stop_lane(c,l,reason); return;
            }
            if (l->power_requested[i]&&motor->enabled&&motor->at>l->power_first_sent_at[i]&&
                s->joint_at[i]>l->power_first_sent_at[i]&&motor->high_at>l->power_first_sent_at[i]) l->power_acknowledged[i]=true;
            all_acknowledged=all_acknowledged&&l->power_acknowledged[i];
            latest_request=fmax(latest_request,l->power_first_sent_at[i]);
        }
        /* The settling window begins only after every driver's own fresh
           acknowledgement. Earlier hand-supported stillness is not proof that
           the powered arm has settled. Keep every drift/power guard active. */
        if (all_acknowledged&&s->status_at>latest_request&&measured_stationary(d,now,latest_request)) {
            if (!l->settled_at) l->settled_at=now;
            if (now-l->settled_at>=settle_hold_s) {
                finish(c,l,d,"All six motors report enabled and stationary. No position target was sent.",true); return;
            }
        } else l->settled_at=0;
        power_round_robin(c,l,d,true,now);
        return;
    }
    if (!enabled(d,true,0)) { stop_lane(c,l,"Motor enable changed during motion."); return; }
    if (l->op!=OP_GRIPPER&&(s->control!=1||s->motion_mode!=1)&&l->sent_at&&now-l->sent_at>.5) { stop_lane(c,l,"Controller did not enter CAN MoveJ mode."); return; }
    if (l->pose_phase==POSE_SEEDING) {
        /* A mode request can activate the MCU's previously latched endpoint.
           Seed this admission's frozen measured pose before issuing any mode
           or new destination. Return to receive draining between the three
           seed writes and the normal four-frame mode/pose transaction. This
           is one shot per ordinary move, never a reference-stream rewrite. */
        if(!valid_target(l,d,l->target)) { stop_lane(c,l,l->error);return; }
        for (unsigned i=0;i<6;++i) if (fabs(s->deg[i]-l->initial[i])>.5) {
            l->obstruction_joint=i+1;
            stop_lane(c,l,"Measured pose changed before the position-control hold seed."); return;
        }
        struct can_frame seed[3];
        if (!piper_targets(l->initial,seed)||!can_bus_send(&c->bus,d,l->generation,seed,3)) {
            stop_lane(c,l,"Initial measured hold pose could not finish transmitting; position-mode entry cancelled."); return;
        }
        l->pose_phase=POSE_TRACKING;
        return;
    }
    double error=0;
    if (l->op==OP_MOVE||l->op==OP_ALIGN||l->op==OP_CYCLE) {
        if (!valid_target(l,d,l->target)) { stop_lane(c,l,l->error); return; }
        if (trajectory_following(l)) {
            enum trajectory_step_result step=trajectory_reference_step(l,d,now,dt);
            if (step==TRAJECTORY_STEP_INVALID) { stop_lane(c,l,l->error); return; }
            if (step==TRAJECTORY_STEP_BOUNDARY) {
                /* A software feasibility cancellation is not a motor fault.
                   Existing hard tracking/obstruction faults must still win
                   before retiring the reference with a verified measured hold. */
                for (unsigned i=0;i<6;++i) {
                    if (fabs(l->command[i]-s->deg[i])>3) { stop_lane(c,l,"Measured joint deviated more than 3 degrees from the commanded path."); return; }
                    struct obstruction_watch probe=l->obstruction[i];
                    if (obstruction_stalled(&probe,now,s->deg[i],l->command[i],l->target[i],l->tolerance)) {
                        char reason[200]; l->obstruction_joint=i+1;
                        snprintf(reason,sizeof reason,"Possible obstruction at J%u: no measured progress for 0.5 seconds (actual %.3f, commanded %.3f, target %.3f degrees).",i+1,s->deg[i],l->command[i],l->target[i]);
                        lane_stop(c,l,KIND_OBSTRUCTION,reason); return;
                    }
                }
                char reason[384]; snprintf(reason,sizeof reason,"%s Holding the verified measured pose; start a new follow session to continue.",l->error);
                follow_hold(c,l,d,now,reason); return;
            }
        }
        if (arrival_holding(l)&&(!valid_target(l,d,l->arrival_hold_pose)||now-l->arrival_hold_at>2)) {
            stop_lane(c,l,"Measured arrival hold did not verify stable fresh feedback within two seconds."); return;
        }
        if (!arrival_holding(l)&&leg_interpolated(l)) {
            if (l->op==OP_CYCLE&&!valid_target(l,d,l->initial)) { stop_lane(c,l,"The saved return pose no longer satisfies joint limits."); return; }
            double fraction=l->leg_span>0?fmin(1,l->leg_fraction+l->speed*dt/l->leg_span):1;
            for (unsigned i=0;i<6;++i) {
                double delta=l->target[i]-l->leg_start[i];
                if (fabs(delta)>.000001) {
                    double lead=(s->deg[i]+copysign(1,delta)-l->leg_start[i])/delta;
                    fraction=fmin(fraction,fmax(l->leg_fraction,lead));
                }
            }
            l->leg_fraction=fraction;
        }
        for (unsigned i=0;i<6;++i) {
            if (fabs(l->command[i]-s->deg[i])>3) { stop_lane(c,l,"Measured joint deviated more than 3 degrees from the commanded path."); return; }
            if (trajectory_following(l)) {
                /* All six quantized references were advanced transactionally
                   above; do not apply the legacy lead snap/interpolator. */
            } else if (arrival_holding(l)) {
                l->command[i]=l->arrival_hold_pose[i];
            } else if (leg_interpolated(l)) {
                l->command[i]=l->leg_start[i]+(l->target[i]-l->leg_start[i])*l->leg_fraction;
            } else if (l->op==OP_ALIGN) {
                /* An out-of-range intermediate target is not valid. Entry is
                   explicit, limited to 3 degrees, and uses 1% MCU speed. */
                if (fabs(s->deg[i]-l->initial[i])>fabs(l->target[i]-l->initial[i])+.5) { stop_lane(c,l,"Unexpected joint movement during range entry."); return; }
                l->command[i]=l->target[i];
            } else {
                double remaining=l->target[i]-l->command[i];
                l->command[i]+=copysign(fmin(fabs(remaining),l->speed*dt),remaining);
                if (fabs(l->command[i]-s->deg[i])>1) l->command[i]=s->deg[i]+copysign(1,l->command[i]-s->deg[i]);
            }
            error=fmax(error,fabs(l->target[i]-s->deg[i]));
            l->command[i]=clamp_limit(&d->feedback.limit[i],l->command[i]);
        }
        for (unsigned i=0;i<6;++i) {
            if (obstruction_stalled(&l->obstruction[i],now,s->deg[i],l->command[i],l->target[i],l->tolerance)) {
                char reason[200]; l->obstruction_joint=i+1;
                snprintf(reason,sizeof reason,"Possible obstruction at J%u: no measured progress for 0.5 seconds (actual %.3f, commanded %.3f, target %.3f degrees).",i+1,s->deg[i],l->command[i],l->target[i]);
                lane_stop(c,l,KIND_OBSTRUCTION,reason); return;
            }
        }
        if (!send_pose(c,l,d,l->command)) { stop_lane(c,l,"Joint command was not fully transmitted."); return; }
        if (!l->sent_at) l->sent_at=l->pose_tx_at;
    } else {
        if (!d->feedback.gripper_seen||!piper_recent(d->feedback.gripper_at,now)||d->feedback.gripper_fault) { stop_lane(c,l,"Gripper feedback missing or faulted."); return; }
        for (unsigned i=0;i<6;++i) if (fabs(s->deg[i]-l->initial[i])>.5) { stop_lane(c,l,"Arm joint moved during gripper control."); return; }
        double remaining=l->gripper_target-l->gripper_command,actual=d->feedback.gripper_mm;
        if (!configuration_current(d,d->feedback.gripper_config_seen,d->feedback.gripper_config_at,now)||d->feedback.gripper_max_mm!=l->gripper_max_mm) { stop_lane(c,l,"Gripper configuration changed or became unavailable."); return; }
        if (!isfinite(actual)||actual<(l->gripper_entry?-2:0)||actual>l->gripper_max_mm) { stop_lane(c,l,"Gripper reading left the supported range."); return; }
        if (l->gripper_entry&&actual<0) {
            /* Explicit bounded opening from a small closed encoder offset.
               Never transmit a negative target or rewrite the encoder zero. */
            l->gripper_command=1;
        } else {
            l->gripper_entry=false;
            l->gripper_command+=copysign(fmin(fabs(remaining),5*dt),remaining);
            if (fabs(l->gripper_command-actual)>1) l->gripper_command=actual+copysign(1,l->gripper_command-actual);
        }
        struct can_frame f;
        if (!piper_gripper(l->gripper_command,l->gripper_effort,true,&f)||!can_bus_send(&c->bus,d,l->generation,&f,1)) { stop_lane(c,l,"Gripper command failed."); return; }
        if (!l->sent_at) l->sent_at=now;
        error=fabs(l->gripper_target-actual);
    }
    // Retargeting never resets the per-joint obstruction watches above.
    if (l->follow==FOLLOW_LIVE) return;
    if (error<l->best_error-.02) { l->best_error=error; l->progress_at=now; }
    else if (error>l->tolerance&&now-l->progress_at>5) { stop_lane(c,l,"No measured progress toward the target for five seconds."); return; }
    bool final_command=true, post_target=true;
    if (l->op==OP_GRIPPER) {
        final_command=fabs(l->gripper_command-l->gripper_target)<.0005;
        post_target=d->feedback.gripper_at>l->final_sent_at&&d->feedback.gripper_enabled;
    } else for (unsigned i=0;i<6;++i) {
        final_command=final_command&&(arrival_holding(l)||fabs(l->command[i]-l->target[i])<.0005);
        post_target=post_target&&s->joint_at[i]>l->final_sent_at;
    }
    if (final_command&&!l->final_sent_at) { l->final_sent_at=l->op==OP_GRIPPER?now:l->pose_tx_at; post_target=false; }
    if (!final_command) l->final_sent_at=0;
    bool independent_arrival=(l->op==OP_MOVE||l->op==OP_ALIGN)&&measured_stationary(d,now,l->final_sent_at);
    double measured_hold[6];
    for (unsigned i=0;i<6&&independent_arrival;++i) {
        const struct piper_limit *lim=&d->feedback.limit[i];
        if (s->deg[i]<lim->min-limit_feedback_slop_deg||s->deg[i]>lim->max+limit_feedback_slop_deg) independent_arrival=false;
        measured_hold[i]=clamp_limit(lim,s->deg[i]);
        if (arrival_holding(l)&&fabs(s->deg[i]-l->arrival_hold_pose[i])>fmax(.03,fmin(l->tolerance,.1))) independent_arrival=false;
    }
    bool arrival_ack=arrival_holding(l)?independent_arrival&&(s->motion_status==0||s->motion_status==1):
        s->motion_status==0||(s->motion_status==1&&independent_arrival);
    if (!(l->final_sent_at&&post_target&&s->status_at>l->final_sent_at&&error<=(l->op==OP_GRIPPER?.5:l->tolerance)&&arrival_ack)) {
        l->settled_at=0; if (l->stage==STAGE_CYCLE_UPRIGHT) l->pause_until=now+2;
        return;
    }
    if (!l->settled_at) l->settled_at=now;
    if (now-l->settled_at<settle_hold_s) return;
    if (arrival_holding(l)||s->motion_status==1) {
        /* One packet that remains younger than 500ms is not evidence of a
           full settling interval. Busy-bit fallback and its hold must be
           covered by new encoder/status/velocity samples at the endpoint. */
        double endpoint=l->settled_at+settle_hold_s;
        if (s->status_at<endpoint) return;
        for (unsigned i=0;i<6;++i) if (s->joint_at[i]<endpoint||d->feedback.motor[i].high_at<endpoint) return;
    }
    if (s->motion_status==1&&!arrival_holding(l)) {
        /* Some MCUs retain busy for a sub-resolution residual already
           inside the operator's tolerance. Independent fresh encoder
           stillness is required first; then remove the old demand by
           sending a legal measured hold and verify new feedback. */
        if (!valid_target(l,d,measured_hold)) { stop_lane(c,l,l->error); return; }
        memcpy(l->arrival_hold_pose,measured_hold,sizeof measured_hold);
        l->pose_phase=POSE_ARRIVAL_HOLD; l->arrival_hold_at=now;
        memcpy(l->command,measured_hold,sizeof measured_hold);
        if (!send_pose(c,l,d,measured_hold)) { stop_lane(c,l,"Measured arrival hold could not be transmitted."); return; }
        l->final_sent_at=l->pose_tx_at; l->settled_at=0;
        message(l,"settling","Inside requested tolerance; verifying a stationary measured hold because the MCU still reports busy.");
        return;
    }
    if (l->stage==STAGE_ALIGN_FOLLOWUP) {
        if (range_distance(d)>0||!valid_target(l,d,l->range_followup_target)) {
            stop_lane(c,l,"Range entry did not verify a legal pose for the pending destination; that destination was discarded."); return;
        }
        memcpy(l->target,l->range_followup_target,sizeof l->target);
        l->op=OP_MOVE; l->stage=STAGE_MOVE_SYNCHRONIZED;
        l->speed=l->range_followup_speed; l->speed_percent=l->range_followup_percent;
        cycle_leg(l,d,now);
        message(l,"moving","Range entry verified. Moving to the explicitly requested destination; Stop movement cancels it.");
        return;
    }
    if (l->stage==STAGE_CYCLE_RAISING) {
        l->stage=STAGE_CYCLE_UPRIGHT; l->pause_until=now+2;
        message(l,"upright","Upright reached. Pausing for two seconds before returning.");
        return;
    }
    if (l->stage==STAGE_CYCLE_UPRIGHT) {
        if (now>=l->pause_until) {
            memcpy(l->target,l->initial,sizeof l->target); l->stage=STAGE_CYCLE_RETURNING; cycle_leg(l,d,now);
            message(l,"returning","Returning to the saved starting pose.");
        }
        return;
    }
    finish(c,l,d,l->op==OP_CYCLE?"Complete movement finished: upright and back to the starting pose within 0.2 degrees. Motors remain enabled.":l->op==OP_GRIPPER?"Gripper opening reached within 0.5 mm; gripper remains enabled.":arrival_holding(l)?"Joint target reached within the selected tolerance; fresh stationary measured hold verified despite retained MCU busy. Motors remain enabled.":"Joint target reached within the selected tolerance; motors remain enabled.",true);
}

void controller_tick(struct controller *c,double now)
{
    discover_lanes(c);
    c->defer_transport_maintenance=controller_any_motion_active(c);
    can_bus_receive(&c->bus,now);
    unsigned busy[CANBUS_MAX],idle[CANBUS_MAX],busy_count=0,idle_count=0,turn=c->next_tick_lane++;
    bool advanced=false;
    for (unsigned i=0;i<CANBUS_MAX;++i) if (*c->lanes[i].lane_device) {
        if (c->lanes[i].op!=OP_IDLE||c->lanes[i].query) busy[busy_count++]=i;
        else idle[idle_count++]=i;
    }
    /* Rotate among actual busy lanes, not unused inventory slots. Idle/absent
       devices cannot bias the priority of two simultaneously moving arms. */
    for (unsigned pass=0;pass<2;++pass) {
        unsigned count=pass?idle_count:busy_count,*order=pass?idle:busy;
        for (unsigned offset=0;offset<count;++offset) {
            unsigned index=order[(turn+offset)%count];
            struct controller_lane *l=&c->lanes[index];
            if (c->storage_failed&&l->op!=OP_IDLE) stop_lane(c,l,"Shared recovery-state storage failed.");
            /* Time is frozen, so no lease or watchdog can expire any more. */
            if (piper_clock_failed()&&l->op!=OP_IDLE) stop_lane(c,l,"Monotonic clock failed.");
            /* A previous lane's send can pump newer RX frames. Do not add real
               elapsed time to an already-advanced deterministic caller clock. */
            double lane_now=advanced?fmax(now,piper_now()):now;
            advanced=true;lane_tick(c,l,lane_now);lane_check(c,l);
        }
    }
    c->defer_transport_maintenance=false;
}

static cJSON *lane_motion(struct controller *c,const struct controller_lane *l,double now)
{
    struct can_device *device=can_bus_find(&c->bus,l->lane_device);
    cJSON *motion=cJSON_CreateObject();str(motion,"device_id",l->lane_device);
    num(motion,"generation",l->op!=OP_IDLE?l->generation:device?device->generation:NAN);
    num(motion,"request_generation",l->generation);str(motion,"phase",l->phase);str(motion,"message",l->message);str(motion,"error",l->error);flag(motion,"query_active",l->query);
    flag(motion,"active",l->op!=OP_IDLE); str(motion,"owner",l->owner);
    num(motion,"request_id",l->request_id); num(motion,"speed_deg_s",l->speed); num(motion,"tolerance_deg",l->tolerance);
    num(motion,"speed_percent",l->speed_percent);
    flag(motion,"following",l->follow==FOLLOW_LIVE);
    flag(motion,"trajectory_follow",trajectory_following(l));
    str(motion,"trajectory_transport","position-velocity");
    flag(motion,"streamed_position_hold",false);
    cJSON *reference_velocity=cJSON_CreateArray();
    if (trajectory_following(l)) for (unsigned i=0;i<6;++i) cJSON_AddItemToArray(reference_velocity,cJSON_CreateNumber(l->trajectory_velocity[i]));
    put(motion,"reference_velocity_deg_s",reference_velocity);
    flag(motion,"finishing_target",l->follow==FOLLOW_COMMITTED);
    num(motion,"follow_sequence",l->follow_sequence);
    num(motion,"teaching_return_stage",teach_stage(l->stage)?l->stage-STAGE_TEACH_STANDBY+1:0);
    num(motion,"elapsed_s",l->op!=OP_IDLE?now-l->started:0);
    str(motion,"sequence_stage",l->stage==STAGE_CYCLE_RAISING?"raising":l->stage==STAGE_CYCLE_UPRIGHT?"upright":l->stage==STAGE_CYCLE_RETURNING?"returning":"");
    if (l->stage==STAGE_ALIGN_FOLLOWUP) str(motion,"sequence_stage","range-entry-before-destination");
    flag(motion,"range_destination_pending",l->stage==STAGE_ALIGN_FOLLOWUP);
    flag(motion,"verifying_arrival_hold",arrival_holding(l));
    cJSON *return_pose=cJSON_CreateArray();
    if (l->op==OP_CYCLE) for (unsigned i=0;i<6;++i) cJSON_AddItemToArray(return_pose,cJSON_CreateNumber(l->initial[i]));
    put(motion,"return_degrees",return_pose);
    return motion;
}

cJSON *controller_state(struct controller *c,const char *token)
{
    double now=piper_now(); cJSON *root=cJSON_CreateObject(),*devices=cJSON_CreateArray(),*motions=cJSON_CreateArray();
    str(root,"kind","piper_direct_c"); str(root,"api_version","1.0"); flag(root,"simulated",c->bus.simulated);
    flag(root,"trajectory_follow_supported",true);
    flag(root,"multi_device_lanes_supported",true);num(root,"maximum_motion_lanes",CANBUS_MAX);
    flag(root,"any_motion_active",controller_any_motion_active(c));flag(root,"any_query_active",controller_any_query_active(c));
    /* Constant since the streamed-position transport was removed; kept for API compatibility. */
    flag(root,"streamed_position_transport_supported",false);
    flag(root,"streamed_position_wire_validation_supported",false);
    str(root,"token",token); str(root,"selected",c->selected); struct controller_lane scratch;
    const struct controller_lane *sel=lane_view(c,c->selected,&scratch);
    str(root,"message",sel->message); str(root,"phase",sel->phase);str(root,"error",sel->error);
    flag(root,"query_active",sel->query); flag(root,"storage_failed",c->storage_failed);
    put(root,"motion",lane_motion(c,sel,now));
    unsigned connected=0;
    for (unsigned n=0;n<CANBUS_MAX;++n) {
        struct can_device *d=&c->bus.devices[n]; if (!*d->id) continue;
        struct controller_lane device_scratch;
        const struct controller_lane *dl=lane_view(c,d->id,&device_scratch);
        struct piper_feedback *f=&d->feedback; struct piper_sample *s=&f->arm[0];
        cJSON *j=cJSON_CreateObject(); str(j,"id",d->id); str(j,"name",*d->label?d->label:d->name);
        str(j,"interface",d->name); str(j,"usb_path",d->path); str(j,"connection_error",d->problem);
        flag(j,"present",d->present); flag(j,"up",d->up); flag(j,"stable_identity",d->stable); num(j,"generation",d->generation);
        num(j,"rx_frames",(double)d->rx); num(j,"tx_frames",(double)d->tx); num(j,"local_frames",(double)d->local); num(j,"can_errors",(double)d->errors);
        flag(j,"link_verified",d->link_verified); num(j,"bitrate",d->link_verified?d->bitrate:NAN); num(j,"bus_state",d->link_verified?d->bus_state:NAN);
        num(j,"kernel_rx_packets",d->kernel_rx); num(j,"kernel_tx_packets",d->kernel_tx);
        num(j,"last_rx_age_s",d->last_rx?now-d->last_rx:NAN);
        bool fresh=piper_fresh(s,now); flag(j,"fresh",fresh); if (fresh) ++connected;
        bool in_action=dl->op!=OP_IDLE;
        const char *why=in_action?"Control action in progress.":controller_blocker(c,d,now); str(j,"blocker",why?why:""); flag(j,"recovery_required",blocked(c,d->id)&&!in_action);
        double range_correction=range_distance(d);
        unsigned outside_count=outside_range_count(d);
        char range_text[256];
        num(j,"range_entry_deg",range_correction);
        const char *range_blocker=why;
        if (!range_blocker&&dl->query) range_blocker="Wait for the information query to complete.";
        if (!range_blocker&&!enabled(d,true,0)) range_blocker="Enable all six arm motors before position control.";
        if (!range_blocker&&!position_control_stationary(d,now)) range_blocker="Wait for a stable measured pose before starting a new move.";
        bool zero_legal=true;
        for (unsigned i=0;i<6;++i) if (!f->limit[i].seen||f->limit[i].min>0||f->limit[i].max<0) zero_legal=false;
        cJSON *range=cJSON_CreateObject();
        flag(range,"required",isfinite(range_correction)&&range_correction>0);
        num(range,"correction_deg",range_correction); num(range,"maximum_correction_deg",3);
        str(range,"action","move-to-zero");
        flag(range,"can_move_to_zero",!range_blocker&&isfinite(range_correction)&&range_correction<=3&&zero_legal);
        str(range,"reason",range_blocker?range_blocker:!isfinite(range_correction)?"Read current joint limits first.":range_correction>3?
            "Range entry requires more than 3 degrees of correction. Inspect the pose and calibration.":!zero_legal?"Zero is outside a reported joint command range.":
            range_correction>0?range_reason(d,range_text,sizeof range_text):"");
        put(j,"range_recovery",range);
        cJSON *r=record(c,d->id); str(j,"stop_reason",yes(r,"blocked")?text_field(r,"reason"):text_field(legacy_stop(c,d->id),"reason"));
        str(j,"last_cleanup_error",text_field(r,"last_cleanup_error"));
        put(j,"last_fault",field(r,"last_fault")?cJSON_Duplicate(field(r,"last_fault"),true):cJSON_CreateNull());
        put(j,"stop_event",field(r,"stop_event")?cJSON_Duplicate(field(r,"stop_event"),true):cJSON_CreateNull());
        flag(j,"reset_pending",reset_pending(c,d->id));
        str(j,"firmware",f->version); str(j,"hardware",f->hardware); str(j,"build",f->build); flag(j,"firmware_supported",piper_supported(f));
        flag(j,"streamed_position_supported",false);
        cJSON *status=cJSON_CreateObject(); num(status,"control",s->status_seen?s->control:NAN); num(status,"arm_status",s->status_seen?s->arm_status:NAN);
        num(status,"motion_mode",s->status_seen?s->motion_mode:NAN); num(status,"error",s->status_seen?s->error:NAN); num(status,"age_s",s->status_seen?now-s->status_at:NAN); put(j,"status",status);
        num(status,"motion_status",s->status_seen?s->motion_status:NAN);
        num(status,"teach_state",s->status_seen?s->teach:NAN);
        num(status,"joint_communication_fault_mask",s->status_seen?s->error&0x3f:NAN);
        num(status,"joint_angle_limit_fault_mask",s->status_seen?(s->error>>8)&0x3f:NAN);
        num(status,"stationary_s",can_bus_strict_still_for(d,now));
        num(status,"pose_stationary_s",can_bus_pose_still_for(d,now));
        cJSON *teaching=cJSON_CreateObject();
        enum teaching_phase teach_phase=fresh?teaching_phase(s):TEACH_UNKNOWN;
        bool controller_stopped=fresh&&s->arm_status==1;
        bool active_teaching=fresh&&!controller_stopped&&teaching_active(s);
        bool return_required=fresh&&!controller_stopped&&(s->control==2||active_teaching);
        const char *return_reason=teaching_return_guard(c,dl,d,now,false);
        const char *recover_reason=teaching_return_guard(c,dl,d,now,true);
        str(teaching,"phase",teaching_phases[teach_phase].name); flag(teaching,"active",active_teaching);
        flag(teaching,"controller_stopped",controller_stopped);
        flag(teaching,"return_required",return_required);
        flag(teaching,"can_return",dl->op==OP_IDLE&&fresh&&s->control==2&&!return_reason);
        flag(teaching,"can_recover_at_pose",dl->op==OP_IDLE&&fresh&&s->control==2&&!recover_reason);
        str(teaching,"recover_at_pose_reason",recover_reason?recover_reason:"");
        flag(teaching,"recovery_teach_cycle_complete",dl->teaching_cycle==TEACH_CYCLE_COMPLETED&&
            dl->teaching_cycle_generation==d->generation);
        bool seamless=piper_seamless_teach_supported(f);
        flag(teaching,"seamless_supported",seamless); flag(teaching,"return_requires_zero",!seamless);
        str(teaching,"return_strategy",seamless?"measured-pose":"zero-standby-can");
        flag(teaching,"zero_within_tolerance",fresh&&teaching_zero(d)); num(teaching,"zero_tolerance_deg",1);
        str(teaching,"return_action","teach-return"); str(teaching,"return_reason",return_reason?return_reason:"");
        flag(teaching,"handoff_in_progress",dl->op==OP_TEACH_RETURN);
        /* A fresh arm_status STOPPED always maps to TEACH_STOPPED. */
        str(teaching,"label",!fresh?"Waiting for fresh teaching feedback":teaching_phases[teach_phase].label);
        put(j,"teaching",teaching);
        cJSON *joints=cJSON_CreateArray();
        for (unsigned i=0;i<6;++i) {
            cJSON *joint=cJSON_CreateObject(); struct piper_motor *m=&f->motor[i]; struct piper_limit *l=&f->limit[i];
            num(joint,"index",i+1); num(joint,"actual_deg",s->joint_seen[i]?s->deg[i]:NAN);
            bool active=(dl->op==OP_MOVE||dl->op==OP_ALIGN||dl->op==OP_CYCLE);
            num(joint,"target_deg",active?dl->target[i]:NAN); num(joint,"age_s",s->joint_seen[i]?now-s->joint_at[i]:NAN);
            num(joint,"command_deg",active?dl->command[i]:NAN);
            flag(joint,"fresh",s->joint_seen[i]&&piper_recent(s->joint_at[i],now));
            if (m->seen&&piper_recent(m->at,now)) flag(joint,"enabled",m->enabled); else put(joint,"enabled",cJSON_CreateNull());
            num(joint,"fault",m->seen?m->fault:NAN); num(joint,"voltage",m->seen?m->voltage:NAN); num(joint,"temperature",m->seen?m->temperature:NAN);
            num(joint,"motor_position_raw",m->high_seen&&piper_recent(m->high_at,now)?m->position_raw:NAN);
            num(joint,"motor_position_deg",m->high_seen&&piper_recent(m->high_at,now)?m->position_raw*180.0/(1000.0*PIPER_PI):NAN);
            num(joint,"current_a",m->high_seen&&piper_recent(m->high_at,now)?m->current:NAN); num(joint,"velocity_rad_s",m->high_seen&&piper_recent(m->high_at,now)?m->velocity:NAN);
            num(joint,"min_deg",l->seen?l->min:NAN); num(joint,"max_deg",l->seen?l->max:NAN); num(joint,"max_speed_rad_s",l->seen?l->speed:NAN);
            num(joint,"max_speed_raw",l->seen&&l->speed_raw_seen?l->speed_raw:NAN);
            num(joint,"max_speed_resolution_rad_s",l->seen&&l->speed_unit_known?l->speed_resolution_rad_s:NAN);
            flag(joint,"max_speed_unit_known",l->seen&&l->speed_unit_known);
            str(joint,"max_speed_unit_firmware",l->seen&&l->speed_unit_known?l->speed_firmware:"");
            num(joint,"range_entry_target_deg",l->seen&&s->joint_seen[i]?range_target(d,i):NAN);
            bool outside=joint_outside_range(d,i);
            bool can_move=!range_blocker&&isfinite(range_correction)&&(range_correction==0||(outside_count==1&&outside&&range_correction<=3));
            flag(joint,"outside_command_range",outside);
            flag(joint,"can_move",can_move);
            flag(joint,"can_move_inward",can_move&&(s->deg[i]<l->min||s->deg[i]>l->max));
            flag(joint,"can_move_negative",can_move&&s->deg[i]>l->min);
            flag(joint,"can_move_positive",can_move&&s->deg[i]<l->max);
            str(joint,"motion_blocker",can_move?"":range_blocker?range_blocker:range_reason(d,range_text,sizeof range_text));
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
        bool gripping=dl->op==OP_GRIPPER;
        num(grip,"command_mm",gripping?dl->gripper_command:NAN);
        num(grip,"effort_limit_nm",gripping?dl->gripper_effort:NAN);
        num(grip,"fault",f->gripper_seen?f->gripper_fault:NAN); put(j,"gripper",grip);
        cJSON *motion=lane_motion(c,dl,now);put(j,"motion",motion);cJSON_AddItemToArray(motions,cJSON_Duplicate(motion,true));
        cJSON_AddItemToArray(devices,j);
    }
    put(root,"motions",motions);
    put(root,"devices",devices); cJSON *discovery=cJSON_CreateObject(); num(discovery,"connected",connected); put(root,"discovery",discovery);
    return root;
}

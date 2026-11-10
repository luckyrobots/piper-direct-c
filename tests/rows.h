#ifndef PIPER_TEST_ROWS_H
#define PIPER_TEST_ROWS_H
/* The bench of the row tests (tests/test_rows_*.c): one simulated arm on the
   fake bus, driven only through the public entry points a client and the
   server loop use (controller_action, controller_tick, controller_state), and
   observed through the persisted record and the frames the bus recorded.

   Each test characterizes one row of a transition table in
   docs/target-architecture.md section 3.1 and carries the row as its ID.
   tests/check_rows.sh fails the build when a row has no test. */
#include "fixture.h"
#include "fake_bus.h"

#define ROWS_OWNER "row-test-client-0001"
#define ROWS_TICK .01

struct rows {
    struct controller c;
    struct can_device *d;
    char directory[FIXTURE_DIRECTORY];
    int status;             /* of the last request */
    char message[512];      /* message or error text of the last reply */
    double request;         /* request_id of the last admitted action */
    bool silent;            /* true: rows_run() stops renewing the lease */
};

/* One tick of the server loop on the injected clock. */
static inline void rows_tick(struct rows *r) { controller_tick(&r->c,fake_bus_advance(ROWS_TICK)); }
/* Firmware and limits as a completed information query leaves them. */
static inline void rows_configure(struct rows *r)
{
    fixture_configure(r->d,"S-V1.8-2",-170,170,3,fake_bus.now);
    r->d->feedback.gripper_config_seen=true; r->d->feedback.gripper_config_at=fake_bus.now;
}
/* A healthy, configured, stationary arm with all six motors on or all off. */
static inline void rows_open(struct rows *r,bool enabled)
{
    memset(r,0,sizeof *r);
    fake_bus_reset(100);
    fixture_directory(r->directory,sizeof r->directory,"piper-rows");
    CHECK(controller_init(&r->c,true,r->directory,""));
    r->d=&r->c.bus.devices[0]; r->c.bus.sim_enabled=enabled?63:0;
    fake_bus_watch(&r->c);
    rows_tick(r); rows_configure(r);
    for (unsigned i=0;i<100;++i) rows_tick(r);
}
static inline void rows_close(struct rows *r)
{
    fake_bus_watch(NULL);
    fixture_cleanup(&r->c,r->directory);
}
/* Stop and start the process: same state directory, same simulated arm. */
static inline void rows_restart(struct rows *r)
{
    unsigned enabled=r->c.bus.sim_enabled;
    controller_close(&r->c);
    CHECK(controller_init(&r->c,true,r->directory,""));
    r->d=&r->c.bus.devices[0]; r->c.bus.sim_enabled=enabled;
    rows_tick(r); rows_configure(r);
    for (unsigned i=0;i<100;++i) rows_tick(r);
}

/* A request body with the identity and attestations every action needs. */
static inline cJSON *rows_body(struct rows *r,const char *action)
{
    cJSON *j=cJSON_CreateObject();
    cJSON_AddStringToObject(j,"device_id",r->d->id); cJSON_AddNumberToObject(j,"generation",r->d->generation);
    cJSON_AddStringToObject(j,"owner",ROWS_OWNER); cJSON_AddStringToObject(j,"action",action);
    cJSON_AddBoolToObject(j,"standalone",true); cJSON_AddBoolToObject(j,"clear",true); cJSON_AddBoolToObject(j,"supported",true);
    return j;
}
/* Send `body` (consumed) to `route`; returns the HTTP status. */
static inline int rows_post(struct rows *r,const char *route,cJSON *body)
{
    cJSON *reply=controller_action(&r->c,route,body,&r->status);
    const cJSON *message=cJSON_GetObjectItemCaseSensitive(reply,r->status<400?"message":"error"),*request=cJSON_GetObjectItemCaseSensitive(reply,"request_id");
    snprintf(r->message,sizeof r->message,"%s",cJSON_IsString(message)?message->valuestring:"");
    if (r->status==202&&cJSON_IsNumber(request)) r->request=request->valuedouble;
    cJSON_Delete(reply); cJSON_Delete(body);
    return r->status;
}
static inline int rows_command(struct rows *r,const char *action) { return rows_post(r,"/api/command",rows_body(r,action)); }
static inline int rows_joint(struct rows *r,unsigned joint,double degrees)
{
    cJSON *j=rows_body(r,"joint"); cJSON_AddNumberToObject(j,"joint",joint); cJSON_AddNumberToObject(j,"degrees",degrees);
    return rows_post(r,"/api/command",j);
}
/* The operator's STOP for this arm. */
static inline int rows_stop(struct rows *r)
{
    cJSON *j=cJSON_CreateObject(); cJSON_AddStringToObject(j,"device_id",r->d->id);
    return rows_post(r,"/api/stop",j);
}
static inline enum operation rows_op(struct rows *r) { return LANE(&r->c)->op; }
static inline bool rows_idle(struct rows *r) { return rows_op(r)==OP_IDLE; }
/* Run the loop for `seconds`, renewing the lease like a live client does. */
static inline void rows_run(struct rows *r,double seconds)
{
    for (double end=fake_bus.now+seconds-ROWS_TICK/2;fake_bus.now<end;) {
        if (!r->silent&&!rows_idle(r)) {
            cJSON *j=rows_body(r,"heartbeat"); cJSON_AddNumberToObject(j,"request_id",r->request);
            int status; cJSON_Delete(controller_action(&r->c,"/api/heartbeat",j,&status)); cJSON_Delete(j);
        }
        rows_tick(r);
    }
}
/* Run until the lane is idle; fails when it is not within `limit` seconds. */
static inline void rows_finish(struct rows *r,double limit)
{
    for (double end=fake_bus.now+limit;!rows_idle(r);) { CHECK(fake_bus.now<end); rows_run(r,ROWS_TICK); }
}

/* The persisted record of the arm, read back from control-state.json. */
static inline cJSON *rows_saved(struct rows *r)
{
    char path[1100]; snprintf(path,sizeof path,"%s/control-state.json",r->directory);
    cJSON *file=fixture_read_json(path);
    cJSON *record=cJSON_DetachItemFromObjectCaseSensitive(cJSON_GetObjectItemCaseSensitive(file,"devices"),r->d->id);
    cJSON_Delete(file); CHECK(record);
    return record;
}
static inline const char *rows_saved_text(struct rows *r,const char *key)
{
    static char text[4][256]; static unsigned turn;
    char *out=text[turn++%4]; cJSON *record=rows_saved(r);
    const cJSON *value=cJSON_GetObjectItemCaseSensitive(record,key);
    snprintf(out,sizeof text[0],"%s",cJSON_IsString(value)?value->valuestring:"");
    cJSON_Delete(record);
    return out;
}
/* The record is `state` with first cause `kind`, on disk. */
static inline bool rows_latch(struct rows *r,const char *state,const char *kind)
{
    return !strcmp(rows_saved_text(r,"state"),state)&&!strcmp(rows_saved_text(r,"kind"),kind);
}
static inline bool rows_saved_has(struct rows *r,const char *key)
{
    cJSON *record=rows_saved(r); const cJSON *value=cJSON_GetObjectItemCaseSensitive(record,key);
    bool has=cJSON_IsObject(value)||(cJSON_IsString(value)&&*value->valuestring)||cJSON_IsTrue(value);
    cJSON_Delete(record);
    return has;
}
/* The lane's display phase ("moving", "complete", "stopped", ...). */
static inline bool rows_phase(struct rows *r,const char *phase) { return !strcmp(LANE(&r->c)->phase,phase); }
/* The reason saved with the first STOP of the current incident. */
static inline const char *rows_stop_reason(struct rows *r)
{
    static char text[256]; cJSON *record=rows_saved(r);
    const cJSON *reason=cJSON_GetObjectItemCaseSensitive(cJSON_GetObjectItemCaseSensitive(record,"stop_event"),"reason");
    snprintf(text,sizeof text,"%s",cJSON_IsString(reason)?reason->valuestring:"");
    cJSON_Delete(record);
    return text;
}
/* What the dashboard is told about the arm. */
static inline bool rows_reports(struct rows *r,const char *key)
{
    cJSON *state=controller_state(&r->c,"rows");
    bool yes=cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(cJSON_GetArrayItem(cJSON_GetObjectItemCaseSensitive(state,"devices"),0),key));
    cJSON_Delete(state);
    return yes;
}
/* Make every later write of control-state.json fail, or work again. */
static inline void rows_storage(struct rows *r,bool working)
{
    snprintf(r->c.state_dir,sizeof r->c.state_dir,"%s%s",r->directory,working?"":"/missing");
}
/* Frames with CAN ID `id` whose first byte is `first`, emitted since `mark`. */
static inline unsigned rows_sent(size_t mark,unsigned id,int first)
{
    unsigned n=0; const struct fake_frame *f;
    while ((f=fake_bus_next(&mark,0,id))) if (first<0||f->frame.data[0]==first) ++n;
    return n;
}
#define ROWS_STOP_FRAME 0x150,1
#define ROWS_RESET_FRAME 0x150,2
#endif

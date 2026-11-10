#define _GNU_SOURCE
/* Characterization of the persistent recovery latch as the core implements
   it today: one test per row of the C-C table and per C-C invariant in
   docs/target-architecture.md section 3.1.

   Today's record spells the states "clear", "latched" and "reset_pending",
   with the first cause in "kind" (intent, obstruction, power, stop, reset).
   The target INTENT is "latched" with kind "intent" or "power"; the target
   INCIDENT is "latched" with an active stop event. */
#include "rows.h"

static struct rows r;

static bool incident(void) { return rows_saved_has(&r,"stop_event_active")&&rows_saved_has(&r,"stop_event"); }
/* An idle arm whose reset is still pending: the reset frame did not go out. */
static void reset_pending_arm(void)
{
    rows_open(&r,false);
    fake_bus_fail(0,0x150,1);
    CHECK(rows_command(&r,"reset")==409&&rows_idle(&r));
    CHECK(rows_latch(&r,"reset_pending","reset"));
    rows_run(&r,.5);
}

static void l1_admission_persists_intent(void)
{
    rows_open(&r,false); CHECK(rows_latch(&r,"clear",""));
    size_t mark=fake_bus_mark();
    CHECK(rows_command(&r,"enable")==202);
    CHECK(rows_latch(&r,"latched","intent")&&!incident());
    CHECK(!strcmp(rows_saved_text(&r,"reason"),"Control action interrupted before verified completion"));
    rows_run(&r,.1);
    const struct fake_frame *first=fake_bus_next(&mark,0,0x471);
    CHECK(first&&!strcmp(first->state,"latched")&&!strcmp(first->kind,"intent"));
    rows_stop(&r); rows_close(&r);
}
static void l2_disable_persists_power(void)
{
    rows_open(&r,true);
    size_t mark=fake_bus_mark();
    CHECK(rows_command(&r,"disable")==202&&rows_op(&r)==OP_DISABLE);
    CHECK(rows_latch(&r,"latched","power")&&!incident());
    CHECK(!strcmp(rows_saved_text(&r,"reason"),"Motors disabled; recovery verification required"));
    rows_run(&r,.1);
    const struct fake_frame *first=fake_bus_next(&mark,0,0x471);
    CHECK(first&&!strcmp(first->state,"latched")&&!strcmp(first->kind,"power"));
    rows_stop(&r); rows_close(&r);
}
static void l3_completion_clears_intent(void)
{
    rows_open(&r,false);
    CHECK(rows_command(&r,"enable")==202);
    rows_finish(&r,8);
    CHECK(rows_phase(&r,"complete")&&r.c.bus.sim_enabled==63);
    CHECK(rows_latch(&r,"clear","")&&!rows_reports(&r,"recovery_required"));
    rows_close(&r);
}
static void teaching(struct can_bus *bus,double now)
{
    (void)now; struct piper_sample *s=&bus->devices[0].feedback.arm[0];
    s->teach=PIPER_TEACH_RECORDING; s->arm_status=PIPER_ARM_STATUS_TEACHING;
}
static void l4_relinquish_clears_own_intent(void)
{
    rows_open(&r,true);
    CHECK(rows_joint(&r,1,20)==202);
    rows_run(&r,.3); CHECK(rows_op(&r)==OP_MOVE&&rows_latch(&r,"latched","intent"));
    size_t mark=fake_bus_mark();
    fake_bus.after_receive=teaching; rows_run(&r,.05);
    CHECK(rows_idle(&r)&&rows_phase(&r,"teaching"));
    CHECK(rows_latch(&r,"clear","")&&!rows_sent(mark,ROWS_STOP_FRAME));
    rows_close(&r);
    /* An incident under the running action is not this lane's intent: it stays. */
    rows_open(&r,true);
    CHECK(rows_stop(&r)==200&&rows_latch(&r,"latched","stop"));
    rows_run(&r,.6);
    CHECK(rows_command(&r,"disable")==202);
    fake_bus.after_receive=teaching; rows_run(&r,.05);
    CHECK(rows_idle(&r)&&rows_phase(&r,"teaching"));
    CHECK(rows_latch(&r,"latched","stop")&&incident());
    rows_close(&r);
}
static void l5_disable_completion_clears(void)
{
    rows_open(&r,true);
    CHECK(rows_command(&r,"disable")==202&&!LANE(&r.c)->disable_keeps_recovery);
    rows_finish(&r,8);
    CHECK(rows_phase(&r,"complete")&&r.c.bus.sim_enabled==0);
    CHECK(rows_latch(&r,"clear",""));
    rows_close(&r);
}
static void l6_stop_creates_incident(void)
{
    /* From clear. */
    rows_open(&r,true); CHECK(!rows_saved_has(&r,"stop_event"));
    size_t mark=fake_bus_mark();
    CHECK(rows_stop(&r)==200&&rows_sent(mark,ROWS_STOP_FRAME)==1);
    CHECK(rows_latch(&r,"latched","stop")&&incident()&&rows_reports(&r,"recovery_required"));
    rows_close(&r);
    /* From an action's intent. */
    rows_open(&r,false);
    CHECK(rows_command(&r,"enable")==202&&rows_latch(&r,"latched","intent"));
    rows_run(&r,.05); mark=fake_bus_mark();
    CHECK(rows_stop(&r)==200&&rows_idle(&r)&&rows_sent(mark,ROWS_STOP_FRAME)==1);
    CHECK(rows_latch(&r,"latched","stop")&&incident());
    cJSON *record=rows_saved(&r); const cJSON *event=cJSON_GetObjectItem(record,"stop_event");
    CHECK(!strcmp(cJSON_GetObjectItem(event,"kind")->valuestring,"stop"));
    CHECK(cJSON_GetObjectItem(event,"request_id")->valuedouble==r.request);
    CHECK(cJSON_GetArraySize(cJSON_GetObjectItem(event,"joints"))==6);
    cJSON_Delete(record);
    rows_close(&r);
}
static void l7_incident_keeps_first_stop(void)
{
    rows_open(&r,true);
    /* The first STOP is a lease expiry; the operator's later STOP does not replace it. */
    CHECK(rows_joint(&r,1,20)==202);
    r.silent=true; rows_run(&r,1); r.silent=false;
    CHECK(rows_idle(&r)&&incident());
    char first[256],later[256]; snprintf(first,sizeof first,"%s",rows_stop_reason(&r)); CHECK(strstr(first,"heartbeat expired"));
    rows_run(&r,.6);
    CHECK(rows_stop(&r)==200); snprintf(later,sizeof later,"%s",rows_stop_reason(&r));
    CHECK(!strcmp(first,later)&&rows_latch(&r,"latched","stop")&&!rows_saved_has(&r,"last_cleanup_error"));
    /* A failed disable is cleanup: its error is kept beside the incident. */
    rows_run(&r,.6);
    CHECK(rows_command(&r,"disable")==202&&LANE(&r.c)->disable_keeps_recovery);
    fake_bus_fail(0,0x471,1); rows_run(&r,.3);
    CHECK(rows_idle(&r)&&rows_phase(&r,"stopped"));
    snprintf(later,sizeof later,"%s",rows_stop_reason(&r));
    CHECK(!strcmp(first,later)&&rows_latch(&r,"latched","stop")&&rows_saved_has(&r,"last_cleanup_error"));
    CHECK(strstr(rows_saved_text(&r,"reason"),"heartbeat expired"));
    rows_close(&r);
}
static void l8_reset_pending_survives_stop(void)
{
    rows_open(&r,false);
    CHECK(rows_command(&r,"reset")==202&&rows_op(&r)==OP_RESET);
    CHECK(rows_latch(&r,"reset_pending","reset"));
    CHECK(rows_stop(&r)==200&&rows_idle(&r));
    CHECK(rows_latch(&r,"reset_pending","reset")&&incident()&&rows_reports(&r,"reset_pending"));
    char first[256],later[256]; snprintf(first,sizeof first,"%s",rows_stop_reason(&r));
    CHECK(rows_stop(&r)==200); snprintf(later,sizeof later,"%s",rows_stop_reason(&r));
    CHECK(rows_latch(&r,"reset_pending","reset")&&!strcmp(first,later));
    rows_close(&r);
}
static void l9_reset_persists_before_frame(void)
{
    rows_open(&r,false);
    CHECK(rows_stop(&r)==200&&rows_latch(&r,"latched","stop"));
    rows_run(&r,.6);
    cJSON *j=rows_body(&r,"reset"); cJSON_DeleteItemFromObject(j,"clear");
    CHECK(rows_post(&r,"/api/command",j)==400&&rows_latch(&r,"latched","stop"));
    j=rows_body(&r,"reset"); cJSON_DeleteItemFromObject(j,"supported");
    CHECK(rows_post(&r,"/api/command",j)==400&&rows_latch(&r,"latched","stop"));
    size_t mark=fake_bus_mark();
    CHECK(rows_command(&r,"reset")==202&&rows_op(&r)==OP_RESET);
    CHECK(rows_latch(&r,"reset_pending","reset")&&incident());
    CHECK(!strcmp(rows_saved_text(&r,"reason"),"Reset pending verification"));
    const struct fake_frame *reset=fake_bus_next(&mark,0,0x150);
    CHECK(reset&&reset->frame.data[0]==2&&!strcmp(reset->state,"reset_pending")&&!strcmp(reset->kind,"reset"));
    rows_stop(&r); rows_close(&r);
    /* While a reset is pending, a retry needs a stopped, all-off, error-free arm. */
    reset_pending_arm();
    CHECK(r.d->feedback.arm[0].arm_status!=PIPER_ARM_STATUS_STOPPED);
    mark=fake_bus_mark();
    CHECK(rows_command(&r,"reset")==409&&!rows_sent(mark,ROWS_RESET_FRAME));
    CHECK(rows_stop(&r)==200); rows_run(&r,.6);
    CHECK(r.d->feedback.arm[0].arm_status==PIPER_ARM_STATUS_STOPPED);
    CHECK(rows_command(&r,"reset")==202&&rows_sent(mark,ROWS_RESET_FRAME)==1);
    rows_stop(&r); rows_close(&r);
}
static void l10_observed_reset_clears(void)
{
    rows_open(&r,true);
    CHECK(rows_command(&r,"reset")==202);
    rows_finish(&r,3);
    CHECK(rows_phase(&r,"complete")&&r.c.bus.sim_enabled==0);
    CHECK(rows_latch(&r,"clear","")&&!rows_reports(&r,"reset_pending"));
    rows_close(&r);
}
static void l11_verify_reset_needs_client_flag(void)
{
    reset_pending_arm();
    size_t mark=fake_bus_mark();
    CHECK(rows_command(&r,"verify-reset")==409&&rows_latch(&r,"reset_pending","reset"));
    cJSON *j=rows_body(&r,"verify-reset"); cJSON_AddBoolToObject(j,"reset_completed",true);
    CHECK(rows_post(&r,"/api/command",j)==200&&rows_idle(&r));
    CHECK(rows_latch(&r,"clear","")&&fake_bus_mark()==mark);
    rows_close(&r);
    /* Hardware evidence is still required: motors on refuse it. */
    reset_pending_arm(); r.c.bus.sim_enabled=63; rows_run(&r,.6);
    j=rows_body(&r,"verify-reset"); cJSON_AddBoolToObject(j,"reset_completed",true);
    CHECK(rows_post(&r,"/api/command",j)==409&&rows_latch(&r,"reset_pending","reset"));
    rows_close(&r);
}
static void l12_verify_disabled_clears(void)
{
    rows_open(&r,true);
    CHECK(rows_stop(&r)==200&&incident());
    r.c.bus.sim_enabled=0; rows_run(&r,.6);
    /* The arm still reports the STOP it was sent: all off, but not healthy. */
    CHECK(r.d->feedback.arm[0].arm_status==PIPER_ARM_STATUS_STOPPED);
    CHECK(rows_command(&r,"verify-disabled")==409&&rows_latch(&r,"latched","stop"));
    /* After a power cycle the status is normal again. Motors on: refused. */
    r.c.bus.sim_enabled=63; rows_restart(&r);
    CHECK(!r.d->feedback.arm[0].arm_status&&incident());
    size_t mark=fake_bus_mark();
    CHECK(rows_command(&r,"verify-disabled")==409&&rows_latch(&r,"latched","stop"));
    r.c.bus.sim_enabled=0; rows_run(&r,.6);
    CHECK(rows_command(&r,"verify-disabled")==200);
    CHECK(rows_latch(&r,"clear","")&&!rows_saved_has(&r,"stop_event_active")&&fake_bus_mark()==mark);
    rows_close(&r);
    reset_pending_arm();
    CHECK(rows_command(&r,"verify-disabled")==409&&rows_latch(&r,"reset_pending","reset"));
    rows_close(&r);
}
static void l13_verify_current_clears(void)
{
    rows_open(&r,true);
    CHECK(rows_joint(&r,1,5)==202); LANE(&r.c)->op=OP_IDLE; /* the process dies mid-action */
    rows_restart(&r);
    CHECK(rows_latch(&r,"latched","intent")&&rows_reports(&r,"recovery_required"));
    cJSON *j=rows_body(&r,"verify-current"); cJSON_DeleteItemFromObject(j,"clear");
    CHECK(rows_post(&r,"/api/command",j)==400&&rows_latch(&r,"latched","intent"));
    size_t mark=fake_bus_mark();
    r.c.bus.sim_enabled=21; rows_run(&r,.6); /* three motors on, three off */
    CHECK(rows_command(&r,"verify-current")==409&&rows_latch(&r,"latched","intent"));
    r.c.bus.sim_enabled=63; rows_run(&r,.6);
    CHECK(rows_command(&r,"verify-current")==200);
    CHECK(rows_latch(&r,"clear","")&&fake_bus_mark()==mark);
    rows_close(&r);
    reset_pending_arm();
    CHECK(rows_command(&r,"verify-current")==409&&rows_latch(&r,"reset_pending","reset"));
    rows_close(&r);
}
static void l15_failed_write_blocks(void)
{
    rows_open(&r,false);
    size_t mark=fake_bus_mark(); uint64_t tx=r.d->tx;
    rows_storage(&r,false);
    CHECK(rows_command(&r,"enable")==500&&rows_idle(&r)&&r.c.storage_failed);
    CHECK(fake_bus_mark()==mark&&r.d->tx==tx);
    rows_storage(&r,true);
    /* The file still says clear. Memory already holds the intent the write was for. */
    CHECK(rows_latch(&r,"clear","")&&rows_reports(&r,"recovery_required"));
    CHECK(rows_command(&r,"enable")==409&&strstr(r.message,"storage failed"));
    CHECK(rows_command(&r,"verify-current")==409&&fake_bus_mark()==mark);
    CHECK(strstr(controller_blocker(&r.c,r.d,fake_bus.now),"storage failed"));
    rows_restart(&r);
    CHECK(!r.c.storage_failed&&rows_latch(&r,"clear","")&&!rows_reports(&r,"recovery_required"));
    rows_close(&r);
}
static void write_file(const char *directory,const char *name,const char *text)
{
    char path[400]; snprintf(path,sizeof path,"%s/%s",directory,name);
    FILE *f=fopen(path,"wb"); CHECK(f); CHECK(fputs(text,f)>=0); CHECK(!fclose(f));
}
static bool device_reports(struct controller *c,const char *id,const char *key)
{
    cJSON *state=controller_state(c,"rows"),*device; bool yes=false;
    cJSON_ArrayForEach(device,cJSON_GetObjectItem(state,"devices"))
        if (!strcmp(cJSON_GetObjectItem(device,"id")->valuestring,id)) yes=cJSON_IsTrue(cJSON_GetObjectItem(device,key));
    cJSON_Delete(state);
    return yes;
}
static void l16_legacy_stop_blocks(void)
{
    char state[FIXTURE_DIRECTORY],legacy[FIXTURE_DIRECTORY]; struct controller c;
    fake_bus_reset(100);
    fixture_directory(state,sizeof state,"piper-rows"); fixture_directory(legacy,sizeof legacy,"piper-rows-legacy");
    write_file(legacy,"calibrations.json","{\"slots\":[{\"device_id\":\"legacy-arm\",\"name\":\"Old arm\"},{\"device_id\":\"legacy-reset\",\"name\":\"Old reset\"},{\"device_id\":\"legacy-clean\",\"name\":\"Clean\"}]}");
    write_file(legacy,"motion-stops.json","{\"stops\":[{\"device_id\":\"legacy-arm\",\"at\":\"2025-01-01T00:00:00Z\",\"reason\":\"Operator stop\",\"source\":\"stop\"},"
        "{\"device_id\":\"legacy-reset\",\"at\":\"2025-01-02T00:00:00Z\",\"reason\":\"Reset\",\"source\":\"reset_intent\"}]}");
    CHECK(controller_init(&c,false,state,legacy));
    CHECK(device_reports(&c,"legacy-arm","recovery_required")&&!device_reports(&c,"legacy-arm","reset_pending"));
    CHECK(device_reports(&c,"legacy-reset","recovery_required")&&device_reports(&c,"legacy-reset","reset_pending"));
    CHECK(!device_reports(&c,"legacy-clean","recovery_required"));
    /* Today the legacy entry is consulted on every read; the record itself is not converted. */
    const cJSON *record=cJSON_GetObjectItem(cJSON_GetObjectItem(c.saved,"devices"),"legacy-arm");
    CHECK(record&&!cJSON_IsTrue(cJSON_GetObjectItem(record,"blocked"))&&!cJSON_GetObjectItem(record,"stop_event"));
    /* The acknowledgement is persisted: the same entry no longer blocks after a restart. */
    cJSON_AddStringToObject((cJSON *)record,"legacy_ack","2025-01-01T00:00:00Z");
    CHECK(!device_reports(&c,"legacy-arm","recovery_required"));
    controller_close(&c);
    char path[400];
    snprintf(path,sizeof path,"%s/control-state.json",state); CHECK(!unlink(path)); CHECK(!rmdir(state));
    snprintf(path,sizeof path,"%s/calibrations.json",legacy); CHECK(!unlink(path));
    snprintf(path,sizeof path,"%s/motion-stops.json",legacy); CHECK(!unlink(path)); CHECK(!rmdir(legacy));
}

/* C1 on the persisted record. */
static void representable(void)
{
    cJSON *record=rows_saved(&r);
    bool blocked=cJSON_IsTrue(cJSON_GetObjectItem(record,"blocked")),pending=cJSON_IsTrue(cJSON_GetObjectItem(record,"reset_pending"));
    bool active=cJSON_IsTrue(cJSON_GetObjectItem(record,"stop_event_active"));
    const char *state=cJSON_GetObjectItem(record,"state")->valuestring,*kind=cJSON_GetObjectItem(record,"kind")->valuestring;
    CHECK(blocked==(strcmp(state,"clear")!=0)); CHECK(pending==!strcmp(state,"reset_pending"));
    CHECK(!active||blocked); CHECK(!active||cJSON_IsObject(cJSON_GetObjectItem(record,"stop_event")));
    CHECK(!*kind==!blocked);
    cJSON_Delete(record);
}
static void c1_record_is_representable(void)
{
    rows_open(&r,false); representable();
    CHECK(rows_command(&r,"enable")==202); representable();
    rows_finish(&r,8); representable();
    CHECK(rows_joint(&r,1,10)==202); representable();
    rows_run(&r,.3); CHECK(rows_stop(&r)==200); representable();
    rows_run(&r,.6); CHECK(rows_command(&r,"reset")==202); representable();
    CHECK(rows_stop(&r)==200); representable();
    rows_run(&r,.6); CHECK(rows_command(&r,"reset")==202);
    rows_finish(&r,3); representable(); CHECK(rows_latch(&r,"clear",""));
    /* The evidence of the cleared incident stays in the file, marked inactive. */
    CHECK(rows_saved_has(&r,"stop_event")&&!rows_saved_has(&r,"stop_event_active"));
    rows_close(&r);
}
static bool authorized(const struct fake_frame *f)
{
    unsigned id=f->frame.can_id;
    return id==0x471||id==0x151||(id>=0x155&&id<=0x157)||id==0x159||(id==0x150&&f->frame.data[0]!=1);
}
static void c2_persisted_before_frames(void)
{
    rows_open(&r,false);
    size_t mark=fake_bus_mark(),cursor=mark; unsigned seen=0;
    CHECK(rows_command(&r,"enable")==202); rows_finish(&r,8);
    CHECK(rows_joint(&r,1,3)==202); rows_finish(&r,20); CHECK(rows_phase(&r,"complete"));
    CHECK(rows_command(&r,"disable")==202); rows_finish(&r,8);
    CHECK(rows_command(&r,"reset")==202); rows_finish(&r,3);
    CHECK(rows_latch(&r,"clear",""));
    for (const struct fake_frame *f;(f=fake_bus_next(&cursor,0,0));) if (authorized(f)) {
        ++seen; CHECK(strcmp(f->state,"clear")&&*f->state&&*f->kind);
    }
    CHECK(seen>20&&rows_sent(mark,0x471,-1)&&rows_sent(mark,0x151,-1)&&rows_sent(mark,0x155,-1)&&rows_sent(mark,ROWS_RESET_FRAME)==1);
    /* STOP is the exception: its frame leaves before the incident is saved. */
    mark=fake_bus_mark();
    CHECK(rows_stop(&r)==200);
    const struct fake_frame *stop=fake_bus_next(&mark,0,0x150);
    CHECK(stop&&stop->frame.data[0]==1&&!strcmp(stop->state,"clear")&&rows_latch(&r,"latched","stop"));
    rows_close(&r);
}
static void c3_eligibility_ignores_reason(void)
{
    /* The same latched record, with its display text rewritten, relinquishes
       to teaching by kind alone: intent clears, power does not. */
    const char *kinds[]={"intent","power"};
    for (unsigned i=0;i<2;++i) {
        rows_open(&r,true);
        CHECK(rows_joint(&r,1,20)==202); rows_run(&r,.3); CHECK(rows_latch(&r,"latched","intent"));
        cJSON *record=cJSON_GetObjectItem(cJSON_GetObjectItem(r.c.saved,"devices"),r.d->id);
        cJSON_ReplaceItemInObject(record,"reason",cJSON_CreateString("Possible obstruction at J1: text only"));
        cJSON_ReplaceItemInObject(record,"kind_reason",cJSON_CreateString("Possible obstruction at J1: text only"));
        cJSON_ReplaceItemInObject(record,"kind",cJSON_CreateString(kinds[i]));
        fake_bus.after_receive=teaching; rows_run(&r,.05);
        CHECK(rows_idle(&r)&&rows_phase(&r,"teaching"));
        CHECK(rows_reports(&r,"recovery_required")==(i==1));
        rows_close(&r);
    }
}
static void c4_client_fields_gate_transitions(void)
{
    /* Identical hardware evidence; only what the client asserts differs. */
    reset_pending_arm();
    cJSON *j=rows_body(&r,"verify-reset"); cJSON_AddBoolToObject(j,"reset_completed",false);
    CHECK(rows_post(&r,"/api/command",j)==409&&rows_latch(&r,"reset_pending","reset"));
    j=rows_body(&r,"verify-reset"); cJSON_AddBoolToObject(j,"reset_completed",true);
    CHECK(rows_post(&r,"/api/command",j)==200&&rows_latch(&r,"clear",""));
    /* Nothing about the attestation is recorded for audit. */
    CHECK(!rows_saved_has(&r,"attestation")&&!rows_saved_has(&r,"confirmation_id"));
    rows_close(&r);
}
static void c5_restart_preserves_state(void)
{
    rows_open(&r,true);
    rows_restart(&r); CHECK(rows_latch(&r,"clear","")&&!rows_reports(&r,"recovery_required"));
    /* An intent found at startup means the action was interrupted. */
    CHECK(rows_joint(&r,1,5)==202); LANE(&r.c)->op=OP_IDLE;
    rows_restart(&r);
    CHECK(rows_latch(&r,"latched","intent")&&rows_reports(&r,"recovery_required")&&rows_idle(&r));
    CHECK(rows_joint(&r,1,5)==409&&strstr(r.message,"Stopped earlier"));
    CHECK(rows_stop(&r)==200);
    rows_restart(&r);
    CHECK(rows_latch(&r,"latched","stop")&&incident()&&rows_reports(&r,"recovery_required"));
    rows_close(&r);
    reset_pending_arm();
    rows_restart(&r);
    CHECK(rows_latch(&r,"reset_pending","reset")&&rows_reports(&r,"reset_pending"));
    rows_close(&r);
}

static const struct check_test tests[]={
    {"CC.L1","an admitted action is saved as an intent before its first frame",l1_admission_persists_intent,NULL},
    {"CC.L2","disable from clear is saved as a power latch before its first frame",l2_disable_persists_power,
        "the target record has one INTENT state with kind=disable; today it is latched with kind=power"},
    {"CC.L3","verified completion clears the intent",l3_completion_clears_intent,NULL},
    {"CC.L4","relinquishing to onboard teaching clears only the lane's own intent",l4_relinquish_clears_own_intent,NULL},
    {"CC.L5","a disable that started clear ends clear",l5_disable_completion_clears,NULL},
    {"CC.L6","any STOP creates the incident and its stop event",l6_stop_creates_incident,NULL},
    {"CC.L7","a later STOP keeps the first stop event; a failed disable adds a cleanup error",l7_incident_keeps_first_stop,NULL},
    {"CC.L8","a pending reset survives STOP and keeps the incident",l8_reset_pending_survives_stop,NULL},
    {"CC.L9","reset is saved as pending before the reset frame",l9_reset_persists_before_frame,NULL},
    {"CC.L10","a reset the core observed completing clears the record",l10_observed_reset_clears,NULL},
    {"CC.L11","verify-reset clears on all-off evidence plus the client's reset_completed flag",l11_verify_reset_needs_client_flag,
        "B1: the reset_completed flag goes; samples newer than the reset intent replace it"},
    {"CC.L12","verify-disabled clears a latch with all motors off, never a pending reset",l12_verify_disabled_clears,NULL},
    {"CC.L13","verify-current clears a latch at a consistent power state, never a pending reset",l13_verify_current_clears,NULL},
    {"CC.L15","a failed write sends nothing and blocks every admission until restart",l15_failed_write_blocks,
        "the target keeps the pre-transition record in memory; today memory holds the unsaved latch"},
    {"CC.L16","an unacknowledged legacy stop blocks its arm",l16_legacy_stop_blocks,
        "Q9: the entry becomes INCIDENT(kind=legacy) once on load; today it is consulted on every read"},
    {"CC.C1","the saved booleans, state, kind and stop event never disagree",c1_record_is_representable,NULL},
    {"CC.C2","every power, mode, target and reset frame leaves after its record is saved",c2_persisted_before_frames,NULL},
    {"CC.C3","relinquish eligibility follows the saved kind, not the reason text",c3_eligibility_ignores_reason,NULL},
    {"CC.C4","a client field alone moves the record",c4_client_fields_gate_transitions,
        "B1: no client field may move the state, and attestations are recorded for audit"},
    {"CC.C5","a restart preserves clear, intent, incident and pending reset",c5_restart_preserves_state,NULL},
};
int main(void) { return check_main("rows-latch",tests,sizeof tests/sizeof *tests); }

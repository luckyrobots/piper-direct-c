#define _GNU_SOURCE
/* Characterization of the lane operation as the core implements it today:
   the rows of the C-A tables in docs/target-architecture.md section 3.1 that
   one simulated arm can show (global events, admission, power, a plain move)
   and the C-A invariants. The other rows are characterized by the suites of
   their feature; the "Row coverage" table of the document names them. */
#include "rows.h"

static struct rows r;

/* A move that takes several seconds, already past its seed. */
static void moving(void)
{
    rows_open(&r,true);
    CHECK(rows_joint(&r,1,40)==202);
    rows_run(&r,.3); CHECK(rows_op(&r)==OP_MOVE&&rows_latch(&r,"latched","intent"));
}
/* The lane stopped: idle, STOP sent once since `mark`, the incident saved with this reason. */
static void stopped(size_t mark,const char *reason)
{
    CHECK(rows_idle(&r)&&rows_phase(&r,"stopped"));
    CHECK(rows_sent(mark,ROWS_STOP_FRAME)==1);
    CHECK(rows_latch(&r,"latched","stop")&&rows_saved_has(&r,"stop_event_active"));
    if (!strstr(rows_stop_reason(&r),reason)) fprintf(stderr,"stop reason: %s\n",rows_stop_reason(&r));
    CHECK(strstr(rows_stop_reason(&r),reason));
}
static int cancel(double request)
{
    cJSON *j=cJSON_CreateObject(); cJSON_AddStringToObject(j,"device_id",r.d->id);
    cJSON_AddStringToObject(j,"owner",ROWS_OWNER); cJSON_AddNumberToObject(j,"request_id",request);
    return rows_post(&r,"/api/stop",j);
}

static void g1_stop(void)
{
    /* Emergency: stops, whoever asks. */
    moving(); size_t mark=fake_bus_mark();
    cJSON *j=cJSON_CreateObject(); cJSON_AddStringToObject(j,"device_id",r.d->id);
    cJSON_AddStringToObject(j,"owner","somebody-else-entirely");
    CHECK(rows_post(&r,"/api/stop",j)==200); stopped(mark,"Operator stop");
    /* An idle arm is stopped as well. */
    rows_run(&r,.2); mark=fake_bus_mark();
    CHECK(rows_stop(&r)==200&&rows_sent(mark,ROWS_STOP_FRAME)==1);
    rows_close(&r);
    /* Cancel: only the caller's own current action. */
    moving(); mark=fake_bus_mark();
    CHECK(cancel(r.request+1)==409&&rows_op(&r)==OP_MOVE&&!rows_sent(mark,ROWS_STOP_FRAME));
    CHECK(rows_latch(&r,"latched","intent"));
    CHECK(cancel(r.request)==200); stopped(mark,"");
    mark=fake_bus_mark();
    CHECK(cancel(r.request)==409&&fake_bus_mark()==mark);
    rows_close(&r);
}
static void g2_lease_expiry(void)
{
    moving(); size_t mark=fake_bus_mark();
    r.silent=true; rows_run(&r,.7); CHECK(rows_op(&r)==OP_MOVE);
    rows_run(&r,.2); stopped(mark,"Control heartbeat expired.");
    rows_close(&r);
    /* A heartbeat that arrives too late stops as well, and says so. */
    moving(); mark=fake_bus_mark();
    fake_bus_advance(.9);
    cJSON *j=rows_body(&r,"heartbeat"); cJSON_AddNumberToObject(j,"request_id",r.request);
    CHECK(rows_post(&r,"/api/heartbeat",j)==409); stopped(mark,"Control heartbeat expired.");
    rows_close(&r);
}
static void g4_generation_change(void)
{
    moving(); size_t mark=fake_bus_mark();
    unsigned pinned=r.d->generation; ++r.d->generation;
    rows_run(&r,.05);
    CHECK(rows_idle(&r)&&rows_phase(&r,"stopped")&&strstr(rows_stop_reason(&r),"Adapter connection changed."));
    /* The STOP carries the pinned generation, which the bus no longer accepts. */
    size_t cursor=mark; const struct fake_frame *stop=fake_bus_next(&cursor,0,0x150);
    CHECK(stop&&stop->frame.data[0]==1&&!stop->accepted&&!LANE(&r.c)->stop_delivered&&pinned!=r.d->generation);
    CHECK(rows_latch(&r,"latched","stop"));
    rows_close(&r);
}
static bool deaf(struct can_bus *bus,struct can_device *device,unsigned generation,const struct can_frame *frames,size_t count)
{
    (void)bus; (void)device; (void)generation; (void)frames; (void)count;
    return true;
}
static void g6_deadline(void)
{
    /* An MCU that accepts every frame and acts on none. */
    rows_open(&r,false);
    fake_bus_model(fake_bus_simulator_receive,deaf);
    CHECK(rows_command(&r,"enable")==202);
    double admitted=fake_bus.now; size_t mark=fake_bus_mark();
    rows_run(&r,7.9); CHECK(rows_op(&r)==OP_ENABLE);
    rows_run(&r,.2); CHECK(fake_bus.now-admitted<8.2);
    fake_bus_model(NULL,NULL); stopped(mark,"Enable timed out; motors still disabled: J1 J2 J3 J4 J5 J6");
    rows_close(&r);
    /* A reset that is never acknowledged ends after 3 s and stays pending. */
    rows_open(&r,false);
    CHECK(rows_stop(&r)==200); rows_run(&r,.6);
    fake_bus_model(fake_bus_simulator_receive,deaf);
    CHECK(rows_command(&r,"reset")==202);
    rows_run(&r,2.9); CHECK(rows_op(&r)==OP_RESET);
    rows_run(&r,.2);
    CHECK(rows_idle(&r)&&rows_phase(&r,"stopped")&&strstr(LANE(&r.c)->message,"Control action timed out."));
    /* The incident keeps the reason of its first STOP. */
    CHECK(strstr(rows_stop_reason(&r),"Operator stop"));
    CHECK(rows_latch(&r,"reset_pending","reset")&&rows_reports(&r,"reset_pending"));
    fake_bus_model(NULL,NULL);
    rows_close(&r);
}
static void driver_fault(struct can_bus *bus,double now) { (void)now; bus->devices[0].feedback.motor[3].fault=1; }
static void g7_health_failure(void)
{
    moving(); size_t mark=fake_bus_mark();
    fake_bus.after_receive=driver_fault; rows_run(&r,.05);
    stopped(mark,"A motor driver reports a fault.");
    rows_close(&r);
}
static void teaching(struct can_bus *bus,double now)
{
    (void)now; struct piper_sample *s=&bus->devices[0].feedback.arm[0];
    s->teach=PIPER_TEACH_RECORDING; s->arm_status=PIPER_ARM_STATUS_TEACHING;
}
/* The firmware quirk: the teach flag stays set after the button is off. */
static void residual_teach_flag(struct can_bus *bus,double now) { (void)now; bus->devices[0].feedback.arm[0].teach=PIPER_TEACH_RECORDING; }
static void g8_relinquish(void)
{
    moving(); size_t mark=fake_bus_mark();
    fake_bus.after_receive=teaching; rows_run(&r,.05);
    CHECK(rows_idle(&r)&&rows_phase(&r,"teaching")&&!*LANE(&r.c)->owner);
    CHECK(!rows_sent(mark,ROWS_STOP_FRAME)&&rows_latch(&r,"clear",""));
    rows_close(&r);
    /* A reset is not given up for a residual teach flag, only for a real teaching status. */
    rows_open(&r,false);
    CHECK(rows_stop(&r)==200); fake_bus_model(fake_bus_simulator_receive,deaf);
    fake_bus.after_receive=residual_teach_flag; rows_run(&r,.6);
    CHECK(rows_command(&r,"reset")==202);
    rows_run(&r,.2); CHECK(rows_op(&r)==OP_RESET);
    fake_bus.after_receive=teaching; rows_run(&r,.05);
    CHECK(rows_idle(&r)&&rows_phase(&r,"teaching")&&rows_latch(&r,"reset_pending","reset"));
    fake_bus_model(NULL,NULL);
    rows_close(&r);
}
/* An MCU that stays in standby: it never enters CAN MoveJ mode. */
static void standby(struct can_bus *bus,double now)
{
    (void)now; bus->devices[0].feedback.arm[0].control=0; bus->devices[0].feedback.arm[0].motion_mode=0;
}
static void g9_enable_lost(void)
{
    moving(); size_t mark=fake_bus_mark();
    r.c.bus.sim_enabled=0; rows_run(&r,.1);
    stopped(mark,"Motor enable changed during motion.");
    rows_close(&r);
    rows_open(&r,true);
    fake_bus.after_receive=standby; rows_run(&r,.1);
    CHECK(rows_joint(&r,1,40)==202);
    mark=fake_bus_mark();
    rows_run(&r,.4); CHECK(rows_op(&r)==OP_MOVE);
    rows_run(&r,.3); stopped(mark,"Controller did not enter CAN MoveJ mode.");
    rows_close(&r);
}
static void g12_close(void)
{
    moving(); size_t mark=fake_bus_mark();
    rows_restart(&r);
    CHECK(rows_sent(mark,ROWS_STOP_FRAME)==1);
    CHECK(rows_latch(&r,"latched","stop")&&strstr(rows_stop_reason(&r),"Controller shutting down."));
    CHECK(rows_reports(&r,"recovery_required"));
    rows_close(&r);
}
static void g13_storage_failure(void)
{
    /* The write that fails is the one that would clear the completed action. */
    rows_open(&r,false);
    CHECK(rows_command(&r,"enable")==202);
    rows_storage(&r,false); rows_finish(&r,8); rows_storage(&r,true);
    CHECK(rows_phase(&r,"blocked")&&r.c.storage_failed&&r.c.bus.sim_enabled==63);
    CHECK(rows_latch(&r,"latched","intent"));
    size_t mark=fake_bus_mark();
    CHECK(rows_joint(&r,1,5)==409&&rows_command(&r,"disable")==409&&rows_command(&r,"verify-current")==409);
    CHECK(fake_bus_mark()==mark&&rows_idle(&r));
    /* STOP still works. */
    CHECK(rows_stop(&r)==200&&rows_sent(mark,ROWS_STOP_FRAME)==1);
    rows_close(&r);
}

static void a1_enable(void)
{
    rows_open(&r,false);
    size_t mark=fake_bus_mark();
    cJSON *j=rows_body(&r,"enable"); cJSON_ReplaceItemInObject(j,"owner",cJSON_CreateString("short-owner"));
    CHECK(rows_post(&r,"/api/command",j)==400);
    j=rows_body(&r,"enable"); cJSON_DeleteItemFromObject(j,"standalone");
    CHECK(rows_post(&r,"/api/command",j)==400);
    j=rows_body(&r,"enable"); cJSON_DeleteItemFromObject(j,"supported");
    CHECK(rows_post(&r,"/api/command",j)>=400);
    j=rows_body(&r,"enable"); cJSON_DeleteItemFromObject(j,"clear");
    CHECK(rows_post(&r,"/api/command",j)>=400);
    CHECK(rows_idle(&r)&&fake_bus_mark()==mark&&rows_latch(&r,"clear",""));
    CHECK(rows_command(&r,"enable")==202&&rows_op(&r)==OP_ENABLE&&rows_phase(&r,"enabling"));
    CHECK(!strcmp(LANE(&r.c)->owner,ROWS_OWNER)&&LANE(&r.c)->request_id==r.request);
    CHECK(rows_latch(&r,"latched","intent"));
    rows_stop(&r); rows_close(&r);
}
static void a2_move(void)
{
    rows_open(&r,false);
    size_t mark=fake_bus_mark();
    CHECK(rows_joint(&r,1,10)==409&&rows_idle(&r)&&fake_bus_mark()==mark&&rows_latch(&r,"clear",""));
    r.c.bus.sim_enabled=63; rows_run(&r,.6);
    CHECK(rows_joint(&r,1,200)==400&&rows_idle(&r)&&fake_bus_mark()==mark);
    CHECK(rows_joint(&r,1,10)==202&&rows_op(&r)==OP_MOVE&&LANE(&r.c)->pose_phase==POSE_SEEDING);
    CHECK(rows_latch(&r,"latched","intent"));
    rows_stop(&r); rows_close(&r);
}
static void a8_disable(void)
{
    rows_open(&r,true);
    size_t mark=fake_bus_mark();
    cJSON *j=rows_body(&r,"disable"); cJSON_DeleteItemFromObject(j,"supported");
    CHECK(rows_post(&r,"/api/command",j)==400&&rows_idle(&r)&&fake_bus_mark()==mark);
    CHECK(rows_command(&r,"disable")==202&&rows_op(&r)==OP_DISABLE&&!LANE(&r.c)->disable_keeps_recovery);
    CHECK(rows_latch(&r,"latched","power"));
    rows_stop(&r); rows_run(&r,.6);
    /* With a latch already held, the disable keeps it: kind and reason are the incident's. */
    CHECK(rows_command(&r,"disable")==202&&LANE(&r.c)->disable_keeps_recovery);
    CHECK(rows_latch(&r,"latched","stop")&&strstr(rows_saved_text(&r,"reason"),"Operator stop"));
    rows_stop(&r); rows_close(&r);
}
static void a9_reset(void)
{
    rows_open(&r,true);
    size_t mark=fake_bus_mark();
    cJSON *j=rows_body(&r,"reset"); cJSON_DeleteItemFromObject(j,"supported");
    CHECK(rows_post(&r,"/api/command",j)==400);
    j=rows_body(&r,"reset"); cJSON_DeleteItemFromObject(j,"clear");
    CHECK(rows_post(&r,"/api/command",j)==400&&fake_bus_mark()==mark&&rows_latch(&r,"clear",""));
    CHECK(rows_command(&r,"reset")==202&&rows_op(&r)==OP_RESET&&rows_phase(&r,"resetting"));
    CHECK(rows_sent(mark,ROWS_RESET_FRAME)==1&&rows_latch(&r,"reset_pending","reset"));
    rows_stop(&r); rows_close(&r);
    /* The reset frame cannot be sent: the lane ends blocked, the reset stays pending. */
    rows_open(&r,false);
    fake_bus_fail(0,0x150,1);
    CHECK(rows_command(&r,"reset")==409&&rows_idle(&r)&&rows_phase(&r,"blocked"));
    CHECK(rows_latch(&r,"reset_pending","reset")&&!rows_saved_has(&r,"stop_event_active"));
    rows_close(&r);
    /* Firmware quirk: a teach flag left set is accepted only on a stopped, all-off arm. */
    rows_open(&r,false);
    fake_bus.after_receive=residual_teach_flag; rows_run(&r,.6);
    mark=fake_bus_mark();
    CHECK(rows_command(&r,"reset")==409&&fake_bus_mark()==mark&&rows_latch(&r,"clear",""));
    CHECK(rows_stop(&r)==200); rows_run(&r,.6);
    CHECK(r.d->feedback.arm[0].arm_status==PIPER_ARM_STATUS_STOPPED&&r.d->feedback.arm[0].teach==PIPER_TEACH_RECORDING);
    CHECK(rows_command(&r,"reset")==202&&rows_op(&r)==OP_RESET);
    rows_stop(&r); rows_close(&r);
}
static void a13_verify_stays_idle(void)
{
    const char *actions[]={"verify-current","verify-disabled","verify-reset"};
    for (unsigned i=0;i<3;++i) {
        rows_open(&r,false);
        size_t mark=fake_bus_mark(); uint64_t tx=r.d->tx;
        cJSON *j=rows_body(&r,actions[i]); cJSON_AddBoolToObject(j,"reset_completed",true);
        CHECK(rows_post(&r,"/api/command",j)==200);
        CHECK(rows_idle(&r)&&!*LANE(&r.c)->owner&&fake_bus_mark()==mark&&r.d->tx==tx&&rows_latch(&r,"clear",""));
        rows_close(&r);
    }
}

static void m1_move_completes(void)
{
    rows_open(&r,true);
    CHECK(rows_joint(&r,1,5)==202);
    rows_finish(&r,20);
    CHECK(rows_phase(&r,"complete")&&fabs(r.d->feedback.arm[0].deg[0]-5)<=LANE(&r.c)->tolerance+1e-9);
    CHECK(!r.d->feedback.arm[0].motion_status&&r.c.bus.sim_enabled==63);
    CHECK(rows_latch(&r,"clear","")&&!rows_reports(&r,"recovery_required"));
    rows_close(&r);
}
static void p1_enable_completes(void)
{
    rows_open(&r,false);
    size_t mark=fake_bus_mark();
    CHECK(rows_command(&r,"enable")==202);
    double admitted=fake_bus.now; rows_finish(&r,8);
    CHECK(rows_phase(&r,"complete")&&r.c.bus.sim_enabled==63&&fake_bus.now-admitted>=.4);
    CHECK(rows_latch(&r,"clear",""));
    /* Power frames only: no mode, no position target, no STOP, no reset. */
    CHECK(rows_sent(mark,0x471,-1)>=6&&fake_bus_sent(mark,0,0)==rows_sent(mark,0x471,-1));
    rows_close(&r);
}
static void paced(size_t mark)
{
    unsigned drivers=0; double last=0;
    for (const struct fake_frame *f;(f=fake_bus_next(&mark,0,0x471));last=f->at) {
        CHECK(f->frame.data[0]>=1&&f->frame.data[0]<=6);
        drivers|=1u<<(f->frame.data[0]-1);
        CHECK(!last||f->at-last>=.02-1e-9);
    }
    CHECK(drivers==63);
}
static void p3_power_pacing(void)
{
    rows_open(&r,false);
    size_t mark=fake_bus_mark();
    CHECK(rows_command(&r,"enable")==202); rows_finish(&r,8); paced(mark);
    mark=fake_bus_mark();
    CHECK(rows_command(&r,"disable")==202); rows_finish(&r,8); paced(mark);
    rows_close(&r);
}
static void p4_disable_completes(void)
{
    rows_open(&r,true);
    size_t mark=fake_bus_mark();
    CHECK(rows_command(&r,"disable")==202); rows_finish(&r,8);
    CHECK(rows_phase(&r,"complete")&&r.c.bus.sim_enabled==0&&rows_latch(&r,"clear",""));
    CHECK(fake_bus_sent(mark,0,0)==rows_sent(mark,0x471,-1));
    rows_close(&r);
    /* keeps_recovery: the latch it started with is still there. */
    rows_open(&r,true);
    CHECK(rows_joint(&r,1,5)==202); LANE(&r.c)->op=OP_IDLE; rows_restart(&r);
    CHECK(rows_latch(&r,"latched","intent"));
    CHECK(rows_command(&r,"disable")==202&&LANE(&r.c)->disable_keeps_recovery); rows_finish(&r,8);
    CHECK(rows_phase(&r,"complete")&&r.c.bus.sim_enabled==0);
    CHECK(rows_latch(&r,"latched","power")&&rows_reports(&r,"recovery_required"));
    rows_close(&r);
}
static void p5_disable_tx_failure(void)
{
    rows_open(&r,true);
    CHECK(rows_command(&r,"disable")==202);
    size_t mark=fake_bus_mark();
    fake_bus_fail(0,0x471,1); rows_run(&r,.3);
    CHECK(rows_idle(&r)&&rows_phase(&r,"stopped")&&rows_sent(mark,ROWS_STOP_FRAME)==1);
    CHECK(rows_saved_has(&r,"stop_event_active")&&rows_saved_has(&r,"last_cleanup_error"));
    CHECK(!strcmp(rows_saved_text(&r,"last_cleanup_error"),rows_stop_reason(&r)));
    cJSON *record=rows_saved(&r);
    const cJSON *transport=cJSON_GetObjectItem(cJSON_GetObjectItem(record,"stop_event"),"transport");
    CHECK(cJSON_GetObjectItem(transport,"failed_can_id")&&cJSON_GetObjectItem(transport,"failed_can_id")->valuedouble==0x471);
    cJSON_Delete(record);
    rows_close(&r);
}
static void r1_reset_completes(void)
{
    rows_open(&r,true);
    size_t mark=fake_bus_mark();
    CHECK(rows_command(&r,"reset")==202);
    double admitted=fake_bus.now; rows_finish(&r,3);
    CHECK(rows_phase(&r,"complete")&&r.c.bus.sim_enabled==0&&fake_bus.now-admitted>=.4);
    CHECK(!r.d->feedback.arm[0].arm_status&&!r.d->feedback.arm[0].error);
    CHECK(rows_latch(&r,"clear","")&&fake_bus_sent(mark,0,0)==1);
    rows_close(&r);
}

static void retired(void)
{
    const struct controller_lane *l=LANE(&r.c);
    CHECK(l->op==OP_IDLE&&!*l->owner&&l->follow==FOLLOW_NONE&&l->stage==0&&l->pose_phase==0);
    cJSON *j=rows_body(&r,"heartbeat"); cJSON_AddNumberToObject(j,"request_id",r.request);
    CHECK(rows_post(&r,"/api/heartbeat",j)==409);
    CHECK(cancel(r.request)==409);
}
static void i1_idle_retires_the_request(void)
{
    rows_open(&r,true);
    CHECK(rows_joint(&r,1,3)==202); rows_finish(&r,20); CHECK(rows_phase(&r,"complete")); retired();
    rows_run(&r,.6); CHECK(rows_joint(&r,1,40)==202); rows_run(&r,.3); CHECK(rows_stop(&r)==200); retired();
    rows_close(&r);
    moving(); r.silent=true; rows_run(&r,1); retired(); rows_close(&r);
    moving(); fake_bus.after_receive=teaching; rows_run(&r,.05); retired(); rows_close(&r);
}
static void i5_state_is_read_only(void)
{
    moving();
    struct controller_lane before=*LANE(&r.c); size_t mark=fake_bus_mark();
    char *saved=cJSON_PrintUnformatted(r.c.saved);
    for (unsigned i=0;i<3;++i) cJSON_Delete(controller_state(&r.c,"rows"));
    (void)controller_any_motion_active(&r.c);
    char *after=cJSON_PrintUnformatted(r.c.saved);
    CHECK(!memcmp(&before,LANE(&r.c),sizeof before)&&!strcmp(saved,after)&&fake_bus_mark()==mark);
    free(saved); free(after);
    rows_stop(&r); rows_close(&r);
}
static void i6_stop_event_is_the_lane_snapshot(void)
{
    moving(); double command=LANE(&r.c)->command[0];
    CHECK(rows_stop(&r)==200);
    cJSON *record=rows_saved(&r); const cJSON *event=cJSON_GetObjectItem(record,"stop_event");
    CHECK(cJSON_GetObjectItem(event,"request_id")->valuedouble==r.request);
    CHECK(!strcmp(cJSON_GetObjectItem(event,"phase_before_stop")->valuestring,"moving"));
    CHECK(cJSON_IsFalse(cJSON_GetObjectItem(event,"following"))&&!cJSON_GetObjectItem(event,"follow_input_age_s"));
    const cJSON *joint=cJSON_GetArrayItem(cJSON_GetObjectItem(event,"joints"),0);
    CHECK(cJSON_GetObjectItem(joint,"target_deg")->valuedouble==40&&cJSON_GetObjectItem(joint,"command_deg")->valuedouble==command);
    cJSON_Delete(record);
    /* A STOP on the now idle lane records no stale action. */
    rows_run(&r,.6); CHECK(rows_command(&r,"reset")==202); rows_finish(&r,3);
    CHECK(rows_stop(&r)==200);
    record=rows_saved(&r); event=cJSON_GetObjectItem(record,"stop_event");
    CHECK(cJSON_IsFalse(cJSON_GetObjectItem(event,"following"))&&!cJSON_GetObjectItem(event,"follow_input_age_s"));
    CHECK(cJSON_IsNull(cJSON_GetObjectItem(cJSON_GetArrayItem(cJSON_GetObjectItem(event,"joints"),0),"target_deg")));
    cJSON_Delete(record);
    rows_close(&r);
}
static void i9_other_requests_change_nothing(void)
{
    moving(); size_t mark;
    const char *actions[]={"enable","joint","disable","reset","verify-current","verify-reset","verify-disabled","follow-update","follow-commit","follow-end","no-such-action"};
    for (unsigned i=0;i<sizeof actions/sizeof *actions;++i) {
        struct controller_lane before=*LANE(&r.c); mark=fake_bus_mark();
        cJSON *j=rows_body(&r,actions[i]); cJSON_AddNumberToObject(j,"request_id",r.request);
        cJSON_AddNumberToObject(j,"joint",2); cJSON_AddNumberToObject(j,"degrees",1); cJSON_AddBoolToObject(j,"reset_completed",true);
        if (rows_post(&r,"/api/command",j)<400) fprintf(stderr,"accepted while moving: %s\n",actions[i]);
        CHECK(r.status>=400&&fake_bus_mark()==mark&&rows_latch(&r,"latched","intent"));
        struct controller_lane *now=LANE(&r.c);
        CHECK(now->op==before.op&&now->request_id==before.request_id&&!strcmp(now->owner,before.owner)&&
            !memcmp(now->target,before.target,sizeof before.target)&&now->started==before.started);
    }
    rows_stop(&r); rows_close(&r);
}

static void busy(struct can_bus *bus,double now) { (void)now; bus->devices[0].feedback.arm[0].motion_status=1; }
static void y2_pause_restarts(void)
{
    rows_open(&r,true);
    CHECK(rows_command(&r,"cycle")==202&&rows_op(&r)==OP_CYCLE);
    for (double end=fake_bus.now+170;LANE(&r.c)->stage!=STAGE_CYCLE_UPRIGHT&&fake_bus.now<end;) rows_run(&r,ROWS_TICK);
    CHECK(LANE(&r.c)->stage==STAGE_CYCLE_UPRIGHT);
    double paused=fake_bus.now;
    /* One second into the pause the arm is not settled for half a second. */
    rows_run(&r,1); fake_bus.after_receive=busy; rows_run(&r,.5); fake_bus.after_receive=NULL;
    double settled=fake_bus.now;
    CHECK(LANE(&r.c)->stage==STAGE_CYCLE_UPRIGHT);
    for (double end=fake_bus.now+5;LANE(&r.c)->stage==STAGE_CYCLE_UPRIGHT&&fake_bus.now<end;) rows_run(&r,ROWS_TICK);
    CHECK(LANE(&r.c)->stage==STAGE_CYCLE_RETURNING);
    CHECK(fake_bus.now-settled>=2-ROWS_TICK&&fake_bus.now-paused>=3.5-ROWS_TICK);
    rows_stop(&r); rows_close(&r);
}
static void l3_range_entry_not_verified(void)
{
    /* J2 is two degrees below its range; the move inward is staged behind a range entry. */
    rows_open(&r,true);
    r.d->feedback.arm[0].deg[1]=r.c.bus.sim_target[1]=-2; r.d->feedback.limit[1].min=0; rows_run(&r,.7);
    CHECK(rows_joint(&r,2,5)==202&&rows_op(&r)==OP_ALIGN&&LANE(&r.c)->stage==STAGE_ALIGN_FOLLOWUP);
    size_t mark=fake_bus_mark();
    /* While it runs, the pending destination stops being a legal target. */
    r.d->feedback.limit[1].max=4;
    rows_finish(&r,30);
    stopped(mark,"Range entry did not verify a legal pose for the pending destination");
    CHECK(fabs(r.d->feedback.arm[0].deg[1])<4);
    rows_close(&r);
}
static void left_teaching(struct can_bus *bus,double now) { (void)now; bus->devices[0].feedback.arm[0].control=0; }
static void t5_seed_loses_teaching_mode(void)
{
    rows_open(&r,true);
    strcpy(r.d->feedback.version,"S-V1.9-0");
    r.d->feedback.arm[0].control=2; r.d->feedback.arm[0].teach=PIPER_TEACH_RECORDING+1; rows_run(&r,.6);
    cJSON *j=rows_body(&r,"teach-return"); cJSON_AddBoolToObject(j,"button_off",true); cJSON_AddBoolToObject(j,"same_pose_confirmed",true);
    CHECK(rows_post(&r,"/api/command",j)==202&&rows_op(&r)==OP_TEACH_RETURN&&LANE(&r.c)->stage==STAGE_TEACH_SEEDED);
    size_t mark=fake_bus_mark();
    fake_bus.after_receive=left_teaching; rows_run(&r,.05);
    CHECK(rows_idle(&r)&&strstr(LANE(&r.c)->message,"Controller left completed teaching before the CAN request."));
    CHECK(fake_bus_mark()==mark&&rows_latch(&r,"clear","")&&r.c.bus.sim_enabled==63);
    rows_close(&r);
}

static const struct check_test tests[]={
    {"CA.G1","STOP: an emergency stops whoever asks; a cancel stops only its own current action",g1_stop,NULL},
    {"CA.G2","a lease older than 0.8 s stops the lane, on a tick or on a late heartbeat",g2_lease_expiry,NULL},
    {"CA.G4","a changed adapter generation stops the lane",g4_generation_change,NULL},
    {"CA.G6","the 8 s power deadline and the 3 s reset deadline stop the lane; the reset stays pending",g6_deadline,NULL},
    {"CA.G7","a failed health check stops the lane",g7_health_failure,NULL},
    {"CA.G8","benign onboard teaching takes the lane without a STOP; a reset needs a real teaching status",g8_relinquish,NULL},
    {"CA.G9","losing motor enable, or an MCU that does not enter CAN MoveJ mode within 0.5 s, stops the lane",g9_enable_lost,NULL},
    {"CA.G12","closing the controller stops a running action and saves the incident",g12_close,NULL},
    {"CA.G13","a failed write latches storage_failed and refuses every later admission",g13_storage_failure,NULL},
    {"CA.A1","enable is admitted with owner and attestations, as a saved intent",a1_enable,NULL},
    {"CA.A2","a joint move needs all six motors on and a target in range, and starts by seeding",a2_move,NULL},
    {"CA.A8","disable needs the supported attestation and keeps a latch it did not create",a8_disable,NULL},
    {"CA.A9","reset needs both attestations; a failed frame leaves it pending; residual teach flag only when stopped",a9_reset,NULL},
    {"CA.A13","the verify actions never leave idle and send nothing",a13_verify_stays_idle,NULL},
    {"CA.L3","a range entry that ends with an illegal pending destination stops and discards it",l3_range_entry_not_verified,NULL},
    {"CA.Y2","an unsettled arm restarts the two-second upright pause",y2_pause_restarts,NULL},
    {"CA.T5","a seamless handoff is abandoned without a STOP when the MCU leaves teaching mode before the CAN request",t5_seed_loses_teaching_mode,NULL},
    {"CA.M1","a settled move completes and clears its intent",m1_move_completes,NULL},
    {"CA.P1","enable completes on six acknowledged drivers after settling, with power frames only",p1_enable_completes,NULL},
    {"CA.P3","power frames go one driver at a time, at least 20 ms apart",p3_power_pacing,NULL},
    {"CA.P4","disable completes all-off; it clears only a latch it created",p4_disable_completes,NULL},
    {"CA.P5","a failed disable frame stops the lane and records the cleanup error",p5_disable_tx_failure,NULL},
    {"CA.R1","reset completes on all-off, error-free, stationary feedback and clears the pending reset",r1_reset_completes,NULL},
    {"CA.I1","an idle lane has no owner and no request, however it got there",i1_idle_retires_the_request,NULL},
    {"CA.I5","reading the state changes neither the lane, the record nor the bus",i5_state_is_read_only,NULL},
    {"CA.I6","the stop event records the action that was running, and none on an idle lane",i6_stop_event_is_the_lane_snapshot,NULL},
    {"CA.I9","any other request during a move is refused and changes nothing",i9_other_requests_change_nothing,NULL},
};
int main(void) { return check_main("rows-lane",tests,sizeof tests/sizeof *tests); }

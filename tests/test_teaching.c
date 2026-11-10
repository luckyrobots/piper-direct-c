#define _GNU_SOURCE
#include "fixture.h"
#include "fake_bus.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static void setup(struct controller *c,char *directory)
{
    CHECK(controller_init(c,true,directory,""));
    struct can_device *d=&c->bus.devices[0];
    memset(c->bus.sim_target,0,sizeof c->bus.sim_target);
    memset(d->feedback.arm[0].deg,0,sizeof d->feedback.arm[0].deg);
    c->bus.sim_enabled=63;
    double now=piper_now(); controller_tick(c,now);
    fixture_configure(d,"S-V1.8-2",-180,180,.3,now);
    d->feedback.arm[0].control=2; d->feedback.arm[0].teach=2;
    fixture_still(d,now-1);
}
static cJSON *request(struct controller *c,const char *action)
{
    struct can_device *d=&c->bus.devices[0]; cJSON *j=cJSON_CreateObject();
    cJSON_AddStringToObject(j,"action",action); cJSON_AddStringToObject(j,"device_id",d->id);
    cJSON_AddNumberToObject(j,"generation",d->generation); cJSON_AddStringToObject(j,"owner","teaching-regression-client");
    cJSON_AddBoolToObject(j,"standalone",true); cJSON_AddBoolToObject(j,"clear",true);
    cJSON_AddBoolToObject(j,"button_off",true); cJSON_AddBoolToObject(j,"zero_pose_confirmed",true); cJSON_AddBoolToObject(j,"supported",true);
    return j;
}
static void action(struct controller *c,cJSON *body,int expected)
{
    int status; cJSON *result=controller_action(c,"/api/command",body,&status);
    if(status!=expected) { char *s=cJSON_PrintUnformatted(result); fprintf(stderr,"expected %d got %d: %s\n",expected,status,s); free(s); }
    CHECK(status==expected); cJSON_Delete(body); cJSON_Delete(result);
}
static cJSON *snapshot(struct controller *c)
{
    cJSON *state=controller_state(c,"test"),*device=cJSON_GetArrayItem(cJSON_GetObjectItem(state,"devices"),0);
    cJSON *copy=cJSON_Duplicate(device,true); cJSON_Delete(state); return copy;
}
static bool can_return(struct controller *c)
{
    cJSON *device=snapshot(c);
    bool result=cJSON_IsTrue(cJSON_GetObjectItem(cJSON_GetObjectItem(device,"teaching"),"can_return"));
    cJSON_Delete(device); return result;
}
static void check_phase(struct controller *c,const char *phase,bool active)
{
    cJSON *device=snapshot(c),*teaching=cJSON_GetObjectItem(device,"teaching");
    CHECK(!strcmp(cJSON_GetObjectItem(teaching,"phase")->valuestring,phase));
    CHECK(cJSON_IsTrue(cJSON_GetObjectItem(teaching,"active"))==active);
    cJSON_Delete(device);
}
static void enter_own_move(struct controller *c)
{
    struct can_device *d=&c->bus.devices[0]; d->feedback.arm[0].control=1; d->feedback.arm[0].teach=0;
    cJSON *j=request(c,"joint"); cJSON_AddNumberToObject(j,"joint",6); cJSON_AddNumberToObject(j,"degrees",25);
    action(c,j,202); CHECK(LANE(c)->op==OP_MOVE);
}
static void scenarios(void)
{
    struct controller c; char first[FIXTURE_DIRECTORY]; fixture_directory(first,sizeof first,"piper-teaching-state"); setup(&c,first);
    struct can_device *d=&c.bus.devices[0];
    CHECK(can_return(&c)); check_phase(&c,"ended",false);
    for (unsigned state=1;state<=7;++state) {
        d->feedback.arm[0].teach=(uint8_t)state;
        d->feedback.arm[0].arm_status=state==1?11:state==4?13:state==3||state==5||state==7?12:0;
        check_phase(&c,state==1?"hand-guiding":state==4?"playback-paused":state==2||state==6?"ended":"playback",state!=2&&state!=6);
        if(state!=2&&state!=6) {
            CHECK(!can_return(&c));
            const char *why=controller_blocker(&c,d,piper_now()); CHECK(why&&strstr(why,"teaching is active"));
        }
    }
    uint64_t presentation_tx=d->tx;
    for (unsigned state=0;state<=7;++state) {
        d->feedback.arm[0].control=0; d->feedback.arm[0].arm_status=1; d->feedback.arm[0].teach=(uint8_t)state;
        check_phase(&c,"stopped",false);
        cJSON *stopped=snapshot(&c),*teaching=cJSON_GetObjectItem(stopped,"teaching");
        CHECK(cJSON_IsTrue(cJSON_GetObjectItem(teaching,"controller_stopped")));
        CHECK(!cJSON_IsTrue(cJSON_GetObjectItem(teaching,"return_required")));
        CHECK(!cJSON_IsTrue(cJSON_GetObjectItem(teaching,"can_return"))&&!cJSON_IsTrue(cJSON_GetObjectItem(teaching,"can_recover_at_pose")));
        CHECK(strstr(cJSON_GetObjectItem(teaching,"label")->valuestring,"STOP"));
        CHECK(cJSON_GetObjectItem(cJSON_GetObjectItem(stopped,"status"),"teach_state")->valueint==(int)state);
        cJSON_Delete(stopped);
    }
    CHECK(d->tx==presentation_tx); /* Presentation must not send a reset, mode or power packet. */
    d->feedback.arm[0].control=2;
    d->feedback.arm[0].teach=2; d->feedback.arm[0].arm_status=0;
    uint64_t tx=d->tx;
    const char *flags[]={"button_off","zero_pose_confirmed","supported","clear"};
    for(unsigned i=0;i<4;++i) { cJSON *j=request(&c,"teach-return"); cJSON_DeleteItemFromObjectCaseSensitive(j,flags[i]); action(&c,j,400); }
    CHECK(d->tx==tx);
    d->feedback.arm[0].deg[5]=1.001; CHECK(!can_return(&c)); action(&c,request(&c,"teach-return"),409); d->feedback.arm[0].deg[5]=0;
    d->feedback.limit[0].min=.2; CHECK(!can_return(&c)); action(&c,request(&c,"teach-return"),409); d->feedback.limit[0].min=-180;
    fixture_still(d,piper_now()); CHECK(!can_return(&c)); action(&c,request(&c,"teach-return"),409); fixture_still(d,piper_now()-1);
    d->feedback.motor[2].fault=1; CHECK(!can_return(&c)); action(&c,request(&c,"teach-return"),409); d->feedback.motor[2].fault=0;
    d->feedback.arm[0].error=1; CHECK(!can_return(&c)); action(&c,request(&c,"teach-return"),409); d->feedback.arm[0].error=0;
    cJSON *old=request(&c,"teach-return"); cJSON_SetNumberValue(cJSON_GetObjectItem(old,"generation"),d->generation+1); action(&c,old,409);
    CHECK(d->tx==tx);
    /* Stale status cannot acknowledge either stage. Exactly two mode packets
       are sent: no position, reset, teaching-start or motor power writes. */
    action(&c,request(&c,"teach-return"),202); CHECK(LANE(&c)->op==OP_TEACH_RETURN&&LANE(&c)->stage==STAGE_TEACH_STANDBY&&d->tx==tx+1&&d->feedback.arm[0].control==0);
    double sent=LANE(&c)->sent_at; controller_tick(&c,sent); CHECK(LANE(&c)->stage==STAGE_TEACH_STANDBY&&d->tx==tx+1);
    fake_bus_advance(0.002); controller_tick(&c,piper_now()); CHECK(LANE(&c)->stage==STAGE_TEACH_CAN&&d->tx==tx+2&&d->feedback.arm[0].control==1);
    sent=LANE(&c)->sent_at; controller_tick(&c,sent); CHECK(LANE(&c)->op==OP_TEACH_RETURN&&d->tx==tx+2);
    fake_bus_advance(0.002); controller_tick(&c,piper_now()); CHECK(LANE(&c)->op==OP_IDLE&&d->tx==tx+2&&c.bus.sim_enabled==63);
    for(unsigned i=0;i<6;++i) CHECK(c.bus.sim_target[i]==0&&d->feedback.arm[0].deg[i]==0);
    action(&c,request(&c,"teach-return"),200); CHECK(d->tx==tx+2); // Already CAN: no replay or repeated writes.
    struct can_frame invalid={.can_id=0x151,.len=8,.data={2,1,1}};
    CHECK(!can_bus_send(&c.bus,d,d->generation,&invalid,1));
    invalid.data[0]=0; invalid.data[2]=2; CHECK(!can_bus_send(&c.bus,d,d->generation,&invalid,1));
    invalid.data[2]=1; invalid.data[3]=1; CHECK(!can_bus_send(&c.bus,d,d->generation,&invalid,1));
    fixture_cleanup(&c,first);

    char takeover[FIXTURE_DIRECTORY]; fixture_directory(takeover,sizeof takeover,"piper-teaching-takeover"); setup(&c,takeover); d=&c.bus.devices[0]; enter_own_move(&c);
    unsigned prior_request=LANE(&c)->request_id; tx=d->tx;
    d->feedback.arm[0].control=2; d->feedback.arm[0].teach=1; d->feedback.arm[0].arm_status=11;
    LANE(&c)->heartbeat=piper_now()-1; // Physical teaching takes priority over our expired lease.
    cJSON *heartbeat=request(&c,""); cJSON_AddNumberToObject(heartbeat,"request_id",prior_request);
    int status; cJSON *result=controller_action(&c,"/api/heartbeat",heartbeat,&status); cJSON_Delete(result); cJSON_Delete(heartbeat);
    CHECK(status==409&&LANE(&c)->op==OP_IDLE&&LANE(&c)->follow!=FOLLOW_LIVE&&LANE(&c)->follow!=FOLLOW_COMMITTED&&!*LANE(&c)->owner&&d->tx==tx);
    cJSON *device=snapshot(&c); CHECK(!cJSON_IsTrue(cJSON_GetObjectItem(device,"recovery_required"))); cJSON_Delete(device);
    controller_tick(&c,piper_now()); CHECK(d->tx==tx&&c.bus.sim_enabled==63);
    d->feedback.arm[0].teach=2; d->feedback.arm[0].arm_status=0;
    action(&c,request(&c,"follow-update"),409); CHECK(d->tx==tx); // Old target never resumes.
    fixture_cleanup(&c,takeover);

    for(unsigned fault=0;fault<4;++fault) {
        char failed[FIXTURE_DIRECTORY]; fixture_directory(failed,sizeof failed,"piper-teaching-fault"); setup(&c,failed); d=&c.bus.devices[0]; enter_own_move(&c); tx=d->tx;
        d->feedback.arm[0].control=2; d->feedback.arm[0].teach=1; d->feedback.arm[0].arm_status=11;
        if(fault==0) d->feedback.motor[0].fault=1;
        if(fault==1) { d->simulated=false; d->fd=-1; } // Bus remains simulated; no host transport is opened.
        if(fault==2) { d->feedback.foreign_seen=true; d->feedback.foreign_at=piper_now(); }
        if(fault==3) { d->feedback.arm[1].status_seen=true; d->feedback.arm[1].status_at=piper_now(); }
        LANE(&c)->heartbeat=piper_now()-1;
        heartbeat=request(&c,""); cJSON_AddNumberToObject(heartbeat,"request_id",LANE(&c)->request_id);
        result=controller_action(&c,"/api/heartbeat",heartbeat,&status); cJSON_Delete(result); cJSON_Delete(heartbeat);
        CHECK(status==409&&LANE(&c)->op==OP_IDLE&&d->tx==tx+1);
        device=snapshot(&c); CHECK(cJSON_IsTrue(cJSON_GetObjectItem(device,"recovery_required"))); cJSON_Delete(device);
        tx=d->tx; controller_tick(&c,piper_now()); CHECK(d->tx==tx); // Existing fault cannot be cleared by entering teaching.
        fixture_cleanup(&c,failed);
    }

    for(unsigned failure=0;failure<6;++failure) {
        char abandoned[FIXTURE_DIRECTORY]; fixture_directory(abandoned,sizeof abandoned,"piper-teaching-abort"); setup(&c,abandoned); d=&c.bus.devices[0];
        action(&c,request(&c,"teach-return"),202); tx=d->tx;
        if(failure==0) { d->feedback.arm[0].control=2; d->feedback.arm[0].teach=1; }
        if(failure==1) { d->feedback.arm[0].control=2; d->feedback.arm[0].deg[5]=.6; }
        if(failure==2) { LANE(&c)->heartbeat=piper_now()-1; }
        if(failure==3) { LANE(&c)->sent_at=piper_now()-2; }
        if(failure==4) { ++d->generation; }
        if(failure==5) { d->feedback.arm[0].control=2; d->feedback.arm[0].teach=3; d->feedback.arm[0].arm_status=12; }
        controller_tick(&c,piper_now()); CHECK(LANE(&c)->op==OP_IDLE&&d->tx==tx&&c.bus.sim_enabled==63);
        fixture_cleanup(&c,abandoned);
    }
    char unexpected[FIXTURE_DIRECTORY]; fixture_directory(unexpected,sizeof unexpected,"piper-teaching-unexpected"); setup(&c,unexpected); d=&c.bus.devices[0];
    action(&c,request(&c,"teach-return"),202); tx=d->tx;
    d->feedback.arm[0].deg[5]=c.bus.sim_target[5]=.6;
    controller_tick(&c,piper_now()); CHECK(LANE(&c)->op==OP_IDLE&&d->tx==tx+1&&d->feedback.arm[0].arm_status==1&&c.bus.sim_enabled==63);
    device=snapshot(&c); CHECK(cJSON_IsTrue(cJSON_GetObjectItem(device,"recovery_required"))); cJSON_Delete(device);
    fixture_cleanup(&c,unexpected);
    for(unsigned noise=0;noise<2;++noise) {
        char settling[FIXTURE_DIRECTORY]; fixture_directory(settling,sizeof settling,"piper-teaching-settling"); setup(&c,settling); d=&c.bus.devices[0];
        action(&c,request(&c,"teach-return"),202); tx=d->tx;
        d->feedback.arm[0].deg[5]=c.bus.sim_target[5]=noise?.2:.06;
        fixture_still(d,piper_now()); // Model a refreshed anchor after small supported settling.
        fake_bus_advance(0.002); controller_tick(&c,piper_now());
        CHECK(LANE(&c)->op==OP_TEACH_RETURN&&LANE(&c)->stage==STAGE_TEACH_CAN&&d->tx==tx+1);
        CHECK(d->feedback.arm[0].arm_status==0&&c.bus.sim_enabled==63);
        // Settling has not qualified a new stationary window, but remains
        // inside the supported handoff settling allowance of 0.5 degrees.
        CHECK(!d->stationary_since||piper_now()-d->stationary_since<.4);
        fake_bus_advance(0.002); controller_tick(&c,piper_now());
        CHECK(LANE(&c)->op==OP_IDLE&&!strcmp(LANE(&c)->phase,"complete")&&d->tx==tx+1);
        device=snapshot(&c); CHECK(!cJSON_IsTrue(cJSON_GetObjectItem(device,"recovery_required"))); cJSON_Delete(device);
        fixture_cleanup(&c,settling);
    }
    char closing[FIXTURE_DIRECTORY]; fixture_directory(closing,sizeof closing,"piper-teaching-close"); setup(&c,closing); d=&c.bus.devices[0];
    action(&c,request(&c,"teach-return"),202); tx=d->tx; fixture_cleanup(&c,closing); CHECK(d->tx==tx);

    char modern[FIXTURE_DIRECTORY]; fixture_directory(modern,sizeof modern,"piper-teaching-modern"); setup(&c,modern); d=&c.bus.devices[0];
    const char *versions[]={"S-V1.8-4","S-V1.8-5","S-V1.9-0","S-V1.9-1","S-V1.9-2","unknown"};
    for(unsigned i=0;i<6;++i) { strcpy(d->feedback.version,versions[i]); CHECK(piper_seamless_teach_supported(&d->feedback)==(i>=1&&i<=3)); }
    strcpy(d->feedback.version,"S-V1.9-0");
    double pose[]={-22.949,-.069,.709,-1.423,23.881,-8.384};
    memcpy(d->feedback.arm[0].deg,pose,sizeof pose);
    for(unsigned i=0;i<6;++i) c.bus.sim_target[i]=LANE(&c)->target[i]=80; // Never replay this prior destination.
    d->feedback.limit[1].min=0; d->feedback.limit[2].max=0;
    CHECK(can_return(&c));
    device=snapshot(&c); cJSON *teaching=cJSON_GetObjectItem(device,"teaching");
    CHECK(!strcmp(cJSON_GetObjectItem(teaching,"return_strategy")->valuestring,"measured-pose"));
    CHECK(cJSON_IsFalse(cJSON_GetObjectItem(teaching,"return_requires_zero"))); cJSON_Delete(device);
    tx=d->tx; action(&c,request(&c,"teach-return"),400); CHECK(d->tx==tx); // A legacy zero flag is not a current-pose acknowledgement.
    for(unsigned rejected=0;rejected<6;++rejected) {
        cJSON *j=request(&c,"teach-return"); cJSON_AddBoolToObject(j,"same_pose_confirmed",true);
        if(rejected==0) { d->feedback.motor[3].enabled=false; }
        if(rejected==1) { d->feedback.arm[0].deg[2]=3; } // Range correction includes the inward 0.5-degree margin.
        if(rejected==2) { fixture_still(d,piper_now()); }
        if(rejected==3) { d->feedback.motor[3].fault=1; }
        if(rejected==4) { d->feedback.foreign_seen=true; d->feedback.foreign_at=piper_now(); }
        if(rejected==5) { d->feedback.arm[0].joint_at[0]=piper_now()-2; }
        action(&c,j,409); CHECK(d->tx==tx);
        d->feedback.motor[3].enabled=true; d->feedback.arm[0].deg[2]=pose[2]; fixture_still(d,piper_now()-1);
        d->feedback.motor[3].fault=0; d->feedback.foreign_seen=false; d->feedback.arm[0].joint_at[0]=piper_now();
    }
    cJSON *j=request(&c,"teach-return"); cJSON_AddBoolToObject(j,"same_pose_confirmed",true);
    action(&c,j,202); CHECK(LANE(&c)->op==OP_TEACH_RETURN&&LANE(&c)->stage==STAGE_TEACH_SEEDED&&d->tx==tx+3);
    CHECK(c.bus.sim_enabled==63&&d->feedback.arm[0].control==2);
    for(unsigned i=0;i<6;++i) { CHECK(fabs(c.bus.sim_target[i]-pose[i])<.00001); CHECK(LANE(&c)->target[i]==pose[i]); }
    sent=LANE(&c)->sent_at; controller_tick(&c,sent); CHECK(LANE(&c)->op==OP_TEACH_RETURN); // No stale status acknowledgement.
    fake_bus_advance(0.025); controller_tick(&c,piper_now()); CHECK(LANE(&c)->op==OP_TEACH_RETURN&&LANE(&c)->stage==STAGE_TEACH_MODE&&d->tx==tx+7);
    sent=LANE(&c)->sent_at; controller_tick(&c,sent); CHECK(LANE(&c)->op==OP_TEACH_RETURN); // Seed feedback cannot acknowledge the final batch.
    fake_bus_advance(0.002); controller_tick(&c,piper_now()); CHECK(LANE(&c)->op==OP_IDLE&&d->tx==tx+7&&c.bus.sim_enabled==63);
    for(unsigned i=0;i<6;++i) CHECK(fabs(d->feedback.arm[0].deg[i]-pose[i])<.00001);
    j=request(&c,"teach-return"); cJSON_AddBoolToObject(j,"same_pose_confirmed",true); action(&c,j,200); CHECK(d->tx==tx+7);
    fixture_cleanup(&c,modern);

    for(unsigned variant=0;variant<3;++variant) {
        char cancel[FIXTURE_DIRECTORY]; fixture_directory(cancel,sizeof cancel,"piper-teaching-modern-abort"); setup(&c,cancel); d=&c.bus.devices[0]; strcpy(d->feedback.version,"S-V1.9-0");
        j=request(&c,"teach-return"); cJSON_AddBoolToObject(j,"same_pose_confirmed",true); action(&c,j,202); tx=d->tx;
        if(variant==0) { d->feedback.arm[0].control=2; d->feedback.arm[0].teach=1; d->feedback.arm[0].arm_status=11; }
        if(variant==1) { d->feedback.arm[0].control=1; d->feedback.arm[0].deg[5]=c.bus.sim_target[5]=.6; }
        if(variant==2) { LANE(&c)->heartbeat=piper_now()-1; }
        controller_tick(&c,piper_now()); CHECK(LANE(&c)->op==OP_IDLE&&c.bus.sim_enabled==63);
        CHECK(d->tx==tx+(variant==1?1:0)); // STOP for unexpected CAN movement; hand guiding/lease cancellation never powers off.
        if(variant!=1) CHECK(strstr(LANE(&c)->message,"measured hold pose"));
        fixture_cleanup(&c,cancel);
    }
    puts("Teaching: legacy zero handoff, firmware-gated measured-pose handoff, fresh acknowledgements, discarded old targets, range-only hold exception and physical teaching priority passed.");
}
static const struct check_test tests[]={{"teaching","every scenario of this file",scenarios,NULL}};
int main(void) { return check_main("teaching",tests,1); }

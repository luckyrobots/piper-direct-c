#define _GNU_SOURCE
#include "fixture.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static void run_case(double measured,const char *action,unsigned joint,double degrees,int expected)
{
    char directory[]="/tmp/piper-range-drift-XXXXXX";
    CHECK(mkdtemp(directory));
    struct controller c;
    CHECK(controller_init(&c,true,directory,""));
    struct can_device *d=&c.bus.devices[0];
    double now=piper_now();
    const double limits[6][2]={{-150,150},{0,180},{-170,0},{-100,100},{-70,70},{-180,180}};
    const double pose[6]={1,.5,-1,1,measured,1};
    memcpy(d->feedback.arm[0].deg,pose,sizeof pose);
    memcpy(c.bus.sim_target,pose,sizeof pose);
    c.bus.sim_enabled=63;
    controller_tick(&c,now);
    strcpy(d->feedback.hardware,"H-V1.2-1");
    strcpy(d->feedback.version,"S-V1.8-2");
    for (unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=limits[i][0],.max=limits[i][1],.speed=.3,.at=now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=now};
    }
    fixture_still(d,now-1);
    strcpy(LANE(&c)->error,"previous lane error");
    cJSON *state=controller_state(&c,"test");
    cJSON *device=cJSON_GetArrayItem(cJSON_GetObjectItem(state,"devices"),0);
    double correction=cJSON_GetObjectItem(device,"range_entry_deg")->valuedouble;
    CHECK((correction==0)==(fabs(measured)<=70.1));
    /* B6: a state read reports the range reason but never overwrites the lane error. */
    CHECK(!strcmp(LANE(&c)->error,"previous lane error"));
    CHECK(!strcmp(cJSON_GetStringValue(cJSON_GetObjectItem(cJSON_GetObjectItem(device,"motion"),"error")),"previous lane error"));
    cJSON_Delete(state);

    cJSON *body=cJSON_CreateObject();
    cJSON_AddStringToObject(body,"device_id",d->id);
    cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddStringToObject(body,"owner","range-drift-regression");
    cJSON_AddStringToObject(body,"action",action);
    cJSON_AddBoolToObject(body,"standalone",true);
    cJSON_AddBoolToObject(body,"clear",true);
    cJSON_AddNumberToObject(body,"speed_percent",100);
    if (!strcmp(action,"pose")) {
        const double zeros[6]={0};
        cJSON_AddItemToObject(body,"degrees",cJSON_CreateDoubleArray(zeros,6));
        cJSON_AddBoolToObject(body,"synchronized",true);
    } else {
        cJSON_AddNumberToObject(body,"joint",joint);
        cJSON_AddNumberToObject(body,"degrees",degrees);
    }
    uint64_t tx=d->tx;
    int status;
    cJSON *reply=controller_action(&c,"/api/command",body,&status);
    if (status!=expected) {
        char *text=cJSON_Print(reply);
        fprintf(stderr,"%s from %.3f: %s\n",action,measured,text); free(text);
    }
    CHECK(status==expected);
    cJSON_Delete(reply); cJSON_Delete(body);
    if (expected==202) {
        if (LANE(&c)->range_followup) {
            CHECK(LANE(&c)->op==OP_ALIGN && LANE(&c)->speed_percent==1);
            if (!strcmp(action,"pose")||!strcmp(action,"move-to-zero"))
                for (unsigned i=0;i<6;++i) CHECK(LANE(&c)->range_followup_target[i]==0);
        } else if (!strcmp(action,"pose")) {
            CHECK(LANE(&c)->synchronized_pose);
            for (unsigned i=0;i<6;++i) CHECK(LANE(&c)->target[i]==0);
        } else if (joint!=5) CHECK(LANE(&c)->target[4]==copysign(70,measured));
        else if (!strcmp(action,"jog")) CHECK(fabs(LANE(&c)->target[4]-(copysign(70,measured)+degrees))<1e-9);
        now=LANE(&c)->started;
        for (unsigned tick=0;tick<500&&LANE(&c)->op!=OP_IDLE;++tick) {
            bool seed=LANE(&c)->position_seed_pending;
            now+=.03; LANE(&c)->heartbeat=now; controller_tick(&c,now);
            /* Only the one-shot unchanged measured hold may preserve the
               admitted boundary overrun. Every moving target remains legal. */
            for (unsigned i=0;i<6;++i) {
                if(seed) CHECK(fabs(c.bus.sim_target[i]-LANE(&c)->initial[i])<.00051);
                else {
                    CHECK(c.bus.sim_target[i]>=limits[i][0]);
                    CHECK(c.bus.sim_target[i]<=limits[i][1]);
                }
            }
        }
        CHECK(LANE(&c)->op==OP_IDLE && !strcmp(LANE(&c)->phase,"complete"));
        CHECK(c.bus.sim_enabled==63);
        for (unsigned i=0;i<6;++i) CHECK(fabs(d->feedback.arm[0].deg[i]-LANE(&c)->target[i])<=LANE(&c)->tolerance);
    } else CHECK(LANE(&c)->op==OP_IDLE && d->tx==tx);
    fixture_cleanup(&c,directory);
}

int main(void)
{
    run_case(70.007,"joint",6,2,202); /* Other joints remain usable. */
    run_case(-70.007,"joint",6,2,202);
    run_case(70.007,"jog",5,-.001,202); /* Even the smallest inward step. */
    run_case(-70.007,"jog",5,.001,202);
    run_case(70.007,"jog",5,.001,400); /* No outward targets. */
    run_case(-70.007,"jog",5,-.001,400);
    run_case(70.007,"joint",5,70.001,400);
    run_case(70.1,"joint",6,2,202);
    run_case(70.101,"joint",6,2,409); /* Significant excursions still need inspection. */
    run_case(-70.101,"joint",6,2,409);
    run_case(70.007,"pose",0,0,202); /* All-zero coordinated position move. */
    run_case(-70.007,"pose",0,0,202);
    run_case(72,"joint",5,69,202); /* Explicit inward target stages entry. */
    run_case(-72,"jog",5,.5,202);
    run_case(72,"jog",5,.5,400); /* Outward remains invalid. */
    run_case(72,"move-to-zero",0,0,202);
    run_case(-72,"pose",0,0,202); /* Legacy all-zero target shares the flow. */
    run_case(72.501,"move-to-zero",0,0,409); /* Includes the .5 degree entry margin. */
    puts("Range drift passed: normal inward and other-joint moves, strict emitted limits, larger excursion rejection, synchronized all-zero pose.");
}

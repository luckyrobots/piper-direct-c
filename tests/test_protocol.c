#include "protocol.h"
#include "check.h"
#include <stdio.h>
#include <string.h>
#include <math.h>

static void scenarios(void)
{
    struct piper_feedback state = {0};
    const uint8_t status[8] = {1,0,1,0,0,0,0x12,0x34};
    const uint8_t joint12[8] = {0,0,0x03,0xe8, 0xff,0xff,0xfc,0x18}; /* +1, -1 deg */
    const uint8_t joint34[8] = {0xff,0xfd,0x67,0xf0, 0,0,0,0}; /* -170, 0 deg */
    const uint8_t joint56[8] = {0,0x02,0xbf,0x20, 0xff,0xfd,0x40,0xe0}; /* +180, -180 */
    CHECK(!piper_fresh(&state.arm[0],0)); /* zeros are not measurements */
    CHECK(piper_decode(&state,0x2a5,joint12,8,10));
    CHECK(state.arm[0].deg[0] == 1 && state.arm[0].deg[1] == -1);
    CHECK(!piper_fresh(&state.arm[0],10));
    CHECK(piper_decode(&state,0x2a6,joint34,8,10));
    CHECK(state.arm[0].deg[2] == -170);
    CHECK(piper_decode(&state,0x2a7,joint56,8,10));
    CHECK(state.arm[0].deg[4] == 180 && state.arm[0].deg[5] == -180);
    CHECK(!piper_fresh(&state.arm[0],10)); /* status still missing */
    CHECK(piper_decode(&state,0x2a1,status,8,10));
    CHECK(state.arm[0].error == 0x1234 && state.arm[0].control == 1);
    CHECK(piper_fresh(&state.arm[0],10.2));
    CHECK(!piper_fresh(&state.arm[0],10.36));
    CHECK(!piper_fresh(&state.arm[0],9));
    /* Updating only status does not refresh stale joint pairs. */
    CHECK(piper_decode(&state,0x2a1,status,8,11));
    CHECK(!piper_fresh(&state.arm[0],11));
    struct piper_feedback before = state;
    CHECK(!piper_decode(&state,0x800002a5,joint12,8,12));
    CHECK(!piper_decode(&state,0x400002a5,joint12,8,12));
    CHECK(!piper_decode(&state,0x200002a5,joint12,8,12));
    CHECK(!piper_decode(&state,0x2a5,joint12,7,12));
    CHECK(!piper_decode(&state,0x2a5,NULL,8,12));
    CHECK(memcmp(&state,&before,sizeof state) == 0);
    CHECK(!piper_decode(&state,0x155,joint12,8,12));
    CHECK(state.foreign_seen && state.foreign_at==12);
    CHECK(piper_decode(&state,0x2b5,joint12,8,20));
    CHECK(state.arm[1].joint_seen[0] && !state.arm[2].joint_seen[0]);
    CHECK(!piper_fresh(&state.arm[1],20)); /* separate address ranges must not mix */
    const uint8_t extremes[8]={0x80,0,0,0,0x7f,0xff,0xff,0xff};
    CHECK(piper_decode(&state,0x2c5,extremes,8,30));
    CHECK(state.arm[2].deg[0] == -2147483.648 && state.arm[2].deg[1] == 2147483.647);
    const uint8_t driver[8]={0,240,0,33,255,0xc0,0,0};
    CHECK(piper_decode(&state,0x261,driver,8,40));
    CHECK(state.motor[0].enabled && state.motor[0].fault==0x80);
    CHECK(state.motor[0].temperature==-1 && state.motor[0].voltage==24);
    const uint8_t high[8]={0xfc,0x18,0xc3,0x50,0,0,0,0};
    CHECK(piper_decode(&state,0x251,high,8,40));
    CHECK(state.motor[0].velocity==-1 && fabs(state.motor[0].current+15.536)<1e-9); /* signed current */
    const uint8_t negative_current[8]={0,0,0xff,0xff,0,0,0,0};
    CHECK(piper_decode(&state,0x251,negative_current,8,40));
    CHECK(state.motor[0].current==-.001);
    const uint8_t negative_position[8]={0,0,0,0,0xff,0xff,0xfc,0x18};
    CHECK(piper_decode(&state,0x251,negative_position,8,40));
    CHECK(state.motor[0].position_raw==-1000);
    const uint8_t limits[8]={1,0x05,0xdc,0xfa,0x24,0x01,0x2c,0};
    CHECK(piper_decode(&state,0x473,limits,8,40));
    CHECK(state.limit[0].min==-150 && state.limit[0].max==150 && state.limit[0].speed_raw==300);
    CHECK(!state.limit[0].speed_unit_known && isnan(state.limit[0].speed));
    const uint8_t acceleration[8]={6,0x01,0xf4}; /* raw500 =5 rad/s^2, unsigned big-endian */
    CHECK(piper_decode(&state,0x47c,acceleration,8,40));
    CHECK(state.acceleration[5].seen && state.acceleration[5].max==5 && state.acceleration[5].at==40);
    const uint8_t maximum_acceleration[8]={1,0xff,0xff};
    CHECK(piper_decode(&state,0x47c,maximum_acceleration,8,40));
    CHECK(fabs(state.acceleration[0].max-655.35)<1e-10); /* Decode the report, never clamp it to a permitted motion cap. */
    const uint8_t zero_acceleration[8]={2,0,0};
    CHECK(piper_decode(&state,0x47c,zero_acceleration,8,40));
    CHECK(state.acceleration[1].seen && state.acceleration[1].max==0);
    uint8_t invalid_acceleration[8]={7,0xff,0xff};
    CHECK(!piper_decode(&state,0x47c,invalid_acceleration,8,41));
    CHECK(!piper_decode(&state,0x47c,acceleration,3,41));
    const uint8_t grip[8]={0,0,0x61,0xa8,1,244,0xc0,0};
    CHECK(piper_decode(&state,0x2a8,grip,8,40));
    CHECK(state.gripper_mm==25 && state.gripper_enabled && !state.gripper_fault);
    const double target[6]={-12.5,1,0,0,0,0}; struct can_frame frames[3];
    CHECK(piper_targets(target,frames));
    const uint8_t wire[8]={0xff,0xff,0xcf,0x2c,0,0,3,0xe8};
    CHECK(frames[0].can_id==0x155 && frames[2].can_id==0x157 && !memcmp(frames[0].data,wire,8));
    double invalid[6]={NAN}; CHECK(!piper_targets(invalid,frames));
    CHECK(!piper_gripper(101,.1,true,&frames[0]));
    CHECK(!piper_gripper(-1,.1,true,&frames[0]));
    CHECK(!piper_gripper(25,.019,true,&frames[0]));
    CHECK(!piper_gripper(25,NAN,true,&frames[0]));
    CHECK(!piper_gripper(25,.501,true,&frames[0]));
    CHECK(piper_gripper(100,.1,true,&frames[0]) && frames[0].data[4]==0 && frames[0].data[5]==100);
    const uint8_t config[8]={100,100,1};
    CHECK(piper_decode(&state,0x47e,config,8,40));
    CHECK(state.gripper_config_seen && state.gripper_max_mm==100 && state.gripper_config_at==40);
    CHECK(piper_gripper(25,.5,true,&frames[0]) && frames[0].data[6]==1 && frames[0].data[7]==0);
    const char firmware[]="H-V1.2-1\0S-V1.8-2251014";
    for (size_t i=0;i<sizeof firmware-1;i+=8) {
        size_t n=sizeof firmware-1-i; if (n>8) n=8;
        piper_decode(&state,0x4af,(const uint8_t *)firmware+i,n,50);
    }
    CHECK(!piper_supported(&state)); /* unterminated version cannot grant control */
    const uint8_t terminator=0; piper_decode(&state,0x4af,&terminator,1,50);
    CHECK(piper_supported(&state) && !strcmp(state.version,"S-V1.8-2") && !strcmp(state.build,"2025-10-14"));
    CHECK(state.limit[0].speed==3 && state.limit[0].speed_resolution_rad_s==.01 && state.limit[0].at==40);
    strcpy(state.version,"S-V1.9-0"); CHECK(piper_supported(&state));
    strcpy(state.version,"S-V1.9-1"); CHECK(piper_supported(&state));
    strcpy(state.version,"S-V1.9-2"); CHECK(!piper_supported(&state));
    strcpy(state.version,"S-V2.0-0"); CHECK(!piper_supported(&state));
    strcpy(state.version,"S-V1.9-0"); strcpy(state.hardware,"H-V2.0-0"); CHECK(!piper_supported(&state));
    struct piper_feedback corrupt={0};
    const char malformed[]="H-V1.2-1\0S-V112";
    for (size_t i=0;i<sizeof malformed;i+=8) {
        size_t n=sizeof malformed-i; if(n>8)n=8;
        piper_decode(&corrupt,0x4af,(const uint8_t *)malformed+i,n,60);
    }
    CHECK(!*corrupt.version&&!piper_supported(&corrupt));
    /* Read-only query frames, previously hand-built in the controller. */
    struct can_frame q=piper_firmware_query(); CHECK(q.can_id==0x4af&&q.len==8&&q.data[0]==1&&!q.data[1]);
    q=piper_gripper_config_query(); CHECK(q.can_id==0x477&&q.data[0]==4&&q.data[4]==3&&!q.data[1]&&!q.data[5]);
    q=piper_limit_query(3,false); CHECK(q.can_id==0x472&&q.data[0]==3&&q.data[1]==1&&!q.data[2]);
    q=piper_limit_query(6,true); CHECK(q.data[0]==6&&q.data[1]==2);
    CHECK(piper_foreign_command(0x150)&&piper_foreign_command(0x18f)&&piper_foreign_command(0x470)&&piper_foreign_command(0x471));
    CHECK(!piper_foreign_command(0x14f)&&!piper_foreign_command(0x190)&&!piper_foreign_command(0x472));
    for (unsigned t=0;t<9;++t) CHECK(piper_teach_active((uint8_t)t)==(t==1||t==3||t==4||t==5||t==7)&&piper_teach_idle((uint8_t)t)==(t==0||t==2||t==6));
    puts("Protocol fixtures passed: signs, units, status, freshness, bad frames, separate addresses.");
}
static const struct check_test tests[]={{"protocol","every scenario of this file",scenarios,NULL}};
int main(void) { return check_main("protocol",tests,1); }

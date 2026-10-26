#include "protocol.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include <math.h>

int main(void)
{
    struct piper_feedback state = {0};
    const uint8_t status[8] = {1,0,1,0,0,0,0x12,0x34};
    const uint8_t joint12[8] = {0,0,0x03,0xe8, 0xff,0xff,0xfc,0x18}; /* +1, -1 deg */
    const uint8_t joint34[8] = {0xff,0xfd,0x67,0xf0, 0,0,0,0}; /* -170, 0 deg */
    const uint8_t joint56[8] = {0,0x02,0xbf,0x20, 0xff,0xfd,0x40,0xe0}; /* +180, -180 */
    assert(!piper_fresh(&state.arm[0],0)); /* zeros are not measurements */
    assert(piper_decode(&state,0x2a5,joint12,8,10));
    assert(state.arm[0].deg[0] == 1 && state.arm[0].deg[1] == -1);
    assert(!piper_fresh(&state.arm[0],10));
    assert(piper_decode(&state,0x2a6,joint34,8,10));
    assert(state.arm[0].deg[2] == -170);
    assert(piper_decode(&state,0x2a7,joint56,8,10));
    assert(state.arm[0].deg[4] == 180 && state.arm[0].deg[5] == -180);
    assert(!piper_fresh(&state.arm[0],10)); /* status still missing */
    assert(piper_decode(&state,0x2a1,status,8,10));
    assert(state.arm[0].error == 0x1234 && state.arm[0].control == 1);
    assert(piper_fresh(&state.arm[0],10.2));
    assert(!piper_fresh(&state.arm[0],10.36));
    assert(!piper_fresh(&state.arm[0],9));
    /* Updating only status does not refresh stale joint pairs. */
    assert(piper_decode(&state,0x2a1,status,8,11));
    assert(!piper_fresh(&state.arm[0],11));
    struct piper_feedback before = state;
    assert(!piper_decode(&state,0x800002a5,joint12,8,12));
    assert(!piper_decode(&state,0x400002a5,joint12,8,12));
    assert(!piper_decode(&state,0x200002a5,joint12,8,12));
    assert(!piper_decode(&state,0x2a5,joint12,7,12));
    assert(!piper_decode(&state,0x2a5,NULL,8,12));
    assert(memcmp(&state,&before,sizeof state) == 0);
    assert(!piper_decode(&state,0x155,joint12,8,12));
    assert(state.foreign_seen && state.foreign_at==12);
    assert(piper_decode(&state,0x2b5,joint12,8,20));
    assert(state.arm[1].joint_seen[0] && !state.arm[2].joint_seen[0]);
    assert(!piper_fresh(&state.arm[1],20)); /* separate address ranges must not mix */
    const uint8_t extremes[8]={0x80,0,0,0,0x7f,0xff,0xff,0xff};
    assert(piper_decode(&state,0x2c5,extremes,8,30));
    assert(state.arm[2].deg[0] == -2147483.648 && state.arm[2].deg[1] == 2147483.647);
    const uint8_t driver[8]={0,240,0,33,255,0xc0,0,0};
    assert(piper_decode(&state,0x261,driver,8,40));
    assert(state.motor[0].enabled && state.motor[0].fault==0x80);
    assert(state.motor[0].temperature==-1 && state.motor[0].voltage==24);
    const uint8_t high[8]={0xfc,0x18,0xc3,0x50,0,0,0,0};
    assert(piper_decode(&state,0x251,high,8,40));
    assert(state.motor[0].velocity==-1 && fabs(state.motor[0].current+15.536)<1e-9); /* signed current */
    const uint8_t negative_current[8]={0,0,0xff,0xff,0,0,0,0};
    assert(piper_decode(&state,0x251,negative_current,8,40));
    assert(state.motor[0].current==-.001);
    const uint8_t limits[8]={1,0x05,0xdc,0xfa,0x24,0x01,0x2c,0};
    assert(piper_decode(&state,0x473,limits,8,40));
    assert(state.limit[0].min==-150 && state.limit[0].max==150 && state.limit[0].speed==.3);
    const uint8_t acceleration[8]={6,0x13,0x88};
    assert(piper_decode(&state,0x47c,acceleration,8,40));
    assert(state.acceleration[5].seen && state.acceleration[5].max==5 && state.acceleration[5].at==40);
    uint8_t invalid_acceleration[8]={7,0xff,0xff};
    assert(!piper_decode(&state,0x47c,invalid_acceleration,8,41));
    assert(!piper_decode(&state,0x47c,acceleration,3,41));
    const uint8_t grip[8]={0,0,0x61,0xa8,1,244,0xc0,0};
    assert(piper_decode(&state,0x2a8,grip,8,40));
    assert(state.gripper_mm==25 && state.gripper_enabled && !state.gripper_fault);
    const double target[6]={-12.5,1,0,0,0,0}; struct can_frame frames[3];
    assert(piper_targets(target,frames));
    const uint8_t wire[8]={0xff,0xff,0xcf,0x2c,0,0,3,0xe8};
    assert(frames[0].can_id==0x155 && frames[2].can_id==0x157 && !memcmp(frames[0].data,wire,8));
    double invalid[6]={NAN}; assert(!piper_targets(invalid,frames));
    assert(!piper_gripper(101,.1,true,&frames[0]));
    assert(!piper_gripper(-1,.1,true,&frames[0]));
    assert(!piper_gripper(25,.019,true,&frames[0]));
    assert(!piper_gripper(25,NAN,true,&frames[0]));
    assert(!piper_gripper(25,.501,true,&frames[0]));
    assert(piper_gripper(100,.1,true,&frames[0]) && frames[0].data[4]==0 && frames[0].data[5]==100);
    const uint8_t config[8]={100,100,1};
    assert(piper_decode(&state,0x47e,config,8,40));
    assert(state.gripper_config_seen && state.gripper_max_mm==100 && state.gripper_config_at==40);
    assert(piper_gripper(25,.5,true,&frames[0]) && frames[0].data[6]==1 && frames[0].data[7]==0);
    const char firmware[]="H-V1.2-1\0S-V1.8-2251014";
    for (size_t i=0;i<sizeof firmware-1;i+=8) {
        size_t n=sizeof firmware-1-i; if (n>8) n=8;
        piper_decode(&state,0x4af,(const uint8_t *)firmware+i,n,50);
    }
    assert(!piper_supported(&state)); /* unterminated version cannot grant control */
    const uint8_t terminator=0; piper_decode(&state,0x4af,&terminator,1,50);
    assert(piper_supported(&state) && !strcmp(state.version,"S-V1.8-2") && !strcmp(state.build,"2025-10-14"));
    puts("Protocol fixtures passed: signs, units, status, freshness, bad frames, separate addresses.");
    return 0;
}

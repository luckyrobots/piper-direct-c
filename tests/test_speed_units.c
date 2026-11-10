#define _GNU_SOURCE
#include "fixture.h"
#include "fake_bus.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static const uint8_t speed300[8]={1,0x05,0xdc,0xfa,0x24,0x01,0x2c,0};
static void feed(struct piper_feedback *s,const void *raw,size_t length,double now,size_t chunk)
{
    const uint8_t *bytes=raw;
    for (size_t i=0;i<length;i+=chunk) {
        size_t count=length-i; if (count>chunk) count=chunk;
        CHECK(piper_decode(s,0x4af,bytes+i,count,now));
    }
}
static void firmware(struct piper_feedback *s,const char *version,double now,size_t chunk)
{
    char text[160]; int length=snprintf(text,sizeof text,"H-V1.2-1%c%s",0,version);
    CHECK(length>0&&(size_t)length<sizeof text);
    feed(s,text,(size_t)length+1,now,chunk);
}
static void resolved(struct piper_feedback *s,double resolution,const char *version,double receipt)
{
    const struct piper_limit *l=&s->limit[0];
    CHECK(l->seen&&l->speed_raw_seen&&l->speed_raw==300&&l->speed_unit_known);
    CHECK(l->speed_resolution_rad_s==resolution&&fabs(l->speed-300*resolution)<1e-12);
    CHECK(!strcmp(l->speed_firmware,version)&&l->at==receipt);
    CHECK(l->min==-150&&l->max==150);
}
static void unresolved(struct piper_feedback *s)
{
    const struct piper_limit *l=&s->limit[0];
    CHECK(l->seen&&l->speed_raw_seen&&l->speed_raw==300&&!l->speed_unit_known);
    CHECK(isnan(l->speed)&&isnan(l->speed_resolution_rad_s)&&!l->speed_firmware[0]);
}
static void scenarios(void)
{
    const struct { const char *version,*canonical; double resolution; } cases[]={
        {"S-V1.4-9","S-V1.4-9",.001},{"S-V1.5-7","S-V1.5-7",.001},
        {"S-V1.5-8","S-V1.5-8",.01},{"S-V1.5-9","S-V1.5-9",.01},
        {"S-V1.6-3","S-V1.6-3",.01},{"S-V1.8-2","S-V1.8-2",.01},
        {"S-V1.9-1","S-V1.9-1",.01},{"S-V1.5-7251014","S-V1.5-7",.001},
        {"S-V1.5-8251014","S-V1.5-8",.01}
    };
    for (size_t i=0;i<sizeof cases/sizeof cases[0];++i) for (size_t chunk=1;chunk<=8;++chunk) {
        struct piper_feedback before={0},after={0};
        CHECK(piper_decode(&before,0x473,speed300,8,10)); unresolved(&before);
        firmware(&before,cases[i].version,11,chunk); resolved(&before,cases[i].resolution,cases[i].canonical,10);
        firmware(&after,cases[i].version,10,chunk);
        CHECK(piper_decode(&after,0x473,speed300,8,11)); resolved(&after,cases[i].resolution,cases[i].canonical,11);
    }
    const char *unknown[]={"","S-V112","S-V1.8--2","S-V1.8-2extra","S-V1.9-2","S-V1.10-0","S-V2.0-0",
        "S-V1.8-10","S-V4294967297.8-2","S-V1.8-4294967298","S-V1.5-8999999"};
    for (size_t i=0;i<sizeof unknown/sizeof unknown[0];++i) for (size_t chunk=1;chunk<=8;++chunk) {
        struct piper_feedback s={0}; CHECK(piper_decode(&s,0x473,speed300,8,20));
        firmware(&s,unknown[i],21,chunk); unresolved(&s); CHECK(!piper_supported(&s));
    }
    struct piper_feedback partial={0}; CHECK(piper_decode(&partial,0x473,speed300,8,30));
    const char unterminated[]="H-V1.2-1\0S-V1.9-1";
    feed(&partial,unterminated,sizeof unterminated-1,31,1); unresolved(&partial);
    const uint8_t zero=0; feed(&partial,&zero,1,31,1); resolved(&partial,.01,"S-V1.9-1",30);
    /* Re-reading a fragmented firmware block must withdraw the old unit until
       the replacement is complete; never renew the saved limit timestamp. */
    const char legacy[]="H-V1.2-1\0S-V1.5-7";
    feed(&partial,legacy,8,32,8); unresolved(&partial);
    feed(&partial,legacy+8,sizeof legacy-8,32,3); resolved(&partial,.001,"S-V1.5-7",30);
    firmware(&partial,"S-V2.0-0",33,1); unresolved(&partial);
    firmware(&partial,"S-V1.9-1",34,8); resolved(&partial,.01,"S-V1.9-1",30);
    uint8_t garbage[8]; memset(garbage,'x',sizeof garbage);
    for (unsigned i=0;i<70;++i) feed(&partial,garbage,sizeof garbage,34,8);
    unresolved(&partial); CHECK(!piper_supported(&partial));

    struct can_device connection={0}; firmware(&connection.feedback,"S-V1.9-1",40,8);
    CHECK(piper_decode(&connection.feedback,0x473,speed300,8,40)); resolved(&connection.feedback,.01,"S-V1.9-1",40);
    can_bus_invalidate_configuration(&connection,41);
    CHECK(!connection.feedback.limit[0].seen&&!connection.feedback.limit[0].speed_raw_seen&&!connection.feedback.version[0]);
    CHECK(piper_decode(&connection.feedback,0x473,speed300,8,42)); unresolved(&connection.feedback);
    firmware(&connection.feedback,"S-V1.5-7",43,2); resolved(&connection.feedback,.001,"S-V1.5-7",42);

    struct piper_feedback acceleration={0}; const uint8_t raw500[8]={1,1,244};
    CHECK(piper_decode(&acceleration,0x47c,raw500,8,49));
    CHECK(acceleration.acceleration[0].max==5); /* 0x47C is .01 rad/s^2, separate from the speed-version boundary. */
    firmware(&acceleration,"S-V1.6-3",50,8); CHECK(piper_decode(&acceleration,0x47c,raw500,8,50));
    CHECK(acceleration.acceleration[0].max==5);
    firmware(&acceleration,"S-V1.9-1",51,8); CHECK(piper_decode(&acceleration,0x47c,raw500,8,51));
    CHECK(acceleration.acceleration[0].max==5);
    uint8_t range[8]; memcpy(range,speed300,sizeof range); range[5]=range[6]=0;
    CHECK(piper_decode(&acceleration,0x473,range,8,51)&&acceleration.limit[0].speed==0);
    range[5]=range[6]=255;
    CHECK(piper_decode(&acceleration,0x473,range,8,51)&&acceleration.limit[0].speed_raw==65535);
    CHECK(fabs(acceleration.limit[0].speed-655.35)<1e-10);

    char directory[96]; fixture_directory(directory,sizeof directory,"piper-speed-units");
    struct controller c; CHECK(controller_init(&c,true,directory,""));
    c.bus.sim_enabled=63; double now=piper_now(); controller_tick(&c,now);
    struct can_device *d=&c.bus.devices[0];
    for (unsigned i=0;i<6;++i) {
        const struct can_frame query={.can_id=0x472,.can_dlc=8,.data={(uint8_t)(i+1),2}};
        CHECK(can_bus_send(&c.bus,d,d->generation,&query,1));
        CHECK(d->feedback.acceleration[i].seen&&d->feedback.acceleration[i].max==5);
    }
    firmware(&d->feedback,"S-V1.9-1",now,8);
    for (unsigned i=0;i<6;++i) {
        uint8_t packet[8]; memcpy(packet,speed300,sizeof packet); packet[0]=(uint8_t)(i+1);
        CHECK(piper_decode(&d->feedback,0x473,packet,8,now));
        uint8_t acc[8]={(uint8_t)(i+1),1,244}; CHECK(piper_decode(&d->feedback,0x47c,acc,8,now));
    }
    CHECK(!controller_blocker(&c,d,now));
    for (unsigned i=0;i<6;++i) CHECK(d->feedback.acceleration[i].max==5);
    cJSON *state=controller_state(&c,"fixture"),*device=cJSON_GetArrayItem(cJSON_GetObjectItem(state,"devices"),0);
    cJSON *joint=cJSON_GetArrayItem(cJSON_GetObjectItem(device,"joints"),0);
    CHECK(cJSON_GetObjectItem(joint,"max_speed_rad_s")->valuedouble==3);
    CHECK(cJSON_GetObjectItem(joint,"max_acceleration_rad_s2")->valuedouble==5);
    CHECK(cJSON_GetObjectItem(joint,"max_speed_raw")->valueint==300);
    CHECK(cJSON_GetObjectItem(joint,"max_speed_resolution_rad_s")->valuedouble==.01);
    CHECK(cJSON_IsTrue(cJSON_GetObjectItem(joint,"max_speed_unit_known")));
    CHECK(!strcmp(cJSON_GetObjectItem(joint,"max_speed_unit_firmware")->valuestring,"S-V1.9-1")); cJSON_Delete(state);
    const uint8_t over_cap[8]={1,1,245}; /* raw501 =5.01, above the unchanged5 rad/s^2 guard. */
    CHECK(piper_decode(&d->feedback,0x47c,over_cap,8,now));
    CHECK(controller_blocker(&c,d,now));
    CHECK(piper_decode(&d->feedback,0x47c,raw500,8,now));
    CHECK(!controller_blocker(&c,d,now));
    d->feedback.limit[0].speed=NAN;
    CHECK(controller_blocker(&c,d,now)); /* An unresolved unit cannot pass a comparison with NaN. */
    fixture_cleanup(&c,directory);
    puts("Speed units: V1.5-8 boundary, firmware ordering/provenance/reconnect; acceleration raw500=5 with unchanged admission cap passed.");
}
static const struct check_test tests[]={{"speed-units","every scenario of this file",scenarios,NULL}};
int main(void) { return check_main("speed-units",tests,1); }

#ifndef PIPER_TEST_FIXTURE_H
#define PIPER_TEST_FIXTURE_H
/* Shared helpers for the offline native tests. Include after _GNU_SOURCE. */
#include "controller_internal.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#ifdef _WIN32
#include <windows.h>
#endif

#include "check.h"

/* The selected device's lane, created on first use. */
#define LANE(c) controller_lane((c),(c)->selected)

/* A fake "open" transport for a non-simulated device. It is negative, so no
   OS call (close, send, recvmsg) can ever act on a real descriptor. */
#define FIXTURE_FD (-2)

/* Deterministic clock for tests linked with -Wl,--wrap=piper_now. */
#define FIXTURE_WRAP_PIPER_NOW(clock) double __wrap_piper_now(void) { return (clock); }

/* Mark a device strictly still since `since` (both detector timestamps). */
static inline void fixture_still(struct can_device *d,double since)
{
    d->stationary_phase=STATIONARY_STRICT_STILL; d->stationary_since=d->pose_stationary_since=since;
}

/* Firmware, limits and acceleration for a supported H-V1.2-1 arm. */
static inline void fixture_configure(struct can_device *d,const char *version,double minimum,double maximum,double speed,double now)
{
    strcpy(d->feedback.hardware,"H-V1.2-1"); strcpy(d->feedback.version,version);
    for (unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=minimum,.max=maximum,.speed=speed,.at=now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=now};
    }
}
/* The vendor H-V1.2-1 joint ranges used by most fixtures. */
static inline void fixture_vendor_limits(struct can_device *d,double speed,double now)
{
    const double limits[6][2]={{-150,150},{0,180},{-170,0},{-100,100},{-70,70},{-180,180}};
    for (unsigned i=0;i<6;++i) {
        d->feedback.limit[i]=(struct piper_limit){.seen=true,.min=limits[i][0],.max=limits[i][1],.speed=speed,.at=now};
        d->feedback.acceleration[i]=(struct piper_acceleration){.seen=true,.max=5,.at=now};
    }
}

/* A fresh state directory "<prefix>-XXXXXX" under the system temp folder. */
static inline void fixture_directory(char *directory,size_t capacity,const char *prefix)
{
#ifdef _WIN32
    static unsigned serial;
    char temp[MAX_PATH]; CHECK(GetTempPathA(sizeof temp,temp)>0);
    snprintf(directory,capacity,"%s%s-%lu-%u",temp,prefix,(unsigned long)GetCurrentProcessId(),++serial);
#else
    CHECK(snprintf(directory,capacity,"/tmp/%s-XXXXXX",prefix)<(int)capacity);
    CHECK(mkdtemp(directory));
#endif
}
/* Close the controller and remove its state directory. */
static inline void fixture_cleanup(struct controller *c,const char *directory)
{
    controller_close(c);
    char path[1100]; snprintf(path,sizeof path,"%s/control-state.json",directory);
    CHECK(!unlink(path)); CHECK(!rmdir(directory));
}
/* Parse a JSON file, failing loudly when it is missing or malformed. */
static inline cJSON *fixture_read_json(const char *path)
{
    FILE *f=fopen(path,"rb"); CHECK(f);
    static char text[1<<16]; size_t n=fread(text,1,sizeof text-1,f); fclose(f); text[n]=0;
    cJSON *json=cJSON_Parse(text); CHECK(json);
    return json;
}
#endif

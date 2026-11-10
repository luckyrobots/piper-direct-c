#define _GNU_SOURCE
#include "camera.h"
#include "check.h"
#include "canbus.h"
#include <stdio.h>
#include <time.h>
#include <unistd.h>

static void scenarios(void)
{
    CHECK(camera_init("")); CHECK(camera_start()!=NULL);
    CHECK(!camera_init("relative-device"));
    CHECK(camera_init("/dev/piper-camera-does-not-exist"));
    size_t size=99; CHECK(!camera_frame(piper_now(),&size)&&size==0);
    bool capture=!access("/usr/bin/ffmpeg",X_OK);
    if (!capture) puts("SKIP camera capture lifecycle: /usr/bin/ffmpeg is not installed, so only the disabled and unavailable-device checks ran.");
    else {
        CHECK(camera_start()==NULL);
        bool stopped=false; double deadline=piper_now()+3;
        while (piper_now()<deadline) {
            camera_tick(piper_now()); cJSON *state=camera_state(piper_now());
            stopped=cJSON_IsFalse(cJSON_GetObjectItem(state,"running"));
            if (stopped) CHECK(*cJSON_GetObjectItem(state,"error")->valuestring);
            CHECK(cJSON_IsFalse(cJSON_GetObjectItem(state,"fresh"))); cJSON_Delete(state);
            if (stopped) break;
            const struct timespec pause={.tv_nsec=10000000}; nanosleep(&pause,NULL);
        }
        CHECK(stopped); CHECK(camera_start()==NULL); // A failed capture can be retried.
    }
    camera_close();
    puts(capture?"Camera lifecycle passed: disabled default, unavailable device, no stale JPEG, child exit and cleanup.":
        "Camera lifecycle passed (capture SKIPPED): disabled default, unavailable device, no stale JPEG.");
}
static const struct check_test tests[]={{"camera","every scenario of this file",scenarios,NULL}};
int main(void) { return check_main("camera",tests,1); }

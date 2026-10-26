#define _GNU_SOURCE
#include "camera.h"
#include "canbus.h"
#include <assert.h>
#include <stdio.h>
#include <time.h>
#include <unistd.h>

int main(void)
{
    assert(camera_init("")); assert(camera_start()!=NULL);
    assert(!camera_init("relative-device"));
    assert(camera_init("/dev/piper-camera-does-not-exist"));
    size_t size=99; assert(!camera_frame(piper_now(),&size)&&size==0);
    if (!access("/usr/bin/ffmpeg",X_OK)) {
        assert(camera_start()==NULL);
        bool stopped=false; double deadline=piper_now()+3;
        while (piper_now()<deadline) {
            camera_tick(piper_now()); cJSON *state=camera_state(piper_now());
            stopped=cJSON_IsFalse(cJSON_GetObjectItem(state,"running"));
            if (stopped) assert(*cJSON_GetObjectItem(state,"error")->valuestring);
            assert(cJSON_IsFalse(cJSON_GetObjectItem(state,"fresh"))); cJSON_Delete(state);
            if (stopped) break;
            const struct timespec pause={.tv_nsec=10000000}; nanosleep(&pause,NULL);
        }
        assert(stopped); assert(camera_start()==NULL); // A failed capture can be retried.
    }
    camera_close();
    puts("Camera lifecycle passed: disabled default, unavailable device, no stale JPEG, child exit and cleanup.");
    return 0;
}

#ifndef PIPER_CAMERA_H
#define PIPER_CAMERA_H
#include "cJSON.h"
#include <stdbool.h>
#include <stddef.h>
bool camera_init(const char *device);
const char *camera_start(void);
void camera_stop(void);
void camera_tick(double now);
void camera_close(void);
cJSON *camera_state(double now);
const unsigned char *camera_frame(double now,size_t *size);
#endif

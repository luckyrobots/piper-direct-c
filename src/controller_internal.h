#ifndef PIPER_CONTROLLER_INTERNAL_H
#define PIPER_CONTROLLER_INTERNAL_H
/* White-box entry points for the offline native tests. Production callers use
   controller.h only; nothing here changes the HTTP/JSON control surface. */
#include "controller.h"

/* STOP recorded as a software obstruction (kind "obstruction"), the only
   incident that recover-at-pose may clear. controller_stop records "stop". */
void controller_stop_obstruction(struct controller *c,const char *reason);
#endif

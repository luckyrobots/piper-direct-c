#include "controller.h"
#include <stdlib.h>
#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif
EXPORT void *piper_create(int simulated,const char *directory) {
    struct controller *c=calloc(1,sizeof *c);
    if (!c||!controller_init(c,simulated!=0,directory,"")) { free(c); return NULL; }
    return c;
}
EXPORT void piper_tick(void *ptr) {
    static double scanned;
    struct controller *c=ptr; double now=piper_now();
    if (now-scanned>=1) { can_bus_scan(&c->bus); scanned=now; }
    controller_tick(c,now);
}
EXPORT char *piper_state(void *ptr,const char *token) {
    cJSON *state=controller_state(ptr,token); char *json=cJSON_PrintUnformatted(state); cJSON_Delete(state); return json;
}
EXPORT char *piper_action(void *ptr,const char *route,const char *body,int *status) {
    cJSON *input=cJSON_Parse(body);
    if (!cJSON_IsObject(input)) { cJSON_Delete(input); *status=400; return NULL; }
    cJSON *result=controller_action(ptr,route,input,status); cJSON_Delete(input);
    char *json=cJSON_PrintUnformatted(result); cJSON_Delete(result); return json;
}
EXPORT void piper_release(char *ptr) { free(ptr); }
EXPORT void piper_destroy(void *ptr) { if (ptr) { controller_close(ptr); free(ptr); } }

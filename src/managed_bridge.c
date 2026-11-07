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
EXPORT void *piper_create_simulated(int count,const char *directory) {
    if (count<1||count>CANBUS_MAX) return NULL;
    struct controller *c=calloc(1,sizeof *c);
    if (!c||!controller_init_simulated(c,(unsigned)count,directory,"")) { free(c); return NULL; }
    return c;
}
EXPORT void piper_tick(void *ptr) {
    struct controller *c=ptr; double now=piper_now();
    if (now-c->inventory_scanned>=1) {
#ifdef _WIN32
        /* Opening a new USB transport can block beyond a reference interval.
           Defer discovery until idle; identity/loss checks still run in motion. */
        if (!controller_any_motion_active(c)) can_bus_scan(&c->bus);
        else can_bus_check_connections(&c->bus);
#else
        can_bus_scan(&c->bus);
#endif
        c->inventory_scanned=now;
    }
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

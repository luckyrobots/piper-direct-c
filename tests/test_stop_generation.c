#define _GNU_SOURCE
#include "fixture.h"

/* /api/stop carries two intents. An emergency STOP (no well-formed
   request_id tied to a known arm) is never refused without stopping. A cancel
   (a known arm and a request_id) stops that request only while it is current.
   Each case starts a real move in the production controller, sends one STOP
   body and checks what was stopped and what the reply says. */
enum generation { GENERATION_NONE, GENERATION_CURRENT, GENERATION_STALE, GENERATION_RAW };
enum request { REQUEST_NONE, REQUEST_MATCH, REQUEST_MALFORMED, REQUEST_OWNER_ONLY };
struct stop_case {
    const char *device;         /* JSON for device_id, "$" for the arm's id, NULL to omit */
    enum generation generation;
    const char *raw_generation; /* JSON, with GENERATION_RAW */
    enum request request;
    int status;
    const char *scope;          /* stop_scope in the reply */
    const char *reason;         /* fragment of stop_scope_reason, NULL when the scope was honoured */
};
static const char *owner="stop-generation-fixture";

static const char *string(const cJSON *json,const char *key)
{
    const char *value=cJSON_GetStringValue(cJSON_GetObjectItem(json,key)); return value?value:"";
}
static struct can_device *start(struct controller *c,char *directory,size_t size,bool moving)
{
    fixture_directory(directory,size,"piper-stop-generation");
    CHECK(controller_init(c,true,directory,""));
    double now=piper_now(); controller_tick(c,now);
    struct can_device *d=&c->bus.devices[0];
    c->bus.sim_enabled=63; controller_tick(c,piper_now());
    fixture_configure(d,"S-V1.9-0",-180,180,.3,now); fixture_still(d,now-1);
    if (!moving) return d;
    cJSON *body=cJSON_CreateObject(); cJSON_AddStringToObject(body,"device_id",d->id);
    cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddStringToObject(body,"action","joint"); cJSON_AddStringToObject(body,"owner",owner);
    cJSON_AddNumberToObject(body,"joint",6); cJSON_AddNumberToObject(body,"degrees",1);
    cJSON_AddBoolToObject(body,"standalone",true); cJSON_AddBoolToObject(body,"clear",true);
    int status=0; cJSON *result=controller_action(c,"/api/command",body,&status);
    CHECK(status==202&&LANE(c)->op==OP_MOVE); cJSON_Delete(body); cJSON_Delete(result);
    return d;
}
/* A cancel whose request is not the current one on its arm stops nothing: no
   frame, no latch, and the running action (if any) keeps its owner. */
static void stale_cancel(bool moving,unsigned request_offset,const char *cancel_owner)
{
    char directory[96]; struct controller c; struct can_device *d=start(&c,directory,sizeof directory,moving);
    uint32_t running=LANE(&c)->request_id;
    cJSON *body=cJSON_CreateObject(); cJSON_AddStringToObject(body,"device_id",d->id);
    cJSON_AddNumberToObject(body,"generation",d->generation);
    cJSON_AddNumberToObject(body,"request_id",(moving?running:1)+request_offset); cJSON_AddStringToObject(body,"owner",cancel_owner);
    uint64_t tx=d->tx; int status=0; cJSON *result=controller_action(&c,"/api/stop",body,&status);
    CHECK(status==409&&d->tx==tx&&!cJSON_IsTrue(cJSON_GetObjectItem(result,"ok")));
    CHECK(!strcmp(string(result,"stop_intent"),"cancel")&&strstr(string(result,"error"),"no longer the current action")&&cJSON_GetArraySize(cJSON_GetObjectItem(result,"stops"))==0);
    CHECK(LANE(&c)->op==(moving?OP_MOVE:OP_IDLE)&&LANE(&c)->request_id==running&&(!moving||!strcmp(LANE(&c)->owner,owner)));
    const cJSON *record=cJSON_GetObjectItem(cJSON_GetObjectItem(c.saved,"devices"),d->id);
    CHECK(!cJSON_IsTrue(cJSON_GetObjectItem(record,"stop_event_active"))&&(moving||!cJSON_IsTrue(cJSON_GetObjectItem(record,"blocked"))));
    cJSON_Delete(body); cJSON_Delete(result);
    /* The running action is still stopped by its own cancel. */
    if (moving) {
        body=cJSON_CreateObject(); cJSON_AddStringToObject(body,"device_id",d->id);
        cJSON_AddNumberToObject(body,"request_id",running); cJSON_AddStringToObject(body,"owner",owner);
        result=controller_action(&c,"/api/stop",body,&status);
        CHECK(status==200&&d->tx==tx+1&&LANE(&c)->op==OP_IDLE&&!strcmp(string(result,"stop_intent"),"cancel"));
        cJSON_Delete(body); cJSON_Delete(result);
    }
    fixture_cleanup(&c,directory);
}
static void run(const struct stop_case *k,unsigned index)
{
    char directory[96],path[160]; struct controller c; struct can_device *d=start(&c,directory,sizeof directory,true);
    snprintf(path,sizeof path,"%s/control-state.json",directory);
    cJSON *body,*result; int status=0;
    /* Onboard teaching feedback: a STOP that cannot be tied to this arm must
       still stop it, not hand the action to teaching. */
    if (k->reason&&!strcmp(k->scope,"all")) { d->feedback.arm[0].control=2; d->feedback.arm[0].teach=1; }

    body=cJSON_CreateObject();
    if (k->device) cJSON_AddItemToObject(body,"device_id",strcmp(k->device,"$")?cJSON_Parse(k->device):cJSON_CreateString(d->id));
    if (k->generation==GENERATION_CURRENT) cJSON_AddNumberToObject(body,"generation",d->generation);
    if (k->generation==GENERATION_STALE) cJSON_AddNumberToObject(body,"generation",d->generation+1);
    if (k->generation==GENERATION_RAW) cJSON_AddItemToObject(body,"generation",cJSON_Parse(k->raw_generation));
    if (k->request==REQUEST_MALFORMED) cJSON_AddStringToObject(body,"request_id","7");
    if (k->request==REQUEST_MATCH) cJSON_AddNumberToObject(body,"request_id",LANE(&c)->request_id);
    if (k->request!=REQUEST_NONE) cJSON_AddStringToObject(body,"owner",k->request==REQUEST_OWNER_ONLY?"another-client":owner);
    const char *intent=k->request==REQUEST_MATCH&&k->device&&!strcmp(k->device,"$")?"cancel":"emergency";
    uint64_t tx=d->tx; status=0; result=controller_action(&c,"/api/stop",body,&status);
    char *sent=cJSON_PrintUnformatted(body),*got=cJSON_PrintUnformatted(result);
    bool honoured=!k->reason;
    bool as_expected=status==k->status&&!strcmp(string(result,"stop_intent"),intent)&&!strcmp(string(result,"stop_scope"),k->scope)&&
        cJSON_IsTrue(cJSON_GetObjectItem(result,"stop_scope_degraded"))==!honoured&&
        cJSON_IsTrue(cJSON_GetObjectItem(result,"ok"))==honoured&&
        (honoured?!cJSON_GetObjectItem(result,"stop_scope_reason"):strstr(string(result,"stop_scope_reason"),k->reason)!=NULL);
    if (!as_expected) fprintf(stderr,"case %u: %s -> %d %s\n",index,sent,status,got);
    CHECK(as_expected);
    /* Whatever the scope, this arm received the STOP frame and is latched. */
    CHECK(d->tx==tx+1&&LANE(&c)->op==OP_IDLE);
    const cJSON *stops=cJSON_GetObjectItem(result,"stops"),*entry=cJSON_GetArrayItem(stops,0);
    CHECK(cJSON_GetArraySize(stops)==1&&!strcmp(string(entry,"device_id"),d->id)&&cJSON_IsTrue(cJSON_GetObjectItem(entry,"ok")));
    /* A degraded reply says why the scope was not honoured and what was stopped. */
    if (!honoured) CHECK(strstr(string(result,"error"),k->reason)&&strstr(string(result,"error"),strcmp(k->scope,"all")?"this arm":"every known arm"));
    cJSON *disk=fixture_read_json(path);
    const cJSON *record=cJSON_GetObjectItem(cJSON_GetObjectItem(disk,"devices"),d->id);
    CHECK(cJSON_IsTrue(cJSON_GetObjectItem(record,"blocked"))&&!strcmp(string(record,"kind"),"stop")&&cJSON_IsTrue(cJSON_GetObjectItem(record,"stop_event_active")));
    cJSON_Delete(disk); free(sent); free(got); cJSON_Delete(body); cJSON_Delete(result);
    fixture_cleanup(&c,directory);
}
int main(void)
{
    const struct stop_case cases[]={
        /* Scope honoured. */
        {"$",GENERATION_CURRENT,NULL,REQUEST_NONE,200,"device",NULL},
        {"$",GENERATION_NONE,NULL,REQUEST_NONE,200,"device",NULL},
        {"$",GENERATION_CURRENT,NULL,REQUEST_MATCH,200,"device",NULL},
        {NULL,GENERATION_NONE,NULL,REQUEST_NONE,200,"all",NULL},
        {"\"\"",GENERATION_NONE,NULL,REQUEST_NONE,200,"all",NULL},
        /* Known arm, stale or malformed scope: that arm stops anyway. */
        {"$",GENERATION_STALE,NULL,REQUEST_NONE,409,"device","generation"},
        {"$",GENERATION_RAW,"null",REQUEST_NONE,409,"device","generation"},
        {"$",GENERATION_RAW,"0",REQUEST_NONE,409,"device","generation"},
        {"$",GENERATION_RAW,"-1",REQUEST_NONE,409,"device","generation"},
        {"$",GENERATION_RAW,"1.5",REQUEST_NONE,409,"device","generation"},
        {"$",GENERATION_RAW,"4294967296",REQUEST_NONE,409,"device","generation"},
        {"$",GENERATION_RAW,"\"2\"",REQUEST_NONE,409,"device","generation"},
        {"$",GENERATION_RAW,"true",REQUEST_NONE,409,"device","generation"},
        /* A current cancel stops its action, even through a stale generation. */
        {"$",GENERATION_STALE,NULL,REQUEST_MATCH,409,"device","generation"},
        /* An emergency STOP from a different owner still stops. */
        {"$",GENERATION_CURRENT,NULL,REQUEST_OWNER_ONLY,200,"device",NULL},
        {NULL,GENERATION_NONE,NULL,REQUEST_OWNER_ONLY,200,"all",NULL},
        /* A cancel with a malformed request_id escalates to an emergency STOP. */
        {"$",GENERATION_CURRENT,NULL,REQUEST_MALFORMED,409,"device","request_id is not a request number"},
        {"$",GENERATION_NONE,NULL,REQUEST_MALFORMED,409,"device","request_id is not a request number"},
        {NULL,GENERATION_NONE,NULL,REQUEST_MALFORMED,409,"all","needs its device_id"},
        /* No arm can be resolved: every arm stops. */
        {"\"simulation:other\"",GENERATION_CURRENT,NULL,REQUEST_NONE,409,"all","not a known CAN adapter"},
        {"7",GENERATION_NONE,NULL,REQUEST_NONE,409,"all","not an adapter identity"},
        {"null",GENERATION_NONE,NULL,REQUEST_NONE,409,"all","not an adapter identity"},
        {"[\"simulation\"]",GENERATION_CURRENT,NULL,REQUEST_NONE,409,"all","not an adapter identity"},
        {"{}",GENERATION_NONE,NULL,REQUEST_MATCH,409,"all","not an adapter identity"},
        {NULL,GENERATION_CURRENT,NULL,REQUEST_NONE,409,"all","needs its device_id"},
        {NULL,GENERATION_NONE,NULL,REQUEST_MATCH,409,"all","needs its device_id"},
        {"\"\"",GENERATION_STALE,NULL,REQUEST_MATCH,409,"all","needs its device_id"},
    };
    for (unsigned i=0;i<sizeof cases/sizeof cases[0];++i) run(&cases[i],i);

    /* Stale cancels: a different request, another owner claiming the running
       request, another owner with an old request, and a finished request on an idle arm. */
    stale_cancel(true,1,owner); stale_cancel(true,0,"another-client"); stale_cancel(true,1,"another-client");
    stale_cancel(false,0,owner);

    /* An idle arm with a feedback gap and a new connection generation still takes a cleanup STOP. */
    char directory[96]; fixture_directory(directory,sizeof directory,"piper-stop-generation");
    struct controller c; CHECK(controller_init(&c,true,directory,""));
    double now=piper_now(); controller_tick(&c,now);
    struct can_device *d=&c.bus.devices[0];
    ++d->generation; d->feedback.arm[0].status_at=now-2; uint64_t tx=d->tx;
    cJSON *body=cJSON_CreateObject(); cJSON_AddStringToObject(body,"device_id",d->id); cJSON_AddNumberToObject(body,"generation",d->generation);
    int status=0; cJSON_Delete(controller_action(&c,"/api/stop",body,&status)); cJSON_Delete(body);
    CHECK(status==200&&d->tx==tx+1);
    fixture_cleanup(&c,directory);
    puts("STOP intent: every emergency STOP body stops (known arm despite stale generation or another owner, unresolvable scope stops every arm, malformed cancel escalates); a current cancel stops its action; a stale cancel sends nothing and leaves an idle arm or another action alone.");
}

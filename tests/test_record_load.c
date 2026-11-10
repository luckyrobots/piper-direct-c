#define _GNU_SOURCE
#include "fixture.h"
#include "fake_bus.h"

/* controller_init reads control-state.json from disk. These records are
   written as files, the way an older build, an editor or a damaged disk would
   leave them, and loaded through the production path. */
static char directory[96],path[160],device[sizeof ((struct can_device *)0)->id];

static void write_state(const char *record)
{
    FILE *f=fopen(path,"wb"); CHECK(f);
    CHECK(fprintf(f,"{\"schema\":1,\"simulated\":true,\"selected\":\"%s\",\"devices\":{\"%s\":%s}}",device,device,record)>0);
    CHECK(!fclose(f));
}
static const char *text(const cJSON *r,const char *key)
{
    const char *value=cJSON_GetStringValue(cJSON_GetObjectItem(r,key)); return value?value:"(missing)";
}
/* Load `record`; it must be accepted and normalize to `state` and `kind`, in
   memory, in the public snapshot and in the file written back. */
static void accepted(const char *record,const char *state,const char *kind)
{
    write_state(record);
    struct controller c; CHECK(controller_init(&c,true,directory,""));
    bool latched=strcmp(state,"clear")!=0;
    for (unsigned pass=0;pass<2;++pass) {
        cJSON *disk=pass?fixture_read_json(path):NULL;
        const cJSON *r=cJSON_GetObjectItem(cJSON_GetObjectItem(pass?disk:c.saved,"devices"),device);
        if (strcmp(text(r,"state"),state)||strcmp(text(r,"kind"),kind))
            fprintf(stderr,"%s -> state %s kind %s\n",record,text(r,"state"),text(r,"kind"));
        CHECK(!strcmp(text(r,"state"),state)&&!strcmp(text(r,"kind"),kind));
        CHECK(cJSON_IsTrue(cJSON_GetObjectItem(r,"blocked"))==latched);
        CHECK(cJSON_IsTrue(cJSON_GetObjectItem(r,"reset_pending"))==!strcmp(state,"reset_pending"));
        cJSON_Delete(disk);
    }
    cJSON *snapshot=controller_state(&c,"record-load");
    const cJSON *published=cJSON_GetArrayItem(cJSON_GetObjectItem(snapshot,"devices"),0);
    CHECK(cJSON_IsTrue(cJSON_GetObjectItem(published,"recovery_required"))==latched);
    CHECK(cJSON_IsTrue(cJSON_GetObjectItem(published,"reset_pending"))==!strcmp(state,"reset_pending"));
    cJSON_Delete(snapshot);
    /* A latched record admits no motion after the restart. */
    if (latched) {
        struct can_device *d=&c.bus.devices[0]; double now=piper_now(); controller_tick(&c,now);
        c.bus.sim_enabled=63; controller_tick(&c,piper_now());
        fixture_configure(d,"S-V1.9-0",-180,180,.3,now); fixture_still(d,now-1);
        cJSON *body=cJSON_CreateObject(); cJSON_AddStringToObject(body,"device_id",d->id);
        cJSON_AddNumberToObject(body,"generation",d->generation); cJSON_AddStringToObject(body,"action","joint");
        cJSON_AddStringToObject(body,"owner","record-load-fixture"); cJSON_AddNumberToObject(body,"joint",6);
        cJSON_AddNumberToObject(body,"degrees",1); cJSON_AddBoolToObject(body,"standalone",true); cJSON_AddBoolToObject(body,"clear",true);
        int status=0; uint64_t tx=d->tx; cJSON_Delete(controller_action(&c,"/api/command",body,&status)); cJSON_Delete(body);
        CHECK(status==409&&d->tx==tx&&LANE(&c)->op==OP_IDLE);
    }
    controller_close(&c);
}
/* Load `record`; the controller must refuse to start and leave the file alone. */
static void refused(const char *record)
{
    write_state(record);
    cJSON *before=fixture_read_json(path);
    struct controller c; bool started=controller_init(&c,true,directory,"");
    if (started) fprintf(stderr,"accepted: %s\n",record);
    CHECK(!started);
    cJSON *after=fixture_read_json(path); CHECK(cJSON_Compare(before,after,true));
    cJSON_Delete(before); cJSON_Delete(after);
}
static void scenarios(void)
{
    fixture_directory(directory,sizeof directory,"piper-record-load");
    snprintf(path,sizeof path,"%s/control-state.json",directory);
    struct controller first; CHECK(controller_init(&first,true,directory,""));
    snprintf(device,sizeof device,"%s",first.bus.devices[0].id); controller_close(&first);

    /* Records from before "state" and "kind" existed: derived once from the booleans and the reason text. */
    accepted("{\"blocked\":false,\"reset_pending\":false,\"reason\":\"\"}","clear","");
    accepted("{\"blocked\":true,\"reset_pending\":false,\"reason\":\"Possible obstruction at J2: no measured progress.\"}","latched","obstruction");
    accepted("{\"blocked\":true,\"reset_pending\":false,\"reason\":\"Control action interrupted before verified completion\"}","latched","intent");
    accepted("{\"blocked\":true,\"reset_pending\":false,\"reason\":\"Motors disabled; recovery verification required\"}","latched","power");
    accepted("{\"blocked\":true,\"reset_pending\":false,\"reason\":\"Control heartbeat expired.\"}","latched","stop");
    accepted("{\"blocked\":true,\"reset_pending\":true,\"reason\":\"Reset pending verification\"}","reset_pending","reset");
    accepted("{\"stop_event_active\":true,\"stop_event\":{\"reason\":\"Operator stop.\"}}","latched","stop");

    /* Every kind this build writes loads unchanged. */
    const char *kinds[]={"intent","stop","obstruction","power"};
    for (unsigned i=0;i<4;++i) {
        char record[256]; snprintf(record,sizeof record,
            "{\"state\":\"latched\",\"kind\":\"%s\",\"reason\":\"r\",\"kind_reason\":\"r\",\"blocked\":true,\"reset_pending\":false}",kinds[i]);
        accepted(record,"latched",kinds[i]);
    }
    accepted("{\"state\":\"reset_pending\",\"kind\":\"reset\",\"reason\":\"r\",\"kind_reason\":\"r\",\"blocked\":true,\"reset_pending\":true}","reset_pending","reset");
    accepted("{\"state\":\"clear\",\"kind\":\"\",\"reason\":\"\",\"kind_reason\":\"\",\"blocked\":false,\"reset_pending\":false}","clear","");

    /* A kind that is not in the known set is not guessed at. */
    const char *tampered[]={"\"recoverable\"","\"Obstruction\"","\"obstruction \"","\" \"","7","null","true","[\"stop\"]"};
    for (unsigned i=0;i<sizeof tampered/sizeof tampered[0];++i) {
        char record[256]; snprintf(record,sizeof record,
            "{\"state\":\"latched\",\"kind\":%s,\"reason\":\"r\",\"kind_reason\":\"r\",\"blocked\":true,\"reset_pending\":false}",tampered[i]);
        refused(record);
    }
    /* Neither is an unknown or mistyped state, or a mistyped legacy boolean. */
    refused("{\"state\":\"released\",\"kind\":\"\",\"blocked\":false,\"reset_pending\":false}");
    refused("{\"state\":\"\",\"blocked\":true,\"reset_pending\":false,\"reason\":\"Operator stop.\"}");
    refused("{\"state\":1,\"blocked\":true,\"reset_pending\":false}");
    refused("{\"state\":\"latched\",\"kind\":\"stop\",\"blocked\":\"true\",\"reset_pending\":false}");
    refused("{\"state\":\"clear\",\"kind\":\"\",\"blocked\":false,\"reset_pending\":false,\"stop_event_active\":1}");

    /* State and booleans disagree (an older build rewrote one side): the
       stricter reading wins and kind is derived again from the reason. */
    accepted("{\"state\":\"clear\",\"kind\":\"\",\"reason\":\"Operator stop.\",\"kind_reason\":\"\",\"blocked\":true,\"reset_pending\":false}","latched","stop");
    accepted("{\"state\":\"latched\",\"kind\":\"obstruction\",\"reason\":\"\",\"kind_reason\":\"\",\"blocked\":false,\"reset_pending\":false}","latched","stop");
    accepted("{\"state\":\"clear\",\"kind\":\"\",\"reason\":\"\",\"kind_reason\":\"\",\"blocked\":false,\"reset_pending\":true}","reset_pending","reset");
    accepted("{\"state\":\"reset_pending\",\"kind\":\"reset\",\"reason\":\"r\",\"kind_reason\":\"r\",\"blocked\":true,\"reset_pending\":false}","reset_pending","reset");
    accepted("{\"state\":\"clear\",\"kind\":\"\",\"reason\":\"\",\"kind_reason\":\"\",\"blocked\":false,\"reset_pending\":false,\"stop_event_active\":true}","latched","stop");
    /* A valid kind whose reason was rewritten by an older build is derived again. */
    accepted("{\"state\":\"latched\",\"kind\":\"obstruction\",\"reason\":\"Control heartbeat expired.\",\"kind_reason\":\"Possible obstruction at J1\",\"blocked\":true,\"reset_pending\":false}","latched","stop");

    CHECK(!unlink(path)); CHECK(!rmdir(directory));
    puts("Record load: legacy, current, tampered-kind, unknown-state and disagreeing records loaded from disk; unknown values refuse startup and disagreement resolves to the stricter latch.");
}
static const struct check_test tests[]={{"record-load","every scenario of this file",scenarios,NULL}};
int main(void) { return check_main("record-load",tests,1); }

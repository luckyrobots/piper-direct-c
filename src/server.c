#define _GNU_SOURCE
#include "controller.h"
#include "camera.h"
#include <arpa/inet.h>
#include <ctype.h>
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/file.h>
#include <sys/random.h>
#include <sys/socket.h>
#include <unistd.h>

#define CLIENTS 16
#define REQUEST_MAX 16384
struct client { int fd; char input[REQUEST_MAX+1]; size_t used, sent, length; char *output; double deadline; };
struct asset { const char *route,*name,*mime; char *data; size_t size; };
static struct asset assets[]={
    {"/","index.html","text/html; charset=utf-8",NULL,0},
    {"/app.js","app.js","text/javascript; charset=utf-8",NULL,0},
    {"/style.css","style.css","text/css; charset=utf-8",NULL,0},
    {"/api/openapi.json","openapi.json","application/json",NULL,0}
};
static volatile sig_atomic_t exiting;
static void on_signal(int number) { (void)number; exiting=1; }
static void close_client(struct client *c)
{
    if (c->fd>=0) close(c->fd);
    free(c->output); memset(c,0,sizeof *c); c->fd=-1;
}
static void respond(struct client *c,int status,const char *mime,const char *data,size_t size)
{
    const char *reason=status==200?"OK":status==202?"Accepted":status==400?"Bad Request":status==403?"Forbidden":status==404?"Not Found":status==409?"Conflict":status==413?"Payload Too Large":"Server Error";
    const char *format="HTTP/1.1 %d %s\r\nContent-Type: %s\r\nContent-Length: %zu\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'self'; style-src 'self'; script-src 'self'; connect-src 'self'; img-src 'self' data: blob:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'\r\n\r\n";
    size_t capacity=size+1024; c->output=malloc(capacity);
    if (!c->output) { close_client(c); return; }
    int n=snprintf(c->output,capacity,format,status,reason,mime,size);
    if (n<0||(size_t)n+size>=capacity) { close_client(c); return; }
    memcpy(c->output+n,data,size); c->length=(size_t)n+size;
}
static void json_response(struct client *c,int status,cJSON *body)
{
    char *data=cJSON_PrintUnformatted(body); cJSON_Delete(body);
    if (!data) { close_client(c); return; }
    respond(c,status,"application/json",data,strlen(data)); free(data);
}
static void error_response(struct client *c,int status,const char *message)
{
    cJSON *j=cJSON_CreateObject(); cJSON_AddBoolToObject(j,"ok",false); cJSON_AddStringToObject(j,"error",message); json_response(c,status,j);
}
static bool unique_keys(const cJSON *j)
{
    for (const cJSON *a=j->child;a;a=a->next) {
        if (!unique_keys(a)) return false;
        if (cJSON_IsObject(j)) for (const cJSON *b=a->next;b;b=b->next) if (!strcmp(a->string,b->string)) return false;
    }
    return true;
}
static void request(struct client *client,struct controller *controller,const char *token,unsigned port)
{
    char *end=strstr(client->input,"\r\n\r\n"); if (!end) return;
    size_t header_size=(size_t)(end-client->input)+4;
    char headers[REQUEST_MAX+1]; memcpy(headers,client->input,header_size); headers[header_size]=0;
    char *save=NULL,*line=strtok_r(headers,"\r\n",&save), method[8],route[128],version[16],extra;
    if (!line||sscanf(line,"%7s %127s %15s %c",method,route,version,&extra)!=3||strcmp(version,"HTTP/1.1")) { error_response(client,400,"Use HTTP/1.1."); return; }
    const char *host=NULL,*origin=NULL,*auth=NULL,*type=NULL,*fetch=NULL;
    bool content_length=false; size_t length=0;
    while ((line=strtok_r(NULL,"\r\n",&save))) {
        char *colon=strchr(line,':'); if (!colon||isspace((unsigned char)*line)) { error_response(client,400,"Malformed header."); return; }
        *colon=0; char *value=colon+1; while (*value==' '||*value=='\t') ++value;
        char *tail=value+strlen(value); while (tail>value&&(tail[-1]==' '||tail[-1]=='\t')) *--tail=0;
        const char **slot=NULL;
        if (!strcasecmp(line,"Host")) slot=&host;
        else if (!strcasecmp(line,"Origin")) slot=&origin;
        else if (!strcasecmp(line,"X-Piper-Token")) slot=&auth;
        else if (!strcasecmp(line,"Content-Type")) slot=&type;
        else if (!strcasecmp(line,"Sec-Fetch-Site")) slot=&fetch;
        else if (!strcasecmp(line,"Transfer-Encoding")) { error_response(client,400,"Chunked requests are unsupported."); return; }
        else if (!strcasecmp(line,"Content-Length")) {
            if (content_length||!*value) { error_response(client,400,"Invalid Content-Length."); return; }
            for (const char *p=value;*p;++p) if (!isdigit((unsigned char)*p)) { error_response(client,400,"Invalid Content-Length."); return; }
            errno=0; unsigned long long n=strtoull(value,NULL,10);
            if (errno||n>REQUEST_MAX-header_size) { error_response(client,413,"Request too large."); return; }
            length=(size_t)n; content_length=true;
        }
        if (slot) { if (*slot) { error_response(client,400,"Duplicate request header."); return; } *slot=value; }
    }
    char host1[64],host2[64],origin1[80],origin2[80];
    snprintf(host1,sizeof host1,"127.0.0.1:%u",port); snprintf(host2,sizeof host2,"localhost:%u",port);
    snprintf(origin1,sizeof origin1,"http://%s",host1); snprintf(origin2,sizeof origin2,"http://%s",host2);
    if (!host||(strcmp(host,host1)&&strcmp(host,host2))||(origin&&strcmp(origin,origin1)&&strcmp(origin,origin2))||(fetch&&!strcmp(fetch,"cross-site"))) { error_response(client,403,"Only this local dashboard origin is permitted."); return; }
    if (client->used<header_size+length) return;
    if (client->used!=header_size+length) { error_response(client,400,"One request per connection is required."); return; }
    if (!strcmp(method,"GET")) {
        if (length) { error_response(client,400,"GET body is unsupported."); return; }
        if (!strcmp(route,"/api/state")) {
            cJSON *state=controller_state(controller,token); cJSON_AddItemToObject(state,"camera",camera_state(piper_now()));
            json_response(client,200,state); return;
        }
        if (!strcmp(route,"/api/camera/frame")) {
            size_t size; const unsigned char *jpeg=camera_frame(piper_now(),&size);
            if (jpeg) respond(client,200,"image/jpeg",(const char *)jpeg,size);
            else error_response(client,409,"No fresh camera frame.");
            return;
        }
        for (size_t i=0;i<sizeof assets/sizeof *assets;++i) if (!strcmp(route,assets[i].route)) { respond(client,200,assets[i].mime,assets[i].data,assets[i].size); return; }
        error_response(client,404,"Not found."); return;
    }
    if (strcmp(method,"POST")||!auth||strcmp(auth,token)) { error_response(client,403,"POST requires X-Piper-Token from /api/state."); return; }
    if (!content_length||!type||(strcmp(type,"application/json")&&strcmp(type,"application/json; charset=utf-8"))) { error_response(client,400,"Use a JSON body and Content-Length."); return; }
    /* This API needs numbers, booleans and opaque ASCII IDs. Reject JSON unicode
       escapes to avoid embedded-NUL string truncation and ambiguous keys in C. */
    if (memchr(client->input+header_size,0,length)||strstr(client->input+header_size,"\\u")) { error_response(client,400,"Unicode escapes are unsupported in control requests."); return; }
    cJSON *body=cJSON_ParseWithLengthOpts(client->input+header_size,length+1,NULL,1);
    if (!cJSON_IsObject(body)||!unique_keys(body)) { cJSON_Delete(body); error_response(client,400,"Expected one JSON object with unique keys."); return; }
    controller_tick(controller,piper_now());
    if (!strcmp(route,"/api/camera")) {
        cJSON *enabled=cJSON_GetObjectItemCaseSensitive(body,"enabled");
        if (!cJSON_IsBool(enabled)) error_response(client,400,"enabled must be true or false.");
        else {
            const char *why=NULL;
            if (cJSON_IsTrue(enabled)) why=camera_start(); else camera_stop();
            if (why) error_response(client,409,why); else json_response(client,200,camera_state(piper_now()));
        }
        cJSON_Delete(body); return;
    }
    int status=500; cJSON *result=controller_action(controller,route,body,&status); cJSON_Delete(body); json_response(client,status,result);
}
static bool load_assets(const char *directory)
{
    for (size_t i=0;i<sizeof assets/sizeof *assets;++i) {
        char path[1024]; snprintf(path,sizeof path,"%s/%s",directory,assets[i].name);
        FILE *f=fopen(path,"rb"); if (!f) { perror(path); return false; }
        if (fseek(f,0,SEEK_END)) { fclose(f); return false; }
        long n=ftell(f); if (n<0||n>1024*1024) { fclose(f); return false; }
        rewind(f); assets[i].data=malloc((size_t)n+1);
        if (!assets[i].data) { fclose(f); return false; }
        assets[i].size=fread(assets[i].data,1,(size_t)n,f); fclose(f);
        if (assets[i].size!=(size_t)n) return false;
    }
    return true;
}
static void usage(void)
{
    puts("piper-control [--port 8765] [--state-dir data] [--web-root web]\n"
         "              [--legacy-dir /path/to/old/dashboard/data] [--simulate]\n"
         "              [--camera-device /dev/v4l/by-id/WEBCAM-video-index0]\n"
         "Native C / SocketCAN controller. Binds only 127.0.0.1.\n"
         "Simulation never opens a real CAN socket. Use a separate state directory.\n"
         "Start-up only monitors: no enable, motion or recovery commands are sent.");
}
int main(int argc,char **argv)
{
    bool simulated=false; unsigned port=8765; const char *directory="data",*legacy="",*web="web",*camera_device="";
    for (int i=1;i<argc;++i) {
        const char *arg=argv[i];
        if (!strcmp(arg,"--help")) { usage(); return 0; }
        if (!strcmp(arg,"--simulate")) { simulated=true; continue; }
        if (i+1>=argc) { usage(); return 2; }
        const char *value=argv[++i];
        if (!strcmp(arg,"--port")) { char *end=NULL; unsigned long n=strtoul(value,&end,10); if (!*value||*end||n<1024||n>65535) return 2; port=(unsigned)n; }
        else if (!strcmp(arg,"--state-dir")) directory=value;
        else if (!strcmp(arg,"--legacy-dir")) legacy=value;
        else if (!strcmp(arg,"--web-root")) web=value;
        else if (!strcmp(arg,"--camera-device")) camera_device=value;
        else { usage(); return 2; }
    }
    if (!load_assets(web)||!camera_init(simulated?"":camera_device)) return 1;
    char lockpath[512];
    if (simulated) snprintf(lockpath,sizeof lockpath,"/tmp/piper-control-%lu-simulation-%u.lock",(unsigned long)getuid(),port);
    else snprintf(lockpath,sizeof lockpath,"/tmp/piper-control-%lu.lock",(unsigned long)getuid());
    int lock=open(lockpath,O_RDWR|O_CREAT|O_CLOEXEC|O_NOFOLLOW,0600);
    if (lock<0||flock(lock,LOCK_EX|LOCK_NB)) { fputs("Another C controller is running, or its lock is unavailable.\n",stderr); return 1; }
    unsigned char random[32]; if (getrandom(random,sizeof random,0)!=(ssize_t)sizeof random) return 1;
    char token[65]; for (unsigned i=0;i<32;++i) snprintf(token+i*2,3,"%02x",random[i]);
    int server=socket(AF_INET,SOCK_STREAM|SOCK_NONBLOCK|SOCK_CLOEXEC,0); if (server<0) return 1;
    int one=1; setsockopt(server,SOL_SOCKET,SO_REUSEADDR,&one,sizeof one);
    struct sockaddr_in address={.sin_family=AF_INET,.sin_port=htons((uint16_t)port),.sin_addr={.s_addr=htonl(INADDR_LOOPBACK)}};
    if (bind(server,(struct sockaddr *)&address,sizeof address)||listen(server,32)) { perror("Local HTTP listener"); return 1; }
    struct controller controller;
    if (!controller_init(&controller,simulated,directory,legacy)) { fputs("Cannot initialize controller: check state files and directory permissions. No commands sent.\n",stderr); return 1; }
    signal(SIGINT,on_signal); signal(SIGTERM,on_signal); signal(SIGPIPE,SIG_IGN);
    struct client clients[CLIENTS]={0}; for (unsigned i=0;i<CLIENTS;++i) clients[i].fd=-1;
    fprintf(stderr,"PiPer Direct C: http://127.0.0.1:%u/ [%s]\n",port,simulated?"SIMULATION — no hardware":"HARDWARE — monitoring");
    double last_scan=piper_now();
    while (!exiting) {
        double now=piper_now();
        if (now-last_scan>=1) { can_bus_scan(&controller.bus); last_scan=now; }
        controller_tick(&controller,now);
        camera_tick(now);
        struct pollfd fds[CLIENTS+1]={{.fd=server,.events=POLLIN}};
        for (unsigned i=0;i<CLIENTS;++i) fds[i+1]=(struct pollfd){.fd=clients[i].fd,.events=clients[i].output?POLLOUT:POLLIN};
        if (poll(fds,CLIENTS+1,10)<0&&errno!=EINTR) break;
        if (fds[0].revents&POLLIN) for (unsigned n=0;n<CLIENTS;++n) {
            int fd=accept4(server,NULL,NULL,SOCK_NONBLOCK|SOCK_CLOEXEC); if (fd<0) break;
            unsigned i=0; while (i<CLIENTS&&clients[i].fd>=0) ++i;
            if (i==CLIENTS) { close(fd); break; }
            clients[i].fd=fd; clients[i].deadline=piper_now()+2;
        }
        for (unsigned i=0;i<CLIENTS;++i) {
            struct client *c=&clients[i]; if (c->fd<0) continue;
            if (piper_now()>c->deadline||(fds[i+1].revents&(POLLERR|POLLNVAL))) { close_client(c); continue; }
            if (!c->output&&(fds[i+1].revents&POLLIN)) {
                ssize_t n=recv(c->fd,c->input+c->used,REQUEST_MAX-c->used,MSG_DONTWAIT);
                if (n<=0) { if (!n||(errno!=EAGAIN&&errno!=EINTR)) close_client(c); continue; }
                c->used+=(size_t)n; c->input[c->used]=0;
                if (memchr(c->input,0,c->used)) error_response(c,400,"NUL bytes are unsupported.");
                else request(c,&controller,token,port);
                if (c->fd>=0&&!c->output&&c->used==REQUEST_MAX) error_response(c,413,"Request too large.");
            }
            if (c->fd>=0&&c->output) {
                ssize_t n=send(c->fd,c->output+c->sent,c->length-c->sent,MSG_DONTWAIT|MSG_NOSIGNAL);
                if (n>0) c->sent+=(size_t)n;
                else if (n<0&&errno!=EAGAIN&&errno!=EINTR) { close_client(c); continue; }
                if (c->sent==c->length) close_client(c);
            }
        }
    }
    controller_close(&controller);
    camera_close();
    for (unsigned i=0;i<CLIENTS;++i) close_client(&clients[i]);
    for (size_t i=0;i<sizeof assets/sizeof *assets;++i) free(assets[i].data);
    close(server); close(lock); return 0;
}

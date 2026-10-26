#define _GNU_SOURCE
#include "canbus.h"
#include <errno.h>
#include <linux/can/raw.h>
#include <poll.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>
#include <math.h>

/* Passive wire observation only: never calls send(), can_bus_send(), or changes
   a CAN interface. Local echoes identify host-origin frames, not MCU acceptance. */
static volatile sig_atomic_t interrupted;
static void stop(int sig) { (void)sig; interrupted=1; }
int main(int argc,char **argv)
{
    const char *id=NULL,*path=NULL; double seconds=180;
    for (int i=1;i<argc;++i) {
        if (!strcmp(argv[i],"--help")) { puts("piper-trace --device usb:SERIAL --out NEW.jsonl [--seconds 180]\nPassive host/remote CAN command trace; no robot commands sent."); return 0; }
        if (i+1>=argc) return 1;
        if (!strcmp(argv[i],"--device")) id=argv[++i];
        else if (!strcmp(argv[i],"--out")) path=argv[++i];
        else if (!strcmp(argv[i],"--seconds")) {
            char *end; errno=0; seconds=strtod(argv[++i],&end);
            if (errno||*end||!isfinite(seconds)||seconds<.2||seconds>600) return 1;
        } else return 1;
    }
    if (!id||!path||strncmp(id,"usb:",4)) return 1;
    struct can_bus bus; can_bus_init(&bus,false); can_bus_scan(&bus);
    struct can_device *d=can_bus_find(&bus,id);
    if (!d||!d->present||!d->stable||!d->up||d->fd<0) { fprintf(stderr,"Selected stable adapter unavailable\n"); can_bus_close(&bus); return 1; }
    const unsigned ids[]={0x150,0x151,0x155,0x156,0x157,0x159,0x471,0x472,0x477,0x4af};
    enum { FILTERS=sizeof ids/sizeof ids[0] };
    struct can_filter filter[FILTERS];
    for (unsigned i=0;i<FILTERS;++i) filter[i]=(struct can_filter){.can_id=ids[i],.can_mask=CAN_SFF_MASK|CAN_EFF_FLAG|CAN_RTR_FLAG};
    int yes=1;
    if (setsockopt(d->fd,SOL_CAN_RAW,CAN_RAW_FILTER,filter,sizeof filter)<0||setsockopt(d->fd,SOL_SOCKET,SO_RXQ_OVFL,&yes,sizeof yes)<0) { perror("trace filter"); can_bus_close(&bus); return 1; }
    for (unsigned i=0;i<CANBUS_MAX;++i) if (&bus.devices[i]!=d&&bus.devices[i].fd>=0) { close(bus.devices[i].fd); bus.devices[i].fd=-1; }
    FILE *out=fopen(path,"wx"); if (!out) { perror(path); can_bus_close(&bus); return 1; }
    signal(SIGINT,stop); signal(SIGTERM,stop);
    double start=piper_now(); unsigned count=0,dropped=0; bool ok=true;
    fprintf(out,"{\"type\":\"header\",\"schema\":1,\"device_id\":\"%s\",\"interface\":\"%s\",\"host_monotonic_start_s\":%.9f,\"passive\":true,\"semantics\":\"Host local CAN echoes are issued frames, not acknowledged execution. Timestamp is host receive time.\"}\n",id,d->name,start);
    while (!interrupted&&piper_now()-start<seconds&&ok) {
        if (if_nametoindex(d->name)!=d->index) { ok=false; break; }
        struct pollfd p={.fd=d->fd,.events=POLLIN}; int ready=poll(&p,1,50);
        if (ready<0&&errno==EINTR) continue;
        if (ready<0||p.revents&(POLLERR|POLLHUP|POLLNVAL)) { ok=false; break; }
        if (!ready) continue;
        for (;;) {
            struct can_frame frame; struct iovec io={.iov_base=&frame,.iov_len=sizeof frame};
            char control[CMSG_SPACE(sizeof(unsigned))];
            struct msghdr msg={.msg_iov=&io,.msg_iovlen=1,.msg_control=control,.msg_controllen=sizeof control};
            ssize_t n=recvmsg(d->fd,&msg,MSG_DONTWAIT);
            if (n<0&&(errno==EAGAIN||errno==EWOULDBLOCK)) break;
            if (n<0&&errno==EINTR) continue;
            if (n!=(ssize_t)sizeof frame||msg.msg_flags&(MSG_TRUNC|MSG_CTRUNC)||frame.can_id&CAN_ERR_FLAG) { ok=false; break; }
            for (struct cmsghdr *c=CMSG_FIRSTHDR(&msg);c;c=CMSG_NXTHDR(&msg,c))
                if (c->cmsg_level==SOL_SOCKET&&c->cmsg_type==SO_RXQ_OVFL&&c->cmsg_len>=CMSG_LEN(sizeof dropped)) memcpy(&dropped,CMSG_DATA(c),sizeof dropped);
            if (dropped) { ok=false; break; }
            bool selected=false; for (unsigned i=0;i<FILTERS;++i) if (frame.can_id==ids[i]) selected=true;
            if (!selected) continue; /* Frames queued before filtering may include telemetry. */
            if (frame.len!=8) { ok=false; break; }
            fprintf(out,"{\"type\":\"frame\",\"index\":%u,\"host_monotonic_s\":%.9f,\"host_origin\":%s,\"id\":%u,\"data\":\"",count++,piper_now(),msg.msg_flags&MSG_DONTROUTE?"true":"false",frame.can_id);
            for (unsigned i=0;i<8;++i) fprintf(out,"%02x",frame.data[i]);
            fprintf(out,"\"}\n");
            if (ferror(out)) { ok=false; break; }
        }
        if (fflush(out)) ok=false;
    }
    bool complete=ok&&!interrupted;
    fprintf(out,"{\"type\":\"footer\",\"capture_complete\":%s,\"frames\":%u,\"dropped\":%u,\"interrupted\":%s}\n",complete?"true":"false",count,dropped,interrupted?"true":"false");
    if (fflush(out)||fsync(fileno(out))||fclose(out)) complete=false;
    can_bus_close(&bus); return complete?0:2;
}

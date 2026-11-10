#define _GNU_SOURCE
#include "canbus.h"
#include <math.h>
#include <errno.h>
#ifndef _WIN32
#include <linux/can/raw.h>
#include <linux/can/error.h>
#include <linux/can/netlink.h>
#include <linux/rtnetlink.h>
#include <poll.h>
#include <net/if_arp.h>
#endif
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifndef _WIN32
#include <sys/socket.h>
#endif
#include <time.h>
#include <unistd.h>

/* One loss path for both transports: close the platform handle, then drop
   every connection-scoped fact (feedback, stillness, link verification). */
static void transport_close(struct can_device *d);
static void lost(struct can_device *d,const char *why);

#ifdef _WIN32
#include "canbus_windows.inc"
#else

/* A failed monotonic clock is latched for the life of the process and time
   stays at the last good reading. The controller stops every action and
   refuses new ones while piper_clock_failed() is true; nothing aborts. */
static bool clock_failed;
double piper_now(void)
{
    static double last;
    struct timespec t;
    if (clock_failed||clock_gettime(CLOCK_MONOTONIC,&t)<0) { clock_failed=true; return last; }
    return last=(double)t.tv_sec+(double)t.tv_nsec/1e9;
}
bool piper_clock_failed(void) { return clock_failed; }
static bool number(const char *name,const char *field,unsigned long *n)
{
    char path[256],text[80],*end;
    snprintf(path,sizeof path,"/sys/class/net/%s/%s",name,field);
    FILE *f=fopen(path,"r"); if (!f) return false;
    bool ok=fgets(text,sizeof text,f)!=NULL; fclose(f); if (!ok) return false;
    errno=0; *n=strtoul(text,&end,0); return errno==0 && end!=text;
}
/* present/up are re-read from sysfs by every scan, so loss leaves them. */
static void transport_close(struct can_device *d)
{
    if (d->fd>=0) close(d->fd);
}
static struct rtattr *attribute(struct rtattr *a,int length,unsigned type)
{
    for (;RTA_OK(a,length);a=RTA_NEXT(a,length)) if ((a->rta_type&NLA_TYPE_MASK)==type) return a;
    return NULL;
}
static void link_info(struct can_device *d)
{
    d->link_verified=false;
    number(d->name,"statistics/rx_packets",&d->kernel_rx);
    number(d->name,"statistics/tx_packets",&d->kernel_tx);
    int fd=socket(AF_NETLINK,SOCK_RAW|SOCK_CLOEXEC|SOCK_NONBLOCK,NETLINK_ROUTE); if (fd<0) return;
    struct { struct nlmsghdr h; struct ifinfomsg i; } request={
        .h={.nlmsg_len=NLMSG_LENGTH(sizeof(struct ifinfomsg)),.nlmsg_type=RTM_GETLINK,.nlmsg_flags=NLM_F_REQUEST,.nlmsg_seq=1},
        .i={.ifi_family=AF_UNSPEC,.ifi_index=(int)d->index}
    };
    struct sockaddr_nl kernel={.nl_family=AF_NETLINK};
    if (sendto(fd,&request,sizeof request,0,(struct sockaddr *)&kernel,sizeof kernel)<0) { close(fd); return; }
    struct pollfd ready={.fd=fd,.events=POLLIN};
    if (poll(&ready,1,10)<=0) { close(fd); return; }
    _Alignas(struct nlmsghdr) char buffer[16384]; ssize_t size=recv(fd,buffer,sizeof buffer,MSG_DONTWAIT); close(fd);
    if (size<=0) return;
    int remaining=(int)size;
    for (struct nlmsghdr *h=(struct nlmsghdr *)buffer;NLMSG_OK(h,remaining);h=NLMSG_NEXT(h,remaining)) {
        if (h->nlmsg_type!=RTM_NEWLINK||h->nlmsg_len<NLMSG_LENGTH(sizeof(struct ifinfomsg))) continue;
        struct ifinfomsg *info=NLMSG_DATA(h); if (info->ifi_index!=(int)d->index) continue;
        struct rtattr *link=attribute(IFLA_RTA(info),IFLA_PAYLOAD(h),IFLA_LINKINFO); if (!link) continue;
        struct rtattr *data=attribute(RTA_DATA(link),RTA_PAYLOAD(link),IFLA_INFO_DATA); if (!data) continue;
        struct rtattr *timing=attribute(RTA_DATA(data),RTA_PAYLOAD(data),IFLA_CAN_BITTIMING);
        struct rtattr *state=attribute(RTA_DATA(data),RTA_PAYLOAD(data),IFLA_CAN_STATE);
        struct rtattr *mode=attribute(RTA_DATA(data),RTA_PAYLOAD(data),IFLA_CAN_CTRLMODE);
        if (!timing||RTA_PAYLOAD(timing)<sizeof(struct can_bittiming)||!state||RTA_PAYLOAD(state)<sizeof(uint32_t)||!mode||RTA_PAYLOAD(mode)<sizeof(struct can_ctrlmode)) continue;
        struct can_bittiming t; struct can_ctrlmode m; uint32_t s;
        memcpy(&t,RTA_DATA(timing),sizeof t); memcpy(&s,RTA_DATA(state),sizeof s); memcpy(&m,RTA_DATA(mode),sizeof m);
        d->bitrate=t.bitrate; d->bus_state=s; d->control_flags=m.flags; d->link_verified=true;
    }
}
static void identity(struct can_device *d,const char *name)
{
    char link[256],path[4096];
    snprintf(d->name,sizeof d->name,"%s",name);
    snprintf(d->id,sizeof d->id,"interface:%s",name); d->stable=false;
    snprintf(link,sizeof link,"/sys/class/net/%s/device",name);
    if (!realpath(link,path)) return;
    snprintf(d->path,sizeof d->path,"%.511s",path);
    for (unsigned i=0;i<5;++i) {
        char filename[4200],serial[128];
        snprintf(filename,sizeof filename,"%s/serial",path);
        FILE *f=fopen(filename,"r");
        if (f) {
            if (fgets(serial,sizeof serial,f)) {
                serial[strcspn(serial,"\r\n")]=0;
                bool valid=*serial!=0;
                for (char *p=serial;*p;++p) if (!((*p>='0'&&*p<='9')||(*p>='A'&&*p<='Z')||(*p>='a'&&*p<='z')||*p=='-'||*p=='_')) valid=false;
                if (valid) { snprintf(d->id,sizeof d->id,"usb:%s",serial); d->stable=true; }
            }
            fclose(f); break;
        }
        char *slash=strrchr(path,'/'); if (!slash||slash==path) break; *slash=0;
    }
}
#endif
static void lost(struct can_device *d,const char *why)
{
    transport_close(d);
    d->fd=-1; d->link_verified=false;
    memset(&d->feedback,0,sizeof d->feedback); can_bus_forget_stationary(d);
    snprintf(d->problem,sizeof d->problem,"%s",why);
}
void can_bus_forget_stationary(struct can_device *d)
{
    d->stationary_phase=STATIONARY_UNKNOWN; d->stationary_since=d->pose_stationary_since=0;
    d->stationary_count=0; d->stationary_sample_at=0;
}
static void stationary_enter(struct can_device *d,enum stationary_phase phase,double now)
{
    if (phase<STATIONARY_POSE_QUIET) d->pose_stationary_since=0;
    else if (d->stationary_phase<STATIONARY_POSE_QUIET) d->pose_stationary_since=now;
    d->stationary_since=phase==STATIONARY_STRICT_STILL?(d->stationary_phase==STATIONARY_STRICT_STILL?d->stationary_since:now):0;
    d->stationary_phase=phase;
}
static double *simulation_targets(struct can_bus *b,struct can_device *d) { return d==&b->devices[0]?b->sim_target:d->sim_target; }
static double *simulation_gripper(struct can_bus *b,struct can_device *d) { return d==&b->devices[0]?&b->sim_gripper:&d->sim_gripper; }
static double *simulation_last(struct can_bus *b,struct can_device *d) { return d==&b->devices[0]?&b->sim_last:&d->sim_last; }
static unsigned *simulation_enabled(struct can_bus *b,struct can_device *d) { return d==&b->devices[0]?&b->sim_enabled:&d->sim_enabled; }
bool can_bus_init_simulated(struct can_bus *b,unsigned count)
{
    if (count<1||count>CANBUS_MAX) return false;
    memset(b,0,sizeof *b);b->simulated=true;
    for (unsigned i=0;i<CANBUS_MAX;++i) b->devices[i].fd=-1;
    for (unsigned i=0;i<count;++i) {
        struct can_device *d=&b->devices[i];
        if (!i) snprintf(d->id,sizeof d->id,"simulation:one");
        else snprintf(d->id,sizeof d->id,"simulation:%u",i+1);
        snprintf(d->name,sizeof d->name,"sim%u",i);
        if (!i) snprintf(d->label,sizeof d->label,"Simulated PiPer");
        else snprintf(d->label,sizeof d->label,"Simulated PiPer %u",i+1);
        d->present=d->up=d->stable=d->simulated=true;d->generation=++b->sequence;
        d->bitrate=1000000;d->link_verified=true;
        const double initial[6]={0,45,-65,0,10,0};
        memcpy(simulation_targets(b,d),initial,sizeof initial);
        memcpy(d->feedback.arm[0].deg,initial,sizeof initial);
        *simulation_gripper(b,d)=25;
    }
    return true;
}
void can_bus_init(struct can_bus *b,bool simulated)
{
    if (simulated) { (void)can_bus_init_simulated(b,1);return; }
    memset(b,0,sizeof *b);
    for (unsigned i=0;i<CANBUS_MAX;++i) b->devices[i].fd=-1;
}
void can_bus_scan(struct can_bus *b)
{
    if (b->simulated) return;
#ifdef _WIN32
    windows_scan(b);
#else
    for (unsigned i=0;i<CANBUS_MAX;++i) b->devices[i].present=false;
    struct if_nameindex *names=if_nameindex(); if (!names) return;
    for (struct if_nameindex *p=names;p->if_index;++p) {
        unsigned long type,flags;
        if (!number(p->if_name,"type",&type)||type!=ARPHRD_CAN) continue;
        struct can_device found={.fd=-1}; identity(&found,p->if_name);
        struct can_device *d=NULL;
        for (unsigned i=0;i<CANBUS_MAX;++i) if (!strcmp(b->devices[i].id,found.id)) { d=&b->devices[i]; break; }
        if (!d) for (unsigned i=0;i<CANBUS_MAX;++i) if (!b->devices[i].id[0]) { d=&b->devices[i]; *d=found; break; }
        if (!d) continue;
        if (d->present) { lost(d,"Duplicate USB identity; control is blocked"); d->stable=false; continue; }
        d->present=true;
        d->stable=found.stable; snprintf(d->path,sizeof d->path,"%s",found.path);
        d->up=number(p->if_name,"flags",&flags)&&(flags&IFF_UP);
        if (d->index!=p->if_index||strcmp(d->name,p->if_name)) {
            lost(d,"Adapter connection changed"); d->index=p->if_index;
            snprintf(d->name,sizeof d->name,"%s",p->if_name);
        }
        if (!d->up) { lost(d,"CAN interface is down; activate it at 1 Mbit/s"); continue; }
        link_info(d);
        if (d->fd>=0) continue;
        int fd=socket(PF_CAN,SOCK_RAW|SOCK_NONBLOCK|SOCK_CLOEXEC,CAN_RAW);
        can_err_mask_t mask=CAN_ERR_MASK;
        struct sockaddr_can address={.can_family=AF_CAN,.can_ifindex=(int)d->index};
        if (fd<0||setsockopt(fd,SOL_CAN_RAW,CAN_RAW_ERR_FILTER,&mask,sizeof mask)<0||
            bind(fd,(struct sockaddr *)&address,sizeof address)<0||if_nametoindex(d->name)!=d->index) {
            int e=errno; if (fd>=0) close(fd); snprintf(d->problem,sizeof d->problem,"%s",strerror(e)); continue;
        }
        d->fd=fd; d->generation=++b->sequence; d->problem[0]=0;
        memset(&d->feedback,0,sizeof d->feedback); can_bus_forget_stationary(d);
    }
    if_freenameindex(names);
    for (unsigned i=0;i<CANBUS_MAX;++i) if (b->devices[i].id[0]&&!b->devices[i].present)
        lost(&b->devices[i],"USB adapter missing");
#endif
}
void can_bus_invalidate_configuration(struct can_device *d,double now)
{
    struct piper_feedback *feedback=&d->feedback;
    d->configuration_since=now;
    feedback->firmware_len=0; feedback->version[0]=0;
    feedback->hardware[0]=0; feedback->build[0]=0;
    memset(feedback->limit,0,sizeof feedback->limit);
    memset(feedback->acceleration,0,sizeof feedback->acceleration);
    feedback->gripper_config_seen=false;
}
static void update_stationary(struct can_device *d,double now)
{
    const struct piper_sample *s=&d->feedback.arm[0];
    if (!piper_fresh(s,now)) { can_bus_forget_stationary(d); return; }
    double sample_at=s->joint_at[0];
    for (unsigned i=0;i<6;++i) {
        if (!isfinite(s->deg[i])) { can_bus_forget_stationary(d); return; }
        sample_at=fmin(sample_at,s->joint_at[i]);
    }
    bool reset=d->stationary_generation!=d->generation||!d->stationary_count||
        now-d->frame_at>PIPER_FRESH_SECONDS;
    /* A repeated status/motor packet is not a new six-joint observation. */
    if (!reset&&(sample_at<=d->stationary_sample_at||now-d->frame_at<.04)) return;
    if (reset) {
        d->stationary_count=d->stationary_next=0; d->stationary_generation=d->generation;
        stationary_enter(d,STATIONARY_MOVING,now);
        memset(d->stationary_encoder_seen,0,sizeof d->stationary_encoder_seen);
        memcpy(d->last_angles,s->deg,sizeof d->last_angles);
    }
    d->stationary_sample_at=sample_at; d->frame_at=now;
    memcpy(d->stationary_samples[d->stationary_next],s->deg,sizeof s->deg);
    for (unsigned i=0;i<6;++i) {
        const struct piper_motor *m=&d->feedback.motor[i];
        d->stationary_velocities[d->stationary_next][i]=m->high_seen&&piper_recent(m->high_at,now)?m->velocity:0;
        d->stationary_encoders[d->stationary_next][i]=m->high_seen&&piper_recent(m->high_at,now)?m->position_raw:NAN;
    }
    d->stationary_next=(d->stationary_next+1)%CANBUS_STATIONARY_SAMPLES;
    if (d->stationary_count<CANBUS_STATIONARY_SAMPLES) ++d->stationary_count;
    bool quiet=d->stationary_count==CANBUS_STATIONARY_SAMPLES, moving=false, velocity_quiet=true, velocity_moving=false;
    double filtered[6]={0}, filtered_encoder[6]={0}; unsigned encoder_count[6]={0};
    unsigned first=d->stationary_count==CANBUS_STATIONARY_SAMPLES?d->stationary_next:0;
    for (unsigned i=0;i<6;++i) {
        double minimum=s->deg[i],maximum=s->deg[i],early=0,late=0;
        double encoder_min=INFINITY,encoder_max=-INFINITY,encoder_early=0,encoder_late=0;
        unsigned moving_velocities=0; bool invalid_velocity=false;
        for (unsigned n=0;n<d->stationary_count;++n) {
            double position=d->stationary_samples[(first+n)%CANBUS_STATIONARY_SAMPLES][i];
            filtered[i]+=position/d->stationary_count;
            minimum=fmin(minimum,position); maximum=fmax(maximum,position);
            if (n<2) early+=position/2;
            if (n+2>=d->stationary_count) late+=position/2;
            double velocity=d->stationary_velocities[(first+n)%CANBUS_STATIONARY_SAMPLES][i];
            if (!isfinite(velocity)) invalid_velocity=true;
            else if (fabs(velocity)>.01) ++moving_velocities;
            double encoder=d->stationary_encoders[(first+n)%CANBUS_STATIONARY_SAMPLES][i];
            if (isfinite(encoder)) {
                ++encoder_count[i]; filtered_encoder[i]+=encoder;
                encoder_min=fmin(encoder_min,encoder); encoder_max=fmax(encoder_max,encoder);
                if (n<2) encoder_early+=encoder/2;
                if (n+2>=d->stationary_count) encoder_late+=encoder/2;
            }
        }
        /* Entry is stricter than exit. A +/-0.01-degree encoder alternation
           does not move the controls, but a real 0.01-degree/50 ms ramp fails
           the window trend and keeps the stationary timer unqualified. */
        if (maximum-minimum>.05||fabs(late-early)>.025) quiet=false;
        if (fabs(filtered[i]-d->last_angles[i])>.04||maximum-minimum>.08||fabs(s->deg[i]-d->last_angles[i])>.1) moving=true;
        /* Corroborate the pose independently: raw encoder positions have a
           1-milliradian quantum. One-count back-and-forth holding noise is not
           ongoing motion, but a cumulative encoder displacement is. */
        if (encoder_count[i]) filtered_encoder[i]/=encoder_count[i];
        if (!d->simulated) {
            if (encoder_count[i]!=d->stationary_count||encoder_max-encoder_min>2||fabs(encoder_late-encoder_early)>1) quiet=false;
            if (encoder_max-encoder_min>3||(d->stationary_encoder_seen[i]&&fabs(filtered_encoder[i]-d->stationary_encoder_anchor[i])>1.5)) moving=true;
        }
        /* Ordinary position-control readiness follows the corroborated pose,
           not the quantized driver's derivative spikes. Preserve a separate
           stricter dwell for power/reset/teach/arrival admission. */
        if (moving_velocities) velocity_quiet=false;
        if (invalid_velocity||moving_velocities>CANBUS_STATIONARY_SAMPLES/2) velocity_moving=true;
        if (invalid_velocity) { quiet=false; moving=true; }
    }
    if (d->stationary_phase<STATIONARY_POSE_QUIET||moving) {
        /* Start a new dwell at the first verified quiet window. Never
           rebase this anchor while quiet: slow drift must accumulate. */
        stationary_enter(d,STATIONARY_MOVING,now);
        if (quiet) stationary_enter(d,STATIONARY_POSE_QUIET,now);
        memcpy(d->last_angles,filtered,sizeof filtered);
        for (unsigned i=0;i<6;++i) if (encoder_count[i]==d->stationary_count) {
            d->stationary_encoder_anchor[i]=filtered_encoder[i]; d->stationary_encoder_seen[i]=true;
        }
    }
    if (d->stationary_phase==STATIONARY_STRICT_STILL&&velocity_moving) stationary_enter(d,STATIONARY_POSE_QUIET,now);
    else if (d->stationary_phase==STATIONARY_POSE_QUIET&&!velocity_moving&&velocity_quiet) stationary_enter(d,STATIONARY_STRICT_STILL,now);
    /* This runs on live feedback. An inconsistent detector fails safe: it
       forgets stillness, so every stationary gate waits for a new dwell. */
    if ((d->pose_stationary_since!=0)!=(d->stationary_phase>=STATIONARY_POSE_QUIET)||
        (d->stationary_since!=0)!=(d->stationary_phase==STATIONARY_STRICT_STILL)) {
        fprintf(stderr,"%s: stationary detector state inconsistent (phase %d); stillness forgotten.\n",d->id,(int)d->stationary_phase);
        can_bus_forget_stationary(d);
    }
}
void can_bus_ingest(struct can_device *d,const struct can_frame *f,bool local,double now)
{
    if (f->can_id&CAN_ERR_FLAG) {
        ++d->errors;
        if (f->can_id&CAN_ERR_BUSOFF) lost(d,"CAN bus-off; inspect connection before reopening");
        return;
    }
    if (local) {
        ++d->local;
        if (piper_foreign_command(f->can_id)) {
            d->feedback.foreign_seen=true; d->feedback.foreign_at=now;
        }
        return;
    }
    /* An arm power cycle/CAN interruption may leave USB open. Invalidate
       static settings when its status stream resumes after a gap, before
       the new live feedback could make the previous settings usable. */
    if (f->can_id==0x2a1 && f->len==8 && d->feedback.arm[0].status_seen &&
        !piper_recent(d->feedback.arm[0].status_at,now))
        can_bus_invalidate_configuration(d,now);
    ++d->rx; d->last_rx=now;
    piper_decode(&d->feedback,f->can_id,f->data,f->len,now);
    update_stationary(d,now);
}
static void simulation(struct can_bus *b,struct can_device *d,double now)
{
    struct piper_feedback *f=&d->feedback;
    double *targets=simulation_targets(b,d), *last=simulation_last(b,d);
    unsigned *powered=simulation_enabled(b,d);
    double dt=(*last)?fmin(now-(*last),.05):.02; (*last)=now;
    struct can_frame frames[3]; double angles[6]; bool reached=true;
    for (unsigned i=0;i<6;++i) {
        double current=f->arm[0].deg[i],error=targets[i]-current;
        bool teaching=f->arm[0].control==PIPER_CONTROL_TEACH||piper_teach_active(f->arm[0].teach);
        angles[i]=current+(((*powered)&(1u<<i))&&f->arm[0].control==1&&!teaching?copysign(fmin(fabs(error),8*dt),error):0);
        if (fabs(targets[i]-angles[i])>.02) reached=false;
    }
    piper_targets(angles,frames);
    for (unsigned i=0;i<3;++i) { frames[i].can_id=0x2a5+i; can_bus_ingest(d,&frames[i],false,now); }
    struct can_frame status={.can_id=0x2a1,.len=8,.data={f->arm[0].control,f->arm[0].arm_status,1,f->arm[0].teach,reached?0:1}};
    can_bus_ingest(d,&status,false,now);
    for (unsigned i=0;i<6;++i) {
        struct can_frame motor={.can_id=0x261+i,.len=8,.data={0,240,0,28,27,((*powered)&(1u<<i))?0x40:0}};
        can_bus_ingest(d,&motor,false,now);
        struct can_frame high={.can_id=0x251+i,.len=8,.data={0,0,0,120}};
        can_bus_ingest(d,&high,false,now);
    }
    double delta=(*simulation_gripper(b,d))-f->gripper_mm;
    struct can_frame gripper;
    /* Feedback can be slightly negative at a mechanical stop; preserve it. */
    uint32_t value=(uint32_t)(int32_t)llround((f->gripper_mm+copysign(fmin(fabs(delta),5*dt),delta))*1000);
    gripper=(struct can_frame){.can_id=0x2a8,.len=8,.data={(uint8_t)(value>>24),(uint8_t)(value>>16),(uint8_t)(value>>8),(uint8_t)value,0,0,f->gripper_enabled?0x40:0,0}};
    can_bus_ingest(d,&gripper,false,now);
}
void can_bus_receive(struct can_bus *b,double now)
{
    if (b->simulated) {
        for (unsigned i=0;i<CANBUS_MAX;++i) if (b->devices[i].simulated) simulation(b,&b->devices[i],now);
        return;
    }
#ifdef _WIN32
    windows_receive(b,now);
#else
    for (unsigned i=0;i<CANBUS_MAX;++i) {
        struct can_device *d=&b->devices[i]; if (d->fd<0) continue;
        for (unsigned budget=0;budget<512;++budget) {
            struct can_frame f; struct iovec iov={.iov_base=&f,.iov_len=sizeof f};
            struct msghdr message={.msg_iov=&iov,.msg_iovlen=1};
            ssize_t n=recvmsg(d->fd,&message,MSG_DONTWAIT);
            if (n<0) { if (errno!=EAGAIN&&errno!=EWOULDBLOCK&&errno!=EINTR) lost(d,strerror(errno)); break; }
            if (n!=(ssize_t)sizeof f||(message.msg_flags&MSG_TRUNC)||f.len>8) continue;
            can_bus_ingest(d,&f,!!(message.msg_flags&MSG_DONTROUTE),now);
            if (d->fd<0) break;
        }
    }
#endif
}
void can_bus_record_tx_failure(struct can_device *d,unsigned generation,unsigned frame_index,unsigned batch_count,unsigned can_id,unsigned os_error,int transport_error)
{
    d->tx_failure=(struct can_tx_failure){.seen=true,.at=piper_now(),.generation=generation,.frame_index=frame_index,
        .batch_count=batch_count,.can_id=can_id,.os_error=os_error,.transport_error=transport_error,
        .rx=d->rx,.tx=d->tx,.local=d->local,.errors=d->errors};
    snprintf(d->tx_failure.problem,sizeof d->tx_failure.problem,"%s",d->problem);
}
bool can_bus_batch_allowed(const struct can_device *d,const struct can_frame *frames,size_t count)
{
    if (!d||!frames||count==0) return false;
    /* Validate the complete batch before sending any part of it. No arbitrary
       CAN writer, parameter writes, MIT gains or calibration writes exist. */
    for (size_t i=0;i<count;++i) {
        const struct can_frame *f=&frames[i];
        if (f->len!=8||f->can_id>0x7ff) return false;
        bool allowed=f->can_id>=0x155&&f->can_id<=0x157;
        unsigned tail=0;
        if (f->can_id==0x150) { allowed=f->data[0]==1||f->data[0]==2; tail=1; }
        if (f->can_id==0x151) {
            /* PV MoveJ or the documented standby request; never a mode,
               gain or impedance flag in the tail bytes. */
            allowed=(f->data[0]==1&&f->data[1]==1&&f->data[2]>=1&&f->data[2]<=100)||
                (f->data[0]==0&&f->data[1]==1&&f->data[2]==1);
            tail=3;
        }
        if (f->can_id==0x471) { allowed=f->data[0]>=1&&f->data[0]<=6&&(f->data[1]==1||f->data[1]==2); tail=2; }
        if (f->can_id==0x472) { allowed=f->data[0]>=1&&f->data[0]<=6&&(f->data[1]==1||f->data[1]==2); tail=2; }
        if (f->can_id==0x4af) { allowed=f->data[0]==1; tail=1; }
        if (f->can_id==0x477) {
            allowed=memcmp(f->data,piper_gripper_config_query().data,8)==0;
        }
        if (f->can_id==0x159) {
            uint32_t width=((uint32_t)f->data[0]<<24)|((uint32_t)f->data[1]<<16)|((uint32_t)f->data[2]<<8)|f->data[3];
            unsigned effort=((unsigned)f->data[4]<<8)|f->data[5];
            bool known=d->feedback.gripper_config_seen&&(d->feedback.gripper_max_mm==70||d->feedback.gripper_max_mm==100);
            double age=piper_now()-d->feedback.gripper_config_at;
            bool configured=known&&age>=0&&d->feedback.gripper_config_at>=d->configuration_since&&width<=d->feedback.gripper_max_mm*1000u;
            /* A zero-target disable remains available even without a configuration reply. */
            allowed=(configured||(f->data[6]==0&&width==0))&&effort>=20&&effort<=500&&f->data[6]<=1&&f->data[7]==0;
        }
        if (tail) for (unsigned j=tail;j<8;++j) if (f->data[j]) allowed=false;
        if (!allowed) return false;
    }
    return true;
}
bool can_bus_send(struct can_bus *b,struct can_device *d,unsigned generation,const struct can_frame *frames,size_t count)
{
    if (!d||!d->present||!d->up||d->generation!=generation) return false;
    if (!b->simulated && !can_bus_transport_open(d)) return false;
#ifndef _WIN32
    if (!b->simulated && if_nametoindex(d->name)!=d->index) return false;
#endif
    if (!can_bus_batch_allowed(d,frames,count)) return false;
    for (size_t i=0;i<count;++i) {
        const struct can_frame *f=&frames[i];
        if (!b->simulated) {
#ifdef _WIN32
            unsigned os_error=0; int transport_error=0;
            if (!windows_send(b,d,f,&os_error,&transport_error)) {
                can_bus_record_tx_failure(d,generation,(unsigned)i+1,(unsigned)count,f->can_id,os_error,transport_error); return false;
            }
#else
            if (send(d->fd,f,sizeof *f,MSG_DONTWAIT)!=(ssize_t)sizeof *f) {
                int error=errno;
                snprintf(d->problem,sizeof d->problem,"CAN send failed: %s",strerror(error));
                can_bus_record_tx_failure(d,generation,(unsigned)i+1,(unsigned)count,f->can_id,(unsigned)error,0); return false;
            }
#endif
        } else {
            if (f->can_id>=0x155&&f->can_id<=0x157) {
                struct piper_feedback decoded={0}; struct can_frame copy=*f;
                copy.can_id=0x2a5+f->can_id-0x155; piper_decode(&decoded,copy.can_id,copy.data,8,piper_now());
                unsigned j=(f->can_id-0x155)*2; simulation_targets(b,d)[j]=decoded.arm[0].deg[j]; simulation_targets(b,d)[j+1]=decoded.arm[0].deg[j+1];
            } else if (f->can_id==0x151) d->feedback.arm[0].control=f->data[0];
            else if (f->can_id==0x471 && f->data[0]>=1 && f->data[0]<=6) {
                unsigned mask=1u<<(f->data[0]-1);
                if (f->data[1]==2) (*simulation_enabled(b,d))|=mask; else (*simulation_enabled(b,d))&=~mask;
            } else if (f->can_id==0x150) {
                memcpy(simulation_targets(b,d),d->feedback.arm[0].deg,6*sizeof(double));
                if (f->data[0]==2) { (*simulation_enabled(b,d))=0; d->feedback.arm[0].arm_status=0; d->feedback.arm[0].control=0; d->feedback.arm[0].teach=0; }
                else d->feedback.arm[0].arm_status=1;
            } else if (f->can_id==0x159) {
                struct piper_feedback decoded={0}; piper_decode(&decoded,0x2a8,f->data,8,piper_now());
                (*simulation_gripper(b,d))=decoded.gripper_mm; d->feedback.gripper_enabled=f->data[6]==1;
            } else if (f->can_id==0x4af) {
                const char raw[]="H-V1.2-1\0ARM_MC\0S-V1.8-2251014\0";
                for (size_t off=0;off<sizeof raw;off+=8) piper_decode(&d->feedback,0x4af,(const uint8_t *)raw+off,fmin(8,sizeof raw-off),piper_now());
            } else if (f->can_id==0x477) {
                const uint8_t config[8]={100,100,1};
                piper_decode(&d->feedback,0x47e,config,8,piper_now());
            } else if (f->can_id==0x472 && f->data[0]>=1 && f->data[0]<=6) {
                if (f->data[1]==2) {
                    const uint8_t acc[8]={f->data[0],0x01,0xf4}; /* raw500 reports5 rad/s^2 at .01/unit. */
                    piper_decode(&d->feedback,0x47c,acc,8,piper_now());
                    ++d->tx; continue;
                }
                const double limits[6][2]={{-150,150},{0,180},{-170,0},{-100,100},{-70,70},{-180,180}};
                unsigned j=f->data[0]-1;
                /* The simulated 1.8-2 firmware uses the modern .01-rad/s
                   0x473 unit. Its intentional .3-rad/s limit is raw 30. */
                uint16_t maximum=(uint16_t)(int16_t)llround(limits[j][1]*10),minimum=(uint16_t)(int16_t)llround(limits[j][0]*10);
                const uint8_t reply[8]={f->data[0],(uint8_t)(maximum>>8),(uint8_t)maximum,
                    (uint8_t)(minimum>>8),(uint8_t)minimum,0,30,0};
                piper_decode(&d->feedback,0x473,reply,8,piper_now());
            }
        }
        ++d->tx;
    }
    return true;
}
struct can_device *can_bus_find(struct can_bus *b,const char *id)
{
    for (unsigned i=0;i<CANBUS_MAX;++i) if (b->devices[i].id[0]&&!strcmp(b->devices[i].id,id)) return &b->devices[i];
    return NULL;
}
void can_bus_close(struct can_bus *b) { for (unsigned i=0;i<CANBUS_MAX;++i) if (b->devices[i].fd>=0) lost(&b->devices[i],"Disconnected"); }
static void reconnect_device_with_scan(struct can_bus *b,struct can_device *d,void (*scan)(struct can_bus *))
{
    if (d) {
        bool owned=false;
        for (unsigned i=0;i<CANBUS_MAX;++i) if (d==&b->devices[i]) { owned=true; break; }
        if (!owned) return;
    }
    if (b->simulated) {
        if (!d||!d->simulated) return;
        double angles[6];
        memcpy(angles,d->feedback.arm[0].deg,sizeof angles);
        memset(&d->feedback,0,sizeof d->feedback); memcpy(d->feedback.arm[0].deg,angles,sizeof angles);
        d->generation=++b->sequence; can_bus_forget_stationary(d);
    } else {
        /* Discovery skips existing live handles. Closing just the failed
           identity prevents a silent peer from repeatedly invalidating every
           other arm's firmware, limit cache, measured pose and permissions. */
        if (d) lost(d,"Reconnecting this CAN adapter");
        scan(b);
    }
}
void can_bus_reconnect_device(struct can_bus *b,struct can_device *d)
{
    reconnect_device_with_scan(b,d,can_bus_scan);
}
void can_bus_reconnect(struct can_bus *b)
{
    if (b->simulated) can_bus_reconnect_device(b,&b->devices[0]);
    else { can_bus_close(b); can_bus_scan(b); }
}

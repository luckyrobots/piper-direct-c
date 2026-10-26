#define _GNU_SOURCE
#include "canbus.h"
#include <errno.h>
#include <linux/can/raw.h>
#include <linux/can/error.h>
#include <linux/can/netlink.h>
#include <linux/rtnetlink.h>
#include <poll.h>
#include <math.h>
#include <net/if_arp.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

double piper_now(void)
{
    struct timespec t;
    if (clock_gettime(CLOCK_MONOTONIC,&t)<0) abort();
    return (double)t.tv_sec+(double)t.tv_nsec/1e9;
}
static bool number(const char *name,const char *field,unsigned long *n)
{
    char path[256],text[80],*end;
    snprintf(path,sizeof path,"/sys/class/net/%s/%s",name,field);
    FILE *f=fopen(path,"r"); if (!f) return false;
    bool ok=fgets(text,sizeof text,f)!=NULL; fclose(f); if (!ok) return false;
    errno=0; *n=strtoul(text,&end,0); return errno==0 && end!=text;
}
static void lost(struct can_device *d,const char *why)
{
    if (d->fd>=0) close(d->fd);
    d->fd=-1; memset(&d->feedback,0,sizeof d->feedback);
    d->stationary_since=0; snprintf(d->problem,sizeof d->problem,"%s",why);
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
void can_bus_init(struct can_bus *b,bool simulated)
{
    memset(b,0,sizeof *b); b->simulated=simulated;
    for (unsigned i=0;i<CANBUS_MAX;++i) b->devices[i].fd=-1;
    if (simulated) {
        struct can_device *d=&b->devices[0];
        snprintf(d->id,sizeof d->id,"simulation:one"); snprintf(d->name,sizeof d->name,"sim0");
        snprintf(d->label,sizeof d->label,"Simulated PiPer");
        d->present=d->up=d->stable=d->simulated=true; d->generation=++b->sequence;
        d->bitrate=1000000; d->link_verified=true;
        double initial[6]={0,45,-65,0,10,0};
        memcpy(b->sim_target,initial,sizeof initial);
        memcpy(d->feedback.arm[0].deg,initial,sizeof initial);
        b->sim_gripper=25;
    }
}
void can_bus_scan(struct can_bus *b)
{
    if (b->simulated) return;
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
        memset(&d->feedback,0,sizeof d->feedback); d->stationary_since=0;
    }
    if_freenameindex(names);
    for (unsigned i=0;i<CANBUS_MAX;++i) if (b->devices[i].id[0]&&!b->devices[i].present)
        lost(&b->devices[i],"USB adapter missing");
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
        if ((f->can_id>=0x150&&f->can_id<=0x18f)||f->can_id==0x471||f->can_id==0x470) {
            d->feedback.foreign_seen=true; d->feedback.foreign_at=now;
        }
        return;
    }
    ++d->rx; d->last_rx=now;
    piper_decode(&d->feedback,f->can_id,f->data,f->len,now);
    if (piper_fresh(&d->feedback.arm[0],now) && now-d->frame_at>=.04) {
        bool moved=!d->stationary_since;
        for (unsigned i=0;i<6;++i) if (fabs(d->last_angles[i]-d->feedback.arm[0].deg[i])>.03) moved=true;
        /* Keep a stationary anchor: small changes must accumulate rather than
           treating a slow-moving joint as stationary on every frame. */
        if (moved) { d->stationary_since=now; memcpy(d->last_angles,d->feedback.arm[0].deg,sizeof d->last_angles); }
        d->frame_at=now;
    }
}
static void simulation(struct can_bus *b,double now)
{
    struct can_device *d=&b->devices[0]; struct piper_feedback *f=&d->feedback;
    double dt=b->sim_last?fmin(now-b->sim_last,.05):.02; b->sim_last=now;
    struct can_frame frames[3]; double angles[6]; bool reached=true;
    for (unsigned i=0;i<6;++i) {
        double current=f->arm[0].deg[i],error=b->sim_target[i]-current;
        angles[i]=current+((b->sim_enabled&(1u<<i))?copysign(fmin(fabs(error),8*dt),error):0);
        if (fabs(b->sim_target[i]-angles[i])>.02) reached=false;
    }
    piper_targets(angles,frames);
    for (unsigned i=0;i<3;++i) { frames[i].can_id=0x2a5+i; can_bus_ingest(d,&frames[i],false,now); }
    struct can_frame status={.can_id=0x2a1,.len=8,.data={f->arm[0].control,f->arm[0].arm_status,1,0,reached?0:1}};
    can_bus_ingest(d,&status,false,now);
    for (unsigned i=0;i<6;++i) {
        struct can_frame motor={.can_id=0x261+i,.len=8,.data={0,240,0,28,27,(b->sim_enabled&(1u<<i))?0x40:0}};
        can_bus_ingest(d,&motor,false,now);
        struct can_frame high={.can_id=0x251+i,.len=8,.data={0,0,0,120}};
        can_bus_ingest(d,&high,false,now);
    }
    double delta=b->sim_gripper-f->gripper_mm;
    struct can_frame gripper;
    /* Feedback can be slightly negative at a mechanical stop; preserve it. */
    uint32_t value=(uint32_t)(int32_t)llround((f->gripper_mm+copysign(fmin(fabs(delta),5*dt),delta))*1000);
    gripper=(struct can_frame){.can_id=0x2a8,.len=8,.data={(uint8_t)(value>>24),(uint8_t)(value>>16),(uint8_t)(value>>8),(uint8_t)value,0,0,f->gripper_enabled?0x40:0,0}};
    can_bus_ingest(d,&gripper,false,now);
}
void can_bus_receive(struct can_bus *b,double now)
{
    if (b->simulated) { simulation(b,now); return; }
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
}
bool can_bus_send(struct can_bus *b,struct can_device *d,unsigned generation,const struct can_frame *frames,size_t count)
{
    if (!d||!d->present||!d->up||d->generation!=generation) return false;
    if (!b->simulated && (d->fd<0||if_nametoindex(d->name)!=d->index)) return false;
    /* Validate the complete batch before sending any part of it. No arbitrary
       CAN writer, parameter writes, MIT gains or calibration writes exist. */
    for (size_t i=0;i<count;++i) {
        const struct can_frame *f=&frames[i];
        if (f->len!=8||f->can_id>0x7ff) return false;
        bool allowed=f->can_id>=0x155&&f->can_id<=0x157;
        unsigned tail=0;
        if (f->can_id==0x150) { allowed=f->data[0]==1||f->data[0]==2; tail=1; }
        if (f->can_id==0x151) { allowed=f->data[0]==1&&f->data[1]==1&&f->data[2]>=1&&f->data[2]<=10; tail=3; }
        if (f->can_id==0x471) { allowed=f->data[0]>=1&&f->data[0]<=6&&(f->data[1]==1||f->data[1]==2); tail=2; }
        if (f->can_id==0x472) { allowed=f->data[0]>=1&&f->data[0]<=6&&(f->data[1]==1||f->data[1]==2); tail=2; }
        if (f->can_id==0x4af) { allowed=f->data[0]==1; tail=1; }
        if (f->can_id==0x477) {
            const uint8_t query[8]={4,0,0,0,3,0,0,0};
            allowed=memcmp(f->data,query,8)==0;
        }
        if (f->can_id==0x159) {
            uint32_t width=((uint32_t)f->data[0]<<24)|((uint32_t)f->data[1]<<16)|((uint32_t)f->data[2]<<8)|f->data[3];
            unsigned effort=((unsigned)f->data[4]<<8)|f->data[5];
            bool known=d->feedback.gripper_config_seen&&(d->feedback.gripper_max_mm==70||d->feedback.gripper_max_mm==100);
            double age=piper_now()-d->feedback.gripper_config_at;
            bool configured=known&&age>=0&&age<=60&&width<=d->feedback.gripper_max_mm*1000u;
            /* A zero-target disable remains available even without a configuration reply. */
            allowed=(configured||(f->data[6]==0&&width==0))&&effort>=20&&effort<=500&&f->data[6]<=1&&f->data[7]==0;
        }
        if (tail) for (unsigned j=tail;j<8;++j) if (f->data[j]) allowed=false;
        if (!allowed) return false;
    }
    for (size_t i=0;i<count;++i) {
        const struct can_frame *f=&frames[i];
        if (f->len!=8 || f->can_id>0x7ff) return false;
        if (!b->simulated) {
            if (send(d->fd,f,sizeof *f,MSG_DONTWAIT)!=(ssize_t)sizeof *f) {
                snprintf(d->problem,sizeof d->problem,"CAN send failed: %s",strerror(errno)); return false;
            }
        } else {
            if (f->can_id>=0x155&&f->can_id<=0x157) {
                struct piper_feedback decoded={0}; struct can_frame copy=*f;
                copy.can_id=0x2a5+f->can_id-0x155; piper_decode(&decoded,copy.can_id,copy.data,8,piper_now());
                unsigned j=(f->can_id-0x155)*2; b->sim_target[j]=decoded.arm[0].deg[j]; b->sim_target[j+1]=decoded.arm[0].deg[j+1];
            } else if (f->can_id==0x151) d->feedback.arm[0].control=f->data[0];
            else if (f->can_id==0x471 && f->data[0]>=1 && f->data[0]<=6) {
                unsigned mask=1u<<(f->data[0]-1);
                if (f->data[1]==2) b->sim_enabled|=mask; else b->sim_enabled&=~mask;
            } else if (f->can_id==0x150) {
                memcpy(b->sim_target,d->feedback.arm[0].deg,sizeof b->sim_target);
                if (f->data[0]==2) { b->sim_enabled=0; d->feedback.arm[0].arm_status=0; d->feedback.arm[0].control=0; d->feedback.arm[0].teach=0; }
                else d->feedback.arm[0].arm_status=1;
            } else if (f->can_id==0x159) {
                struct piper_feedback decoded={0}; piper_decode(&decoded,0x2a8,f->data,8,piper_now());
                b->sim_gripper=decoded.gripper_mm; d->feedback.gripper_enabled=f->data[6]==1;
            } else if (f->can_id==0x4af) {
                const char raw[]="H-V1.2-1\0ARM_MC\0S-V1.8-2251014\0";
                for (size_t off=0;off<sizeof raw;off+=8) piper_decode(&d->feedback,0x4af,(const uint8_t *)raw+off,fmin(8,sizeof raw-off),piper_now());
            } else if (f->can_id==0x477) {
                const uint8_t config[8]={100,100,1};
                piper_decode(&d->feedback,0x47e,config,8,piper_now());
            } else if (f->can_id==0x472 && f->data[0]>=1 && f->data[0]<=6) {
                if (f->data[1]==2) {
                    const uint8_t acc[8]={f->data[0],0x13,0x88};
                    piper_decode(&d->feedback,0x47c,acc,8,piper_now());
                    ++d->tx; continue;
                }
                const double limits[6][2]={{-150,150},{0,180},{-170,0},{-100,100},{-70,70},{-180,180}};
                unsigned j=f->data[0]-1;
                d->feedback.limit[j]=(struct piper_limit){.seen=true,.min=limits[j][0],.max=limits[j][1],.speed=.3,.at=piper_now()};
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
void can_bus_reconnect(struct can_bus *b)
{
    if (b->simulated) {
        struct can_device *d=&b->devices[0]; double angles[6];
        memcpy(angles,d->feedback.arm[0].deg,sizeof angles);
        memset(&d->feedback,0,sizeof d->feedback); memcpy(d->feedback.arm[0].deg,angles,sizeof angles);
        d->generation=++b->sequence; d->stationary_since=0;
    } else { can_bus_close(b); can_bus_scan(b); }
}

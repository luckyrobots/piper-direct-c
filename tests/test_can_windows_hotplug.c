#define _GNU_SOURCE
/* Offline enumeration and scheduling only. No SetupDi lookup, adapter open,
   real driver callback or robot command is executed by this fixture. */
#include "../src/canbus.c"
#include "fixture.h"
#include <stdio.h>

struct fake_handle { unsigned device; bool open,freed; };
static struct fake_handle original={.device=0,.open=true},candidates[64];
static unsigned allocated,scans,opens[2],stops[2],closes[2],frees[2],timings[2],starts[2],can_sends;
static bool present[2]={true,true},reordered=true;
static uint64_t arrivals[2]={1001,2001};
static const char *serials[2]={"004000485246570520323934","0021003A5443571020393433"};
static const wchar_t *wide_serials[2]={L"004000485246570520323934",L"0021003A5443571020393433"};

static bool fake_connection(const char *serial,uint64_t *arrival)
{
    for (unsigned i=0;i<2;++i) if (!strcmp(serial,serials[i])) {
        if (!present[i]) return false;
        *arrival=arrivals[i];return true;
    }
    return false;
}
static bool fake_identity(const wchar_t *serial,char *name,size_t capacity)
{
    for (unsigned i=0;i<2;++i) if (!wcscmp(serial,wide_serials[i])) {
        CHECK(strlen(serials[i])+1<=capacity);strcpy(name,serials[i]);return true;
    }
    return false;
}
static bool __stdcall fake_list_alloc(void **list) { *list=(void *)11;return true; }
static bool __stdcall fake_list_scan(void *list) { CHECK(list==(void *)11);++scans;return true; }
static bool __stdcall fake_list_free(void *list) { CHECK(list==(void *)11);return true; }
static bool __stdcall fake_num(void *list,uint8_t *count) { CHECK(list==(void *)11);*count=2;return true; }
static bool __stdcall fake_alloc(void *list,uint8_t index,void **handle)
{
    CHECK(list==(void *)11&&index<2&&allocated<64);
    struct fake_handle *candidate=&candidates[allocated++];
    *candidate=(struct fake_handle){.device=reordered?1u-index:index};*handle=candidate;return true;
}
static const wchar_t *__stdcall fake_serial(void *handle)
{
    struct fake_handle *h=handle;CHECK(!h->freed);return wide_serials[h->device];
}
static bool __stdcall fake_open(void *handle)
{
    struct fake_handle *h=handle;CHECK(h!=&original&&h->device==1&&!h->open&&!h->freed);
    h->open=true;++opens[h->device];return true;
}
static bool __stdcall fake_stop(void *handle)
{
    struct fake_handle *h=handle;CHECK(h!=&original&&h->device==1&&h->open&&!h->freed);++stops[h->device];return true;
}
static bool __stdcall fake_close(void *handle)
{
    struct fake_handle *h=handle;CHECK(h!=&original&&h->device==1&&h->open&&!h->freed);h->open=false;++closes[h->device];return true;
}
static bool __stdcall fake_free(void *handle)
{
    struct fake_handle *h=handle;CHECK(h!=&original&&!h->open&&!h->freed);h->freed=true;++frees[h->device];return true;
}
static bool __stdcall fake_timing(void *handle,struct win_timing *timing)
{
    struct fake_handle *h=handle;CHECK(h->device==1&&h->open&&!h->freed);
    CHECK(timing->prop==1&&timing->phase1==12&&timing->phase2==2&&timing->sjw==1&&timing->brp==3);
    ++timings[h->device];return true;
}
static bool fail_start;
static bool __stdcall fake_start(void *handle,uint32_t flags)
{
    struct fake_handle *h=handle;CHECK(h->device==1&&h->open&&!h->freed&&flags==(1u<<3));++starts[h->device];return !fail_start;
}
static int __stdcall fake_last_error(void *handle) { (void)handle;return 7; }
static bool __stdcall fake_send(void *handle,struct win_frame *frame,fn_pump pump,void *context)
{
    (void)handle;(void)frame;(void)pump;(void)context;++can_sends;CHECK(!"Discovery must not send a CAN frame");return false;
}
static void fake_inventory_scan(struct can_bus *bus) { windows_scan_inventory(bus,fake_connection,fake_identity); }

/* Exercise the actual managed-bridge scheduling branch with only its scan and
   controller-tick boundaries replaced. It must defer expensive discovery while
   a motion lease is active, without skipping the cheap identity checks. */
static double scheduling_clock=10;
static unsigned full_scans,connection_checks,controller_ticks;
static double fake_now(void) { return scheduling_clock; }
static void fake_scan(struct can_bus *bus) { (void)bus;++full_scans; }
static void fake_check(struct can_bus *bus) { (void)bus;++connection_checks; }
static void fake_tick(struct controller *controller,double now) { (void)controller;CHECK(now==scheduling_clock);++controller_ticks; }
#define piper_now fake_now
#define can_bus_scan fake_scan
#define can_bus_check_connections fake_check
#define controller_tick fake_tick
#include "../src/managed_bridge.c"
#undef piper_now
#undef can_bus_scan
#undef can_bus_check_connections
#undef controller_tick

static void scenarios(void)
{
    struct can_bus bus;can_bus_init(&bus,false);bus.sequence=7;
    struct can_device *first=&bus.devices[0];
    snprintf(first->id,sizeof first->id,"usb:%s",serials[0]);strcpy(first->name,"Cando 0");
    first->fd=0;first->generation=7;first->present=first->up=first->stable=first->link_verified=true;
    first->bitrate=1000000;first->rx=123;first->tx=456;first->stationary_since=42;
    strcpy(first->feedback.hardware,"H-V1.2-1");strcpy(first->feedback.version,"S-V1.9-0");
    first->feedback.arm[0].deg[1]=12.34;first->feedback.motor[1].enabled=true;
    const struct can_device saved_first=*first;
    win.handles[0]=&original;win.arrivals[0]=arrivals[0];
    win.list_alloc=fake_list_alloc;win.list_scan=fake_list_scan;win.list_free=fake_list_free;
    win.num=fake_num;win.alloc=fake_alloc;win.serial=fake_serial;win.open=fake_open;
    win.stop=fake_stop;win.close=fake_close;win.free=fake_free;win.timing=fake_timing;win.start=fake_start;win.send_pumped=fake_send;
    win.last_error=fake_last_error;

    windows_scan_inventory(&bus,fake_connection,fake_identity);
    struct can_device *second=&bus.devices[1];
    CHECK(!memcmp(first,&saved_first,sizeof *first)&&win.handles[0]==&original&&win.arrivals[0]==arrivals[0]);
    CHECK(!strcmp(second->id,"usb:0021003A5443571020393433")&&second->fd==1&&second->generation==8);
    CHECK(!strcmp(first->name,"Cando 0")&&!strcmp(second->name,"Cando 1")&&second->index==0);
    CHECK(second->present&&second->up&&second->stable&&second->link_verified&&second->bitrate==1000000);
    CHECK(opens[1]==1&&stops[1]==1&&timings[1]==1&&starts[1]==1&&closes[1]==0&&can_sends==0);
    void *second_handle=win.handles[1];
    const struct can_device saved_second=*second;

    windows_scan_inventory(&bus,fake_connection,fake_identity);
    reordered=false;windows_scan_inventory(&bus,fake_connection,fake_identity);
    CHECK(scans==3&&!memcmp(first,&saved_first,sizeof *first)&&!memcmp(second,&saved_second,sizeof *second));
    CHECK(win.handles[0]==&original&&win.handles[1]==second_handle);
    CHECK(!strcmp(first->name,"Cando 0")&&!strcmp(second->name,"Cando 1"));
    CHECK(opens[1]==1&&stops[1]==1&&timings[1]==1&&starts[1]==1&&closes[1]==0&&can_sends==0);

    /* Motion-time connection checking never enumerates or opens anything. */
    unsigned scans_before=scans;windows_check_connections(&bus,fake_connection);
    CHECK(scans==scans_before&&opens[1]==1&&!memcmp(first,&saved_first,sizeof *first));
    present[1]=false;windows_check_connections(&bus,fake_connection);
    CHECK(scans==scans_before&&second->fd==-1&&!second->present&&!second->up);
    CHECK(stops[1]==2&&closes[1]==1&&win.handles[1]==NULL&&!memcmp(first,&saved_first,sizeof *first));
    windows_scan_inventory(&bus,fake_connection,fake_identity);
    CHECK(opens[1]==1&&win.handles[0]==&original&&can_sends==0);

    present[1]=true;++arrivals[1];windows_scan_inventory(&bus,fake_connection,fake_identity);
    CHECK(second->fd==1&&second->generation==9&&win.arrivals[1]==arrivals[1]&&win.handles[1]!=second_handle);
    CHECK(opens[1]==2&&stops[1]==3&&timings[1]==2&&starts[1]==2&&closes[1]==1);
    CHECK(!memcmp(first,&saved_first,sizeof *first)&&win.handles[0]==&original&&can_sends==0);

    /* A selected silent peer's automatic retry must run the real targeted
       close+discovery path, never reopen or erase the healthy first adapter.
       Only the scan's OS metadata boundary is replaced by offline callbacks. */
    strcpy(second->problem,"Windows USB-CAN send failed (transport 14, WinUSB 121)");
    second->last_rx=1;strcpy(second->feedback.version,"stale-peer-version");
    unsigned failed_generation=second->generation,opens_before=opens[1],closes_before=closes[1];
    reconnect_device_with_scan(&bus,second,fake_inventory_scan);
    CHECK(second->generation>failed_generation&&opens[1]==opens_before+1&&closes[1]==closes_before+1);
    CHECK(second->fd==1&&second->present&&second->up&&second->feedback.version[0]==0&&second->problem[0]==0);
    CHECK(!memcmp(first,&saved_first,sizeof *first)&&win.handles[0]==&original&&can_sends==0);
    failed_generation=second->generation;
    reconnect_device_with_scan(&bus,NULL,fake_inventory_scan);
    CHECK(second->generation==failed_generation&&!memcmp(first,&saved_first,sizeof *first)&&win.handles[0]==&original);
    CHECK(opens[0]==0&&stops[0]==0&&closes[0]==0&&timings[0]==0&&starts[0]==0);
    /* An unplug/replug between scans changes the PnP arrival even if the serial
       is already present again; invalidate only that connection generation. */
    failed_generation=second->generation;opens_before=opens[1];closes_before=closes[1];
    ++arrivals[1];windows_scan_inventory(&bus,fake_connection,fake_identity);
    CHECK(second->generation>failed_generation&&win.arrivals[1]==arrivals[1]&&opens[1]==opens_before+1&&closes[1]==closes_before+1);
    CHECK(opens[0]==0&&stops[0]==0&&closes[0]==0&&timings[0]==0&&starts[0]==0);
    CHECK(!memcmp(first,&saved_first,sizeof *first)&&win.handles[0]==&original&&can_sends==0);

    /* B20: a timing/start failure after open closes the handle and reports why. */
    fail_start=true;++arrivals[1];windows_scan_inventory(&bus,fake_connection,fake_identity);
    CHECK(second->fd==-1&&!second->up&&strstr(second->problem,"timing/start failed (transport error 7"));
    CHECK(win.handles[1]==NULL&&!memcmp(first,&saved_first,sizeof *first)&&can_sends==0);
    fail_start=false;windows_scan_inventory(&bus,fake_connection,fake_identity);
    CHECK(second->fd==1&&second->up&&second->problem[0]==0);

    struct controller controller={0};strcpy(controller.lanes[0].lane_device,"usb:lane");controller.lanes[0].op=OP_MOVE;piper_tick(&controller);
    CHECK(full_scans==0&&connection_checks==1&&controller_ticks==1);
    scheduling_clock+=1.1;controller.lanes[0].op=OP_IDLE;piper_tick(&controller);
    CHECK(full_scans==1&&connection_checks==1&&controller_ticks==2);
    scheduling_clock+=.1;controller.lanes[0].op=OP_MOVE;piper_tick(&controller);
    CHECK(full_scans==1&&connection_checks==1&&controller_ticks==3);
    scheduling_clock+=1.1;piper_tick(&controller);
    CHECK(full_scans==1&&connection_checks==2&&controller_ticks==4);
    puts("PASS offline Windows hotplug: discovery/reorder/removal and targeted stale-peer reconnect preserve the healthy adapter's exact handle, generation, firmware and feedback; discovery is deferred until idle.");
}
static const struct check_test tests[]={{"can-windows-hotplug","every scenario of this file",scenarios,NULL}};
int main(void) { return check_main("can-windows-hotplug",tests,1); }

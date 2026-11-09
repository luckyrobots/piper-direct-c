#define _GNU_SOURCE
/* Exercise the production controller idle scheduler with fake transport
   boundaries. No native adapter enumeration, socket, USB or robot I/O runs. */
#include "fixture.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>

static double clock_now=100;
static unsigned targeted_reopens,inventory_scans,query_attempts;
double __wrap_piper_now(void) { return clock_now; }
void __wrap_can_bus_receive(struct can_bus *bus,double now) { CHECK(now==clock_now);bus->devices[0].last_rx=now; }
void __wrap_can_bus_reconnect(struct can_bus *bus) { (void)bus;CHECK(!"An automatic retry must never reconnect the whole bus"); }
void __wrap_can_bus_reconnect_device(struct can_bus *bus,struct can_device *device)
{
    if (!device) { ++inventory_scans;return; }
    CHECK(device==&bus->devices[1]);
    ++targeted_reopens;device->generation=++bus->sequence;
    device->problem[0]=0;memset(&device->feedback,0,sizeof device->feedback);
}
bool __wrap_can_bus_send(struct can_bus *bus,struct can_device *device,unsigned generation,const struct can_frame *frames,size_t count)
{
    CHECK(device==&bus->devices[1]&&generation==device->generation&&count==2);
    CHECK(frames[0].can_id==0x4af&&frames[0].data[0]==1&&frames[1].can_id==0x477);
    ++query_attempts;
    /* Keep the selected peer failed so the next idle retry is exercised. */
    strcpy(device->problem,"Windows USB-CAN send failed (transport 14, WinUSB 121)");
    return false;
}

int main(void)
{
    struct controller controller={0};can_bus_init(&controller.bus,false);controller.bus.sequence=2;
    struct can_device *healthy=&controller.bus.devices[0],*failed=&controller.bus.devices[1];
    strcpy(healthy->id,"usb:healthy");healthy->fd=0;healthy->generation=1;
    healthy->present=healthy->up=healthy->stable=healthy->link_verified=true;healthy->last_rx=clock_now;
    strcpy(healthy->feedback.hardware,"H-V1.2-1");strcpy(healthy->feedback.version,"S-V1.9-0");
    healthy->feedback.arm[0].deg[2]=-32.125;healthy->feedback.motor[2].enabled=true;
    for (unsigned i=0;i<6;++i) { healthy->feedback.limit[i].seen=true;healthy->feedback.acceleration[i].seen=true; }
    healthy->rx=12345;healthy->tx=12;
    struct can_device saved_healthy=*healthy;
    strcpy(failed->id,"usb:failed");failed->fd=1;failed->generation=2;failed->present=failed->up=true;
    strcpy(failed->problem,"Windows USB-CAN send failed (transport 14, WinUSB 121)");
    strcpy(controller.selected,failed->id);

    controller_tick(&controller,clock_now);
    CHECK(targeted_reopens==1&&query_attempts==1&&failed->generation==3&&!controller.query);
    CHECK(!memcmp(healthy,&saved_healthy,sizeof *healthy));
    clock_now+=1;controller_tick(&controller,clock_now);
    saved_healthy.last_rx=clock_now;
    CHECK(targeted_reopens==1&&query_attempts==1&&!memcmp(healthy,&saved_healthy,sizeof *healthy));
    clock_now+=7.1;controller_tick(&controller,clock_now);
    saved_healthy.last_rx=clock_now;
    CHECK(targeted_reopens==2&&query_attempts==2&&failed->generation==4&&!memcmp(healthy,&saved_healthy,sizeof *healthy));

    /* An existing read-only query retains its connection until it retires. */
    controller.query=true;controller.query_generation=failed->generation;
    clock_now+=9;controller.query_start=clock_now;controller.query_next=clock_now+1;
    controller_tick(&controller,clock_now);
    saved_healthy.last_rx=clock_now;
    CHECK(targeted_reopens==2&&query_attempts==2&&!memcmp(healthy,&saved_healthy,sizeof *healthy));
    controller.query=false;

    /* Unknown saved selection only discovers inventory; it must not close a
       healthy peer just because that selection has no device object yet. */
    strcpy(controller.selected,"usb:not-connected");controller_tick(&controller,clock_now);
    CHECK(inventory_scans==1&&targeted_reopens==3&&!memcmp(healthy,&saved_healthy,sizeof *healthy));

    /* Selecting the healthy arm must not retry a different stale adapter. */
    strcpy(controller.selected,healthy->id);healthy->last_rx=clock_now;controller.auto_query_at=clock_now;
    const struct can_device healthy_selected=*healthy;
    controller_tick(&controller,clock_now);
    CHECK(inventory_scans==1&&targeted_reopens==3&&query_attempts==3&&!memcmp(healthy,&healthy_selected,sizeof *healthy));
    puts("PASS idle reconnect: a persistently failing selected adapter retries alone, preserving the healthy peer's generation, firmware, limits, motor state and readings; retries are throttled and query-safe.");
    return 0;
}

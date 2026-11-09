#define _GNU_SOURCE
/* SocketCAN loss path: a bus-off error frame on a fake open device must drop
   every connection-scoped fact. No socket or robot I/O runs; FIXTURE_FD is
   negative, so the close in lost() never touches a real descriptor. */
#include "fixture.h"
#include <string.h>

static struct can_device *fake_open_device(struct can_bus *bus)
{
    can_bus_init(bus,false);
    struct can_device *d=&bus->devices[0];
    strcpy(d->id,"usb:fake"); strcpy(d->name,"can0");
    d->fd=FIXTURE_FD; d->generation=1;
    d->present=d->up=d->stable=d->link_verified=true;
    d->feedback.arm[0].status_seen=true; d->feedback.arm[0].status_at=9;
    fixture_still(d,5);
    return d;
}
int main(void)
{
    struct can_bus bus;
    struct can_device *d=fake_open_device(&bus);
    /* An error frame without bus-off is counted but keeps the link. */
    struct can_frame warning={.can_id=CAN_ERR_FLAG,.len=8};
    can_bus_ingest(d,&warning,false,10);
    CHECK(d->errors==1&&d->fd==FIXTURE_FD&&d->link_verified&&d->stationary_phase==STATIONARY_STRICT_STILL);

    struct can_frame busoff={.can_id=CAN_ERR_FLAG|CAN_ERR_BUSOFF,.len=8};
    can_bus_ingest(d,&busoff,false,10);
    CHECK(d->errors==2);
    CHECK(d->fd==-1&&!can_bus_transport_open(d));
    CHECK(!d->link_verified);
    CHECK(d->stationary_phase==STATIONARY_UNKNOWN&&d->stationary_since==0&&d->pose_stationary_since==0);
    CHECK(can_bus_strict_still_for(d,10)==0);
    CHECK(!d->feedback.arm[0].status_seen&&strstr(d->problem,"bus-off"));
    puts("PASS CAN loss: bus-off closes the fake transport, clears link verification, feedback and stationary phase.");
    return 0;
}

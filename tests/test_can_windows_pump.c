/* Pure fake Cando callbacks: no adapter enumeration, open, or Windows USB I/O. */
#include "../src/canbus.c"
#include <assert.h>
#include <stdio.h>
static struct win_frame received[4];
static unsigned next_frame,reads,sends;
static struct can_frame expected_frames[4];
static size_t expected_count,expected_sent;
static bool __stdcall fake_read(void *handle,struct win_frame *frame,uint32_t timeout)
{
    assert(handle==(void *)1&&timeout==0); ++reads;
    if (next_frame==4) { SetLastError(ERROR_TIMEOUT); return false; }
    *frame=received[next_frame++]; return true;
}
static int __stdcall fake_error(void *handle) { assert(handle==(void *)1); return 15; }
static uint32_t __stdcall fake_zero(void *handle) { assert(handle==(void *)1); return 0; }
static bool __stdcall fake_send(void *handle,struct win_frame *frame,fn_pump pump,void *context)
{
    assert(handle==(void *)1&&frame->echo==0&&frame->channel==0&&frame->flags==0&&frame->reserved==0);
    assert(expected_sent<expected_count);
    const struct can_frame *expected=&expected_frames[expected_sent++];
    assert(frame->id==expected->can_id&&frame->len==expected->len&&!memcmp(frame->data,expected->data,8));
    ++sends;
    assert(pump(context)); assert(next_frame==4); return true;
}
static void expect_frames(const struct can_frame *frames,size_t count)
{
    assert(count<=4);expected_count=count;expected_sent=0;
    if (count) memcpy(expected_frames,frames,count*sizeof *frames);
}
static void rejected_batch(struct can_bus *bus,struct can_device *d,const struct can_frame *frames,size_t count)
{
    unsigned previous_sends=sends;uint64_t previous_tx=d->tx;expect_frames(NULL,0);
    assert(!can_bus_send(bus,d,d->generation,frames,count));
    assert(sends==previous_sends&&d->tx==previous_tx&&expected_sent==0);
}
int main(void)
{
    struct can_bus bus={0}; for (unsigned i=0;i<CANBUS_MAX;++i) bus.devices[i].fd=-1;
    struct can_device *d=&bus.devices[0]; d->fd=0; d->present=d->up=true; d->generation=7;
    win.handles[0]=(void *)1; win.read=fake_read; win.send_pumped=fake_send; win.last_error=fake_error;
    win.rx_ready=win.rx_next=win.last_rx_bytes=fake_zero;
    received[0]=(struct win_frame){.echo=0,.id=0x151,.len=8}; /* Local echo must never become foreign control. */
    received[1]=(struct win_frame){.echo=0xffffffffu,.id=0x2a1,.len=9}; /* Invalid length discarded. */
    received[2]=(struct win_frame){.echo=0xffffffffu,.id=0x800002a1,.len=8}; /* Extended address discarded. */
    received[3]=(struct win_frame){.echo=0xffffffffu,.id=0x2a1,.len=8,.data={1,0,1,0,0}};
    double before=piper_now(); struct can_frame frame=piper_mode(1);
    expect_frames(&frame,1);
    assert(can_bus_send(&bus,d,7,&frame,1));
    assert(sends==1&&reads==5&&d->local==1&&d->rx==1&&d->tx==1&&!d->feedback.foreign_seen);
    assert(d->feedback.arm[0].control==1&&d->feedback.arm[0].status_at>=before);
    /* Keep the real common allowlist and the real windows_send wrapper. Only
       Cando callbacks are fake, so a rejected packet cannot hide behind a
       replacement can_bus_send as it did in the controller-only fixture. */
    strcpy(d->feedback.hardware,"H-V1.2-1");strcpy(d->feedback.version,"S-V1.9-0");
    const double target[6]={1.125,2.25,-3.375,4.5,-5.625,6.75};
    struct can_frame pose[4];pose[0]=piper_mode(90);assert(piper_targets(target,pose+1));
    expect_frames(pose,4);uint64_t previous_tx=d->tx;unsigned previous_sends=sends;
    assert(can_bus_send(&bus,d,7,pose,4));
    assert(expected_sent==4&&sends==previous_sends+4&&d->tx==previous_tx+4);
    struct can_frame invalid[4];
    for (unsigned error=0;error<9;++error) {
        memcpy(invalid,pose,sizeof invalid);
        if (error==0) invalid[0].data[3]=0xAD; /* The removed streamed-position mode flag. */
        if (error==1) invalid[0].data[4]=1;
        if (error==2) invalid[0].data[0]=2;
        if (error==3) invalid[0].data[1]=2;
        if (error==4) invalid[0].data[2]=0;
        if (error==5) invalid[0].data[2]=101;
        if (error==6) invalid[3].len=7;
        if (error==7) invalid[3].can_id|=0x80000000u;
        if (error==8) invalid[3].can_id=0x158;
        rejected_batch(&bus,d,invalid,4);
    }
    puts("PASS fake native Windows pump: real batch validation and Windows packing preserve a complete mode plus six-angle transaction; malformed or 0xAD transactions send nothing, ordered RX timestamps and echo filtering remain intact.");
}

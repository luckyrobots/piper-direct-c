/* Windows-only, fake WinUSB. Includes the production implementation so the
   actual single-submit/cancel/retire path is exercised without opening USB. */
#include "../vendor/candle/candle_defs.h"
#include <assert.h>
#include <stdio.h>

static BOOL WINAPI fake_write(WINUSB_INTERFACE_HANDLE,UCHAR,PUCHAR,ULONG,PULONG,LPOVERLAPPED);
static BOOL WINAPI fake_result(WINUSB_INTERFACE_HANDLE,LPOVERLAPPED,PULONG,BOOL);
static BOOL WINAPI fake_read(WINUSB_INTERFACE_HANDLE,UCHAR,PUCHAR,ULONG,PULONG,LPOVERLAPPED);
static BOOL WINAPI fake_abort(WINUSB_INTERFACE_HANDLE,UCHAR);
static BOOL WINAPI fake_reset(WINUSB_INTERFACE_HANDLE,UCHAR);
static BOOL WINAPI fake_cancel(HANDLE,LPOVERLAPPED);
static DWORD WINAPI fake_wait(HANDLE,DWORD);
static ULONGLONG WINAPI fake_clock(void);
#define WinUsb_WritePipe fake_write
#define WinUsb_GetOverlappedResult fake_result
#define WinUsb_ReadPipe fake_read
#define WinUsb_AbortPipe fake_abort
#define WinUsb_ResetPipe fake_reset
#define CancelIoEx fake_cancel
#define WaitForSingleObject fake_wait
#define GetTickCount64 fake_clock
#include "../vendor/candle/candle.c"

enum test_mode { RELEASE_BY_RX, TIMEOUT, IMMEDIATE_ERROR, PARTIAL, PENDING_ERROR, UNRETIRED, OVERSIZE, INCOMPLETE_RESULT, READ_FAILURE };
static enum test_mode mode;
static candle_device_t device;
static OVERLAPPED *active_tx;
static unsigned writes,pumps,requeues,cancels,aborts,resets,rx_left,retire_results;
static bool pending,canceled,cancel_complete;
static ULONGLONG clock_ms;
static BOOL WINAPI fake_write(WINUSB_INTERFACE_HANDLE h,UCHAR pipe,PUCHAR bytes,ULONG length,PULONG sent,LPOVERLAPPED ovl)
{
    assert(h==device.winUSBHandle&&pipe==device.bulkOutPipe&&length==20&&ovl);
    candle_frame_t frame; memset(&frame,0,sizeof frame); memcpy(&frame,bytes,20);
    assert(frame.can_id==0x155&&frame.data[0]==0x42&&frame.channel==0);
    ++writes; active_tx=ovl;
    if (mode==IMMEDIATE_ERROR) { SetLastError(ERROR_GEN_FAILURE); return false; }
    if (mode==PARTIAL) { *sent=12; return true; }
    pending=true; if (mode==RELEASE_BY_RX) rx_left=3;
    SetLastError(ERROR_IO_PENDING); return false;
}
static BOOL WINAPI fake_result(WINUSB_INTERFACE_HANDLE h,LPOVERLAPPED ovl,PULONG bytes,BOOL wait)
{
    assert(h==device.winUSBHandle&&!wait);
    if (ovl==active_tx) {
        if (canceled) { ++retire_results; SetLastError(ERROR_OPERATION_ABORTED); return false; }
        if (mode==INCOMPLETE_RESULT) { SetLastError(ERROR_IO_INCOMPLETE); return false; }
        if (mode==PENDING_ERROR) { SetLastError(ERROR_SEM_TIMEOUT); return false; }
        assert(mode==RELEASE_BY_RX&&requeues>=3); *bytes=20; pending=false; return true;
    }
    *bytes=mode==OVERSIZE?64:20; return true;
}
static BOOL WINAPI fake_read(WINUSB_INTERFACE_HANDLE h,UCHAR pipe,PUCHAR bytes,ULONG length,PULONG transferred,LPOVERLAPPED ovl)
{
    (void)bytes; (void)transferred; (void)ovl;
    assert(h==device.winUSBHandle&&pipe==device.bulkInPipe&&length==64);
    ++requeues; if (rx_left) --rx_left;
    SetLastError(ERROR_IO_PENDING); return false;
}
static BOOL WINAPI fake_abort(WINUSB_INTERFACE_HANDLE h,UCHAR pipe) { assert(h==device.winUSBHandle&&pipe==device.bulkOutPipe); ++aborts; return true; }
static BOOL WINAPI fake_reset(WINUSB_INTERFACE_HANDLE h,UCHAR pipe) { assert(h==device.winUSBHandle&&pipe==device.bulkOutPipe); ++resets; return true; }
static BOOL WINAPI fake_cancel(HANDLE h,LPOVERLAPPED ovl) { assert(h==device.deviceHandle&&ovl==active_tx); ++cancels; canceled=true; return true; }
static DWORD WINAPI fake_wait(HANDLE event,DWORD milliseconds)
{
    if (active_tx&&event==active_tx->hEvent) {
        if (canceled) return cancel_complete?WAIT_OBJECT_0:WAIT_TIMEOUT;
        if ((mode==RELEASE_BY_RX&&requeues>=3)||mode==PENDING_ERROR||mode==INCOMPLETE_RESULT) return WAIT_OBJECT_0;
        clock_ms+=milliseconds; return WAIT_TIMEOUT;
    }
    return rx_left?WAIT_OBJECT_0:WAIT_TIMEOUT;
}
static ULONGLONG WINAPI fake_clock(void) { return clock_ms; }
static bool __stdcall pump(void *context)
{
    assert(context==&device); ++pumps;
    candle_frame_t received;
    while (candle_frame_read(&device,&received,0)) { assert(received.echo_id==0xffffffffu); }
    return mode!=READ_FAILURE||!pending;
}
static void fixture(enum test_mode selected)
{
    memset(&device,0,sizeof device); mode=selected;
    writes=pumps=requeues=cancels=aborts=resets=rx_left=retire_results=0; clock_ms=0;
    active_tx=NULL; pending=canceled=false; cancel_complete=true;
    device.winUSBHandle=(void *)0x1234; device.deviceHandle=(void *)0x5678; device.bulkInPipe=0x81; device.bulkOutPipe=2;
    candle_frame_t received={.echo_id=0xffffffffu,.can_id=0x2a1,.can_dlc=8};
    for (unsigned i=0;i<CANDLE_URB_COUNT;++i) {
        device.rxevents[i]=CreateEvent(NULL,true,false,NULL); assert(device.rxevents[i]); device.rxurbs[i].ovl.hEvent=device.rxevents[i];
        memcpy(device.rxurbs[i].buf,&received,20);
    }
}
static void cleanup(void)
{
    assert(!device.retained_tx);
    for (unsigned i=0;i<CANDLE_URB_COUNT;++i) CloseHandle(device.rxevents[i]);
}
static bool send_frame(void) { candle_frame_t f={.echo_id=7,.can_id=0x155,.can_dlc=8,.data={0x42}}; return candle_frame_send_pumped(&device,0,&f,pump,&device); }
int main(void)
{
    fixture(RELEASE_BY_RX); assert(send_frame());
    assert(writes==1&&pumps>=2&&requeues==3&&!cancels&&!aborts&&device.last_error==CANDLE_ERR_OK); cleanup();
    fixture(TIMEOUT); assert(!send_frame()&&GetLastError()==ERROR_SEM_TIMEOUT);
    assert(writes==1&&clock_ms==20&&cancels==1&&retire_results==1&&aborts&&resets==1); cleanup();
    fixture(IMMEDIATE_ERROR); assert(!send_frame()&&GetLastError()==ERROR_GEN_FAILURE&&writes==1&&!cancels); cleanup();
    fixture(PENDING_ERROR); assert(!send_frame()&&GetLastError()==ERROR_SEM_TIMEOUT&&writes==1&&!cancels); cleanup();
    fixture(PARTIAL); assert(!send_frame()&&GetLastError()==ERROR_BAD_LENGTH&&writes==1&&device.last_error==CANDLE_ERR_SEND_FRAME); cleanup();
    fixture(INCOMPLETE_RESULT); assert(!send_frame()&&GetLastError()==ERROR_IO_INCOMPLETE&&writes==1&&cancels==1&&retire_results==1); cleanup();
    fixture(READ_FAILURE); assert(!send_frame()&&GetLastError()==ERROR_READ_FAULT&&writes==1&&cancels==1&&retire_results==1); cleanup();
    fixture(UNRETIRED); cancel_complete=false; assert(!send_frame()&&device.retained_tx&&writes==1);
    assert(!send_frame()&&GetLastError()==ERROR_BUSY&&writes==1); /* Never resubmit uncertain I/O. */
    cancel_complete=true; assert(candle_retire_tx(&device)); cleanup();
    fixture(OVERSIZE); rx_left=1;
    struct { candle_frame_t frame; uint32_t sentinel; } guarded; memset(&guarded,0x55,sizeof guarded);
    assert(!candle_frame_read(&device,&guarded.frame,0)&&device.last_error==CANDLE_ERR_READ_SIZE);
    assert(guarded.sentinel==0x55555555u&&requeues==1&&device.next_rx==1); cleanup();
    puts("PASS fake WinUSB: receive requeue unblocks single OUT, unchanged 20ms timeout, canceled I/O retirement, errors/partial writes, retained uncertain I/O and oversized RX discard.");
}

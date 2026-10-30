/* Local ABI adapter for the LGPL candle transport; see vendor/candle/LICENSE. */
#include "candle_defs.h"
#include <stdlib.h>
struct local_list { candle_list_handle inner; };
#define API __declspec(dllexport) bool __stdcall
API cando_list_malloc(void **p) { *p=calloc(1,sizeof(struct local_list)); return *p!=NULL; }
API cando_list_scan(void *p) { struct local_list *l=p; if(l->inner)candle_list_free(l->inner); l->inner=NULL; return candle_list_scan(&l->inner); }
API cando_list_num(void *p,uint8_t *n) { return candle_list_length(((struct local_list*)p)->inner,n); }
API cando_list_free(void *p) { struct local_list *l=p; if(l->inner)candle_list_free(l->inner); free(l); return true; }
API cando_malloc(void *p,uint8_t n,void **h) { return candle_dev_get(((struct local_list*)p)->inner,n,h); }
API cando_open(void *h) { return candle_dev_open(h); }
API cando_close(void *h) { candle_device_t *d=h; return !d->deviceHandle || candle_dev_close(h); }
API cando_free(void *h) { if(((candle_device_t*)h)->deviceHandle)return false; return candle_dev_free(h); }
__declspec(dllexport) const wchar_t * __stdcall cando_get_serial_number_str(void *h) { return candle_dev_get_path(h); }
API cando_get_dev_info(void *h,uint32_t *fw,uint32_t *hw) { candle_device_t *d=h; *fw=d->dconf.sw_version; *hw=d->dconf.hw_version; return true; }
API cando_set_timing(void *h,candle_bittiming_t *t) { return candle_channel_set_timing(h,0,t); }
API cando_start(void *h,uint32_t flags) { return candle_channel_start(h,0,flags|0x10u); }
API cando_stop(void *h) { return candle_channel_stop(h,0); }
API cando_frame_send(void *h,candle_frame_t *f) { return candle_frame_send(h,0,f); }
API cando_frame_read(void *h,candle_frame_t *f,uint32_t t) { return candle_frame_read(h,f,t); }

__declspec(dllexport) uint32_t __stdcall cando_last_rx_bytes(void *h) { return ((candle_device_t*)h)->last_rx_bytes; }

__declspec(dllexport) uint32_t __stdcall cando_rx_ready(void *h) {
    candle_device_t *d=h; uint32_t ready=0;
    for(unsigned i=0;i<CANDLE_URB_COUNT;i++) if(WaitForSingleObject(d->rxevents[i],0)==WAIT_OBJECT_0) ready|=1u<<i;
    return ready;
}
__declspec(dllexport) uint32_t __stdcall cando_rx_next(void *h) { return ((candle_device_t*)h)->next_rx; }

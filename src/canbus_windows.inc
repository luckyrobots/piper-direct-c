/* Windows candleLight transport. All vendor calls are serialized by the host.
   Shared protocol, simulator and motion guards remain in canbus/controller.c. */
#include <windows.h>
#include <wchar.h>
#include <ctype.h>
#include <setupapi.h>
#include <cfgmgr32.h>
typedef bool (__stdcall *fn_list_alloc)(void **);
typedef bool (__stdcall *fn_one)(void *);
typedef int (__stdcall *fn_error)(void *);
typedef uint32_t (__stdcall *fn_rx_bytes)(void *);
typedef bool (__stdcall *fn_num)(void *,uint8_t *);
typedef bool (__stdcall *fn_alloc)(void *,uint8_t,void **);
typedef const wchar_t *(__stdcall *fn_serial)(void *);
typedef bool (__stdcall *fn_info)(void *,uint32_t *,uint32_t *);
#pragma pack(push,1)
struct win_frame { uint32_t echo,id; uint8_t len,channel,flags,reserved,data[8]; uint32_t timestamp; };
struct win_timing { uint32_t prop,phase1,phase2,sjw,brp; };
#pragma pack(pop)
typedef bool (__stdcall *fn_timing)(void *,struct win_timing *);
typedef bool (__stdcall *fn_start)(void *,uint32_t);
typedef bool (__stdcall *fn_send)(void *,struct win_frame *);
typedef bool (__stdcall *fn_read)(void *,struct win_frame *,uint32_t);
static struct {
    HMODULE dll;
    fn_list_alloc list_alloc; fn_one list_free,list_scan,free,open,close,stop;
    fn_num num; fn_alloc alloc; fn_serial serial; fn_info info;
    fn_error last_error; fn_rx_bytes last_rx_bytes,rx_ready,rx_next;
    fn_timing timing; fn_start start; fn_send send; fn_read read;
    void *handles[CANBUS_MAX];
    uint64_t arrivals[CANBUS_MAX];
} win;
static bool windows_connection(const char *serial,uint64_t *arrival) {
    wchar_t instance[MAX_DEVICE_ID_LEN],suffix[128]; DEVINST node;
    if (!MultiByteToWideChar(CP_UTF8,0,serial,-1,suffix,128)) return false;
    swprintf(instance,MAX_DEVICE_ID_LEN,L"USB\\VID_1D50&PID_606F\\%ls",suffix);
    ULONG status,problem;
    if (CM_Locate_DevNodeW(&node,instance,CM_LOCATE_DEVNODE_NORMAL)!=CR_SUCCESS ||
        CM_Get_DevNode_Status(&status,&problem,node,0)!=CR_SUCCESS || !(status&DN_STARTED) || problem) return false;
    /* Vendor get_dev_info returns cached fields, so it cannot detect unplug.
       The PnP arrival timestamp also detects a quick replug between scans. */
    const DEVPROPKEY key={{0x83da6326,0x97a6,0x4088,{0x94,0x53,0xa1,0x92,0x3f,0x57,0x3b,0x29}},102};
    DEVPROPTYPE type; ULONG size=sizeof *arrival;
    return CM_Get_DevNode_PropertyW(node,&key,&type,(PBYTE)arrival,&size,0)==CR_SUCCESS &&
        type==DEVPROP_TYPE_FILETIME && size==sizeof *arrival;
}
static bool windows_identity(const wchar_t *value,char *name,size_t capacity) {
    if (!value) return false;
    const wchar_t *serial=value;
    wchar_t instance[MAX_DEVICE_ID_LEN];
    if (!wcsncmp(value,L"\\\\?\\",4)) {
        HDEVINFO set=SetupDiCreateDeviceInfoList(NULL,NULL);
        if (set==INVALID_HANDLE_VALUE) return false;
        SP_DEVICE_INTERFACE_DATA iface={.cbSize=sizeof iface};
        SP_DEVINFO_DATA info={.cbSize=sizeof info}; DWORD needed=0;
        bool ok=false;
        if (SetupDiOpenDeviceInterfaceW(set,value,0,&iface)) {
            SetupDiGetDeviceInterfaceDetailW(set,&iface,NULL,0,&needed,NULL);
            SP_DEVICE_INTERFACE_DETAIL_DATA_W *detail=calloc(1,needed);
            if (detail) {
                detail->cbSize=sizeof *detail;
                if (SetupDiGetDeviceInterfaceDetailW(set,&iface,detail,needed,NULL,&info)) {
                    DEVINST node=info.DevInst;
                    for (unsigned i=0;i<5;i++) {
                        if (CM_Get_Device_IDW(node,instance,MAX_DEVICE_ID_LEN,0)!=CR_SUCCESS) break;
                        if (!_wcsnicmp(instance,L"USB\\VID_1D50&PID_606F\\",22)) { ok=true; break; }
                        DEVINST parent;
                        if (CM_Get_Parent(&parent,node,0)!=CR_SUCCESS) break;
                        node=parent;
                    }
                }
                free(detail);
            }
        }
        SetupDiDestroyDeviceInfoList(set);
        if (!ok) return false;
        serial=wcsrchr(instance,L'\\'); if (!serial) return false; serial++;
    }
    if (!WideCharToMultiByte(CP_UTF8,0,serial,-1,name,(int)capacity,NULL,NULL)||!*name) return false;
    for (char *p=name;*p;p++) if (!isalnum((unsigned char)*p)&&*p!='-'&&*p!='_') return false;
    return true;
}
double piper_now(void) {
    LARGE_INTEGER ticks,freq; QueryPerformanceCounter(&ticks); QueryPerformanceFrequency(&freq);
    return (double)ticks.QuadPart/(double)freq.QuadPart;
}
static bool windows_load(void) {
    if (win.dll) return true;
    HMODULE module=NULL; wchar_t path[32768];
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,(LPCWSTR)(uintptr_t)&piper_now,&module)) return false;
    DWORD n=GetModuleFileNameW(module,path,32768); if (!n||n>=32750) return false;
    wchar_t *end=wcsrchr(path,L'\\'); if (!end) return false;
    wcscpy(end+1,L"piper-cando.dll");
    HMODULE dll=LoadLibraryExW(path,NULL,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!dll) return false;
#define BIND(member,name) do { FARPROC p=GetProcAddress(dll,name); if (!p) { FreeLibrary(dll); return false; } memcpy(&win.member,&p,sizeof p); } while (0)
    BIND(list_alloc,"cando_list_malloc"); BIND(list_free,"cando_list_free"); BIND(list_scan,"cando_list_scan");
    BIND(num,"cando_list_num"); BIND(alloc,"cando_malloc"); BIND(free,"cando_free");
    BIND(last_error,"candle_dev_last_error");
    BIND(last_rx_bytes,"cando_last_rx_bytes");
    BIND(rx_ready,"cando_rx_ready"); BIND(rx_next,"cando_rx_next");
    BIND(open,"cando_open"); BIND(close,"cando_close"); BIND(stop,"cando_stop");
    BIND(serial,"cando_get_serial_number_str"); BIND(info,"cando_get_dev_info");
    BIND(timing,"cando_set_timing"); BIND(start,"cando_start"); BIND(send,"cando_frame_send"); BIND(read,"cando_frame_read");
#undef BIND
    win.dll=dll; return true;
}
static void lost(struct can_device *d,const char *why) {
    if (d->fd>=0&&d->fd<CANBUS_MAX&&win.handles[d->fd]) {
        win.stop(win.handles[d->fd]); win.close(win.handles[d->fd]); win.free(win.handles[d->fd]); win.handles[d->fd]=NULL;
    }
    d->fd=-1; d->up=false; d->present=false; d->link_verified=false;
    memset(&d->feedback,0,sizeof d->feedback); d->stationary_since=0;
    snprintf(d->problem,sizeof d->problem,"%s",why);
}
static void windows_scan(struct can_bus *b) {
    if (!windows_load()) return;
    bool active=false;
    for (unsigned i=0;i<CANBUS_MAX;i++) {
        struct can_device *d=&b->devices[i];
        if (d->fd>=0) {
            uint64_t arrival;
            if (!windows_connection(d->id+4,&arrival)||arrival!=win.arrivals[d->fd]) lost(d,"Windows USB-CAN connection changed");
            else active=true;
        } else if (*d->id && !strncmp(d->id,"usb:",4)) {
            uint64_t arrival;
            if (!windows_connection(d->id+4,&arrival)) {
                d->present=d->up=d->link_verified=false;
                snprintf(d->problem,sizeof d->problem,"Windows USB-CAN adapter not enumerated");
            }
        }
    }
    /* Do not reopen another process's device or disturb active handles. An
       explicit Reconnect refreshes adapter enumeration and connection identity. */
    if (active) return;
    void *list=NULL; uint8_t count=0;
    if (!win.list_alloc(&list)) return;
    if (!win.list_scan(list)||!win.num(list,&count)) { win.list_free(list); return; }
    for (unsigned i=0;i<count&&i<CANBUS_MAX;i++) {
        void *handle=NULL;
        if (!win.alloc(list,(uint8_t)i,&handle)) continue;
        const wchar_t *serial=win.serial(handle); char id[160]="",name[128]="";
        uint64_t arrival;
        bool valid=windows_identity(serial,name,sizeof name)&&windows_connection(name,&arrival);
        if (!valid) { win.free(handle); continue; }
        snprintf(id,sizeof id,"usb:%s",name);
        struct can_device *d=can_bus_find(b,id);
        if (!d) for (unsigned k=0;k<CANBUS_MAX;k++) if (!*b->devices[k].id) { d=&b->devices[k]; break; }
        if (!d||d->fd>=0) { win.free(handle); continue; }
        snprintf(d->id,sizeof d->id,"%s",id);
        snprintf(d->name,sizeof d->name,"Cando %u",i);
        snprintf(d->path,sizeof d->path,"Windows USB-CAN %s",name);
        d->present=true;
        if (!win.open(handle)) {
            DWORD error=GetLastError(); int transport=win.last_error(handle);
            d->up=d->link_verified=false;
            snprintf(d->problem,sizeof d->problem,"Windows USB-CAN initialization failed (transport error %d, WinUSB error %lu)",transport,(unsigned long)error);
            win.free(handle); continue;
        }
        unsigned slot=(unsigned)(d-b->devices); struct win_timing timing={1,12,2,1,3};
        /* gs_usb bit 8 is CAN FD, not the vendor DLL's NO_ECHO_BACK option.
           Classic CAN uses one-shot only; receive filters transmit echoes.
           No enable/motion/reset frame is sent during transport setup. */
        if (!win.stop(handle)||!win.timing(handle,&timing)||!win.start(handle,1u<<3)) {
            win.close(handle); win.free(handle); continue;
        }
        win.handles[slot]=handle; win.arrivals[slot]=arrival; d->fd=(int)slot; d->index=i;
        snprintf(d->id,sizeof d->id,"%s",id); snprintf(d->name,sizeof d->name,"Cando %u",i);
        snprintf(d->path,sizeof d->path,"Windows USB-CAN %s",name);
        d->present=d->up=d->stable=d->link_verified=true; d->bitrate=1000000; d->bus_state=0; d->control_flags=0;
        d->generation=++b->sequence; d->problem[0]=0; memset(&d->feedback,0,sizeof d->feedback);
    }
    win.list_free(list);
}
static void windows_receive(struct can_bus *b,double now) {
    for (unsigned i=0;i<CANBUS_MAX;i++) {
        struct can_device *d=&b->devices[i]; if (d->fd<0) continue;
        for (unsigned budget=0;budget<512;budget++) {
            struct win_frame frame={0};
            if (!win.read(win.handles[d->fd],&frame,0)) {
                DWORD error=GetLastError(); int transport=win.last_error(win.handles[d->fd]);
                if (transport==15 && !piper_recent(d->last_rx,now) && win.rx_ready(win.handles[d->fd]))
                    snprintf(d->problem,sizeof d->problem,"Windows USB-CAN receive order stalled (next %lu, ready %08lx)",(unsigned long)win.rx_next(win.handles[d->fd]),(unsigned long)win.rx_ready(win.handles[d->fd]));
                if (transport!=15) snprintf(d->problem,sizeof d->problem,"Windows USB-CAN receive failed (transport %d, WinUSB %lu, bytes %lu)",transport,(unsigned long)error,(unsigned long)win.last_rx_bytes(win.handles[d->fd]));
                break;
            }
            if (!strncmp(d->problem,"Windows USB-CAN receive ",sizeof("Windows USB-CAN receive ")-1)) d->problem[0]=0;
            if (frame.echo!=0xffffffffu) { d->local++; continue; }
            if (frame.id&0x20000000u) { d->errors++; continue; }
            if (frame.len>8||frame.id>0x7ff) continue;
            struct can_frame f={.can_id=frame.id,.len=frame.len}; memcpy(f.data,frame.data,frame.len);
            can_bus_ingest(d,&f,false,now);
        }
    }
}
static bool windows_send(struct can_device *d,const struct can_frame *f) {
    /* gs_usb copies this ID into transmit echoes. 0xffffffff is reserved
       for actual bus reception, so using it here defeats echo filtering. */
    struct win_frame frame={.echo=0,.id=f->can_id,.len=f->len}; memcpy(frame.data,f->data,8);
    if (win.send(win.handles[d->fd],&frame)) return true;
    snprintf(d->problem,sizeof d->problem,"Windows USB-CAN send failed (WinUSB error %lu)",(unsigned long)GetLastError()); return false;
}

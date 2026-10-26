#define _GNU_SOURCE
#include "protocol.h"
#include <errno.h>
#include <getopt.h>
#include <limits.h>
#include <linux/can.h>
#include <linux/can/error.h>
#include <linux/can/raw.h>
#include <net/if.h>
#include <net/if_arp.h>
#include <poll.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

#define MAX_ADAPTERS 32
struct adapter {
    char name[IF_NAMESIZE];
    unsigned index, generation;
    bool present, queried, fw_reply;
    int fd;
    unsigned long long rx, local, errors, malformed, decoded;
    struct piper_feedback feedback;
};
static struct adapter adapters[MAX_ADAPTERS];
static volatile sig_atomic_t interrupted;

static void on_signal(int signal_number) { (void)signal_number; interrupted = 1; }

static double monotonic(void)
{
    struct timespec ts;
    if (clock_gettime(CLOCK_MONOTONIC, &ts) < 0) { perror("clock_gettime"); exit(1); }
    return (double)ts.tv_sec + (double)ts.tv_nsec / 1e9;
}

static bool read_number(const char *name, const char *field, unsigned long long *value)
{
    char path[PATH_MAX];
    snprintf(path, sizeof path, "/sys/class/net/%s/%s", name, field);
    FILE *file = fopen(path, "r");
    if (!file) return false;
    char text[80], *end;
    bool ok = fgets(text, sizeof text, file) != NULL;
    fclose(file);
    if (!ok) return false;
    errno = 0;
    *value = strtoull(text, &end, 0);
    return !errno && end != text && (*end == '\0' || *end == '\n');
}

static void disconnect(struct adapter *a, const char *reason)
{
    if (a->fd >= 0) {
        printf("%s: socket closed (%s); feedback invalidated\n", a->name, reason);
        close(a->fd); a->fd = -1;
    }
    memset(&a->feedback, 0, sizeof a->feedback);
    a->fw_reply = false;
}

static void identify(const char *name)
{
    char path[PATH_MAX], resolved[PATH_MAX];
    snprintf(path, sizeof path, "/sys/class/net/%s/device", name);
    if (realpath(path, resolved)) {
        printf("  device=%s\n", resolved);
        /* Walk from the USB interface to its parent USB device. */
        for (unsigned level = 0; level < 5; ++level) {
            char serial_path[PATH_MAX], serial[128];
            int n = snprintf(serial_path, sizeof serial_path, "%s/serial", resolved);
            if (n < 0 || (size_t)n >= sizeof serial_path) break;
            FILE *f = fopen(serial_path, "r");
            if (f) {
                if (fgets(serial, sizeof serial, f)) {
                    serial[strcspn(serial, "\r\n")] = '\0';
                    printf("  adapter serial=%s (identifies the adapter, not the arm)\n", serial);
                }
                fclose(f); break;
            }
            char *slash = strrchr(resolved, '/');
            if (!slash || slash == resolved) break;
            *slash = '\0';
        }
    }
}

static int connect_adapter(struct adapter *a)
{
    int fd = socket(PF_CAN, SOCK_RAW | SOCK_NONBLOCK | SOCK_CLOEXEC, CAN_RAW);
    if (fd < 0) return -1;
    int receive_own = 1;
    can_err_mask_t error_mask = CAN_ERR_MASK;
    struct sockaddr_can addr = {.can_family = AF_CAN, .can_ifindex = (int)a->index};
    if (setsockopt(fd, SOL_CAN_RAW, CAN_RAW_ERR_FILTER, &error_mask, sizeof error_mask) < 0 ||
        setsockopt(fd, SOL_CAN_RAW, CAN_RAW_RECV_OWN_MSGS, &receive_own, sizeof receive_own) < 0 ||
        bind(fd, (struct sockaddr *)&addr, sizeof addr) < 0 ||
        if_nametoindex(a->name) != a->index) {
        int saved = errno ? errno : ENODEV;
        close(fd); errno = saved; return -1;
    }
    a->fd = fd; ++a->generation;
    memset(&a->feedback, 0, sizeof a->feedback);
    printf("%s: opened raw CAN socket; index=%u generation=%u\n", a->name, a->index, a->generation);
    identify(a->name);
    return 0;
}

static int discover(const char *selected, bool query)
{
    for (unsigned i = 0; i < MAX_ADAPTERS; ++i) adapters[i].present = false;
    struct if_nameindex *names = if_nameindex();
    if (!names) { perror("if_nameindex"); return -1; }
    for (struct if_nameindex *it = names; it->if_index; ++it) {
        if (selected && strcmp(selected, it->if_name)) continue;
        unsigned long long type, flags;
        if (!read_number(it->if_name, "type", &type) || type != ARPHRD_CAN) continue;
        struct adapter *a = NULL;
        for (unsigned i = 0; i < MAX_ADAPTERS; ++i)
            if (!strcmp(adapters[i].name, it->if_name)) { a = &adapters[i]; break; }
        if (!a) {
            for (unsigned i = 0; i < MAX_ADAPTERS; ++i)
                if (!adapters[i].name[0]) { a = &adapters[i]; break; }
            if (!a) { fprintf(stderr,"More than %d adapter names; restart the probe\n",MAX_ADAPTERS); if_freenameindex(names); return -1; }
            snprintf(a->name, sizeof a->name, "%s", it->if_name);
        }
        a->present = true;
        if (a->index != it->if_index) {
            disconnect(a, "interface replaced"); a->index = it->if_index;
        }
        if (!read_number(a->name, "flags", &flags) || !(flags & IFF_UP)) {
            disconnect(a, "interface down"); continue;
        }
        if (a->fd < 0 && connect_adapter(a) < 0) {
            fprintf(stderr, "%s: cannot open CAN: %s\n", a->name, strerror(errno)); continue;
        }
        /* This is the sole transmission path: one information request, explicitly selected. */
        if (query && !a->queried) {
            a->queried = true; /* Never retry automatically, including after a reconnect. */
            struct can_frame f = {.can_id = 0x4af, .len = 8, .data = {1}};
            ssize_t written = send(a->fd, &f, sizeof f, MSG_DONTWAIT);
            if (written != (ssize_t)sizeof f)
                fprintf(stderr, "%s: firmware request not queued: %s\n", a->name, written < 0 ? strerror(errno) : "short send");
            else
                printf("%s: firmware query 4AF#0100000000000000 queued; this is not a robot acknowledgment\n", a->name);
        }
    }
    if_freenameindex(names);
    for (unsigned i = 0; i < MAX_ADAPTERS; ++i)
        if (adapters[i].name[0] && !adapters[i].present) disconnect(&adapters[i], "interface removed");
    return 0;
}

static void receive_frames(struct adapter *a)
{
    /* Bound work so a busy bus cannot starve rediscovery, deadlines or Ctrl-C. */
    for (unsigned budget = 0; budget < 2048 && !interrupted; ++budget) {
        struct can_frame frame;
        struct iovec iov = {.iov_base = &frame, .iov_len = sizeof frame};
        struct msghdr message = {.msg_iov = &iov, .msg_iovlen = 1};
        ssize_t count = recvmsg(a->fd, &message, MSG_DONTWAIT);
        if (count < 0) {
            if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR) return;
            disconnect(a, strerror(errno)); return;
        }
        if (count != (ssize_t)sizeof frame || (message.msg_flags & MSG_TRUNC) || frame.len > 8) {
            ++a->malformed; continue;
        }
        if (frame.can_id & CAN_ERR_FLAG) {
            ++a->errors;
            if (a->errors <= 8)
                printf("%s: CAN error flags=0x%08X%s%s\n",a->name,frame.can_id & CAN_ERR_MASK,
                       frame.can_id & CAN_ERR_ACK ? " ACK missing" : "",
                       frame.can_id & CAN_ERR_BUSOFF ? " BUS-OFF" : "");
            continue;
        }
        if (message.msg_flags & MSG_DONTROUTE) {
            ++a->local; /* Local sends/echoes are never decoded as robot feedback. */
            continue;
        }
        ++a->rx;
        if (piper_decode(&a->feedback, frame.can_id, frame.data, frame.len, monotonic())) ++a->decoded;
        if (frame.can_id == 0x4af && frame.len) {
            a->fw_reply = true;
            printf("%s: firmware reply bytes=",a->name);
            for (unsigned i = 0; i < frame.len; ++i) printf("%02X",frame.data[i]);
            printf(" ascii=");
            for (unsigned i = 0; i < frame.len; ++i)
                putchar(frame.data[i] >= 32 && frame.data[i] <= 126 ? frame.data[i] : '.');
            putchar('\n');
        }
    }
}

static unsigned report(double now)
{
    unsigned present = 0, identified = 0;
    for (unsigned i = 0; i < MAX_ADAPTERS; ++i) {
        struct adapter *a = &adapters[i];
        if (!a->name[0]) continue;
        if (a->present) ++present;
        printf("%s: %s | external RX=%llu local/echo=%llu errors=%llu malformed=%llu PiPer frames=%llu\n",
               a->name, !a->present ? "MISSING" : a->fd < 0 ? "NOT OPEN (check interface UP/permissions)" : "LISTENING",
               a->rx,a->local,a->errors,a->malformed,a->decoded);
        unsigned fresh_banks = 0;
        for (unsigned bank = 0; bank < 3; ++bank) {
            const struct piper_sample *s = &a->feedback.arm[bank];
            bool seen = s->status_seen;
            for (unsigned joint=0;joint<6;++joint) seen = seen || s->joint_seen[joint];
            if (!seen) continue;
            bool fresh = piper_fresh(s,now);
            if (fresh && a->present && a->fd >= 0) ++fresh_banks;
            printf("  0x%03X: %s |",0x2a1+bank*16,fresh ? "FRESH status + six joints" : "INCOMPLETE/STALE feedback");
            for (unsigned joint=0;joint<6;++joint) {
                if (s->joint_seen[joint]) printf(" J%u=%.3f%s",joint+1,s->deg[joint],now-s->joint_at[joint] > PIPER_FRESH_SECONDS ? "(stale)" : "");
                else printf(" J%u=unknown",joint+1);
            }
            if (s->status_seen) printf(" | control=%u arm_status=%u mode=%u fault=0x%04X%s",
                s->control,s->arm_status,s->motion_mode,s->error,now-s->status_at > PIPER_FRESH_SECONDS ? " (status stale)" : "");
            putchar('\n');
        }
        if (fresh_banks == 1) ++identified;
        if (fresh_banks > 1) printf("  AMBIGUOUS: multiple fresh PiPer address ranges on this adapter\n");
        if (a->rx == 0) printf("  No external CAN frames received; USB/interface detection alone does not establish an arm connection.\n");
        if (a->queried && !a->fw_reply) printf("  No external firmware reply observed.\n");
        const char *fields[] = {"statistics/rx_packets", "statistics/tx_packets", "statistics/rx_errors", "statistics/tx_errors"};
        printf("  kernel");
        for (unsigned field=0;field<4;++field) {
            unsigned long long value;
            if (read_number(a->name,fields[field],&value)) printf(" %s=%llu",strchr(fields[field],'/')+1,value);
        }
        putchar('\n');
    }
    if (!present) printf("No matching CAN interfaces. Connect the adapter and check Linux driver enumeration.\n");
    return identified;
}

static void usage(FILE *out)
{
    fprintf(out,"Usage: piper-can [--scan] [--interface NAME] [--seconds N | --watch] [--query-firmware]\n"
                "Default: discover and listen for 5 seconds, transmit nothing.\n"
                "--watch: monitor continuously and reopen sockets after interface changes.\n"
                "--query-firmware: send one information request; requires --interface.\n"
                "No enable, reset, mode, calibration or movement commands exist in this program.\n"
                "Exit 0: one fresh PiPer feedback set on at least one adapter; 2: not established; 1: usage/system error.\n");
}

int main(int argc, char **argv)
{
    const char *selected = NULL;
    bool query = false;
    long seconds = 5;
    const struct option options[] = {
        {"scan",no_argument,NULL,'s'},{"interface",required_argument,NULL,'i'},
        {"seconds",required_argument,NULL,'t'},{"watch",no_argument,NULL,'w'},
        {"query-firmware",no_argument,NULL,'q'},{"help",no_argument,NULL,'h'},{0}
    };
    int option;
    while ((option = getopt_long(argc,argv,"",options,NULL)) != -1) {
        switch (option) {
        case 's': break;
        case 'i':
            if (!*optarg || strlen(optarg) >= IF_NAMESIZE || strchr(optarg,'/')) { usage(stderr); return 1; }
            selected = optarg; break;
        case 't': {
            char *end; errno = 0; seconds = strtol(optarg,&end,10);
            if (errno || end == optarg || *end || seconds < 1 || seconds > 3600) { usage(stderr); return 1; }
            break;
        }
        case 'w': seconds = 0; break;
        case 'q': query = true; break;
        case 'h': usage(stdout); return 0;
        default: usage(stderr); return 1;
        }
    }
    if (optind != argc || (query && !selected)) { usage(stderr); return 1; }
    for (unsigned i = 0; i < MAX_ADAPTERS; ++i) adapters[i].fd = -1;
    struct sigaction action = {.sa_handler = on_signal};
    sigemptyset(&action.sa_mask);
    if (sigaction(SIGINT,&action,NULL) < 0 || sigaction(SIGTERM,&action,NULL) < 0) { perror("sigaction"); return 1; }
    setvbuf(stdout,NULL,_IOLBF,0);
    printf("PiPer direct C CAN probe | %s | no SDK or dashboard\n",query ? "ONE firmware information request" : "PASSIVE (zero transmissions)");
    double start = monotonic(), next_scan = start, next_report = start+1;
    int result = 2;
    while (!interrupted && (!seconds || monotonic()-start < (double)seconds)) {
        double now = monotonic();
        if (now >= next_scan) {
            if (discover(selected,query) < 0) { result=1; break; }
            next_scan = now+1;
        }
        struct pollfd fds[MAX_ADAPTERS];
        struct adapter *map[MAX_ADAPTERS];
        nfds_t count = 0;
        for (unsigned i=0;i<MAX_ADAPTERS;++i) if (adapters[i].fd >= 0) {
            fds[count] = (struct pollfd){.fd=adapters[i].fd,.events=POLLIN};
            map[count++] = &adapters[i];
        }
        int ready = poll(fds,count,100);
        if (ready < 0 && errno != EINTR) { perror("poll"); result=1; break; }
        for (nfds_t i=0;i<count && ready>0;++i) {
            if (fds[i].revents & POLLIN) receive_frames(map[i]);
            if (fds[i].revents & (POLLERR|POLLHUP|POLLNVAL)) disconnect(map[i],"socket/interface error");
        }
        now = monotonic();
        if (now >= next_report) { report(now); next_report=now+1; }
    }
    printf("Final connection report after %.1f seconds:\n",monotonic()-start);
    unsigned connected = report(monotonic());
    if (result != 1) result = connected ? 0 : 2;
    for (unsigned i=0;i<MAX_ADAPTERS;++i) if (adapters[i].fd >= 0) close(adapters[i].fd);
    printf("%u adapter(s) with one fresh complete PiPer feedback set. This is not permission or readiness to move.\n",connected);
    return result;
}

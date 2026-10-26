#define _GNU_SOURCE
#include "camera.h"
#include "canbus.h"
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/wait.h>
#include <unistd.h>

#define FRAME_MAX (2u*1024u*1024u)
static struct {
    char device[512], error[192];
    pid_t pid;
    int fd;
    bool wanted;
    unsigned char *pending,*jpeg;
    size_t used,size;
    unsigned long sequence;
    double frame_at,started,stopping;
} camera={.fd=-1};

bool camera_init(const char *device)
{
    if (strlen(device)>=sizeof camera.device||(*device&&strncmp(device,"/dev/",5))) return false;
    snprintf(camera.device,sizeof camera.device,"%s",device);
    return true;
}
void camera_stop(void)
{
    camera.wanted=false; camera.size=0; camera.used=0;
    if (camera.fd>=0) { close(camera.fd); camera.fd=-1; }
    if (camera.pid&&!camera.stopping) { kill(camera.pid,SIGTERM); camera.stopping=piper_now(); }
}
const char *camera_start(void)
{
    if (!*camera.device) return "No bench camera is configured for this server.";
    if (camera.wanted&&camera.pid) return NULL;
    if (camera.pid) return "Camera is stopping. Try Start camera again in a moment.";
    if (!camera.pending) camera.pending=malloc(FRAME_MAX);
    if (!camera.jpeg) camera.jpeg=malloc(FRAME_MAX);
    if (!camera.pending||!camera.jpeg) return "Camera buffer allocation failed.";
    int pipefd[2]; if (pipe2(pipefd,O_CLOEXEC)) return "Cannot open camera capture pipe.";
    pid_t parent=getpid(),child=fork();
    if (child<0) { close(pipefd[0]); close(pipefd[1]); return "Cannot start camera capture."; }
    if (!child) {
        signal(SIGTERM,SIG_DFL); signal(SIGINT,SIG_DFL); signal(SIGPIPE,SIG_DFL);
        prctl(PR_SET_PDEATHSIG,SIGTERM);
        if (getppid()!=parent) _exit(1);
        if (dup2(pipefd[1],STDOUT_FILENO)<0) _exit(1);
        close_range(3,~0u,0);
        execl("/usr/bin/ffmpeg","ffmpeg","-nostdin","-hide_banner","-loglevel","error",
              "-f","v4l2","-input_format","mjpeg","-video_size","1280x720","-framerate","15",
              "-i",camera.device,"-an","-c:v","copy","-f","mjpeg","pipe:1",(char *)NULL);
        _exit(127);
    }
    close(pipefd[1]); camera.fd=pipefd[0];
    fcntl(camera.fd,F_SETFL,O_NONBLOCK);
    camera.pid=child; camera.wanted=true; camera.stopping=0; camera.started=piper_now();
    camera.size=camera.used=0; camera.error[0]=0; camera.frame_at=0;
    return NULL;
}
void camera_tick(double now)
{
    if (camera.pid) {
        int status; pid_t exited=waitpid(camera.pid,&status,WNOHANG);
        if (exited==camera.pid) {
            camera.pid=0;
            if (camera.wanted) snprintf(camera.error,sizeof camera.error,"Logitech camera stopped. Check its USB connection or close other camera apps, then press Start camera.");
            camera_stop(); camera.stopping=0;
        } else if (camera.stopping&&now-camera.stopping>1) kill(camera.pid,SIGKILL);
    }
    if (!camera.wanted||camera.fd<0) return;
    /* Bound camera work per tick. Backpressure affects only the video child,
       never CAN polling, motion deadlines or the HTTP control lease. */
    for (unsigned budget=0;budget<4;++budget) {
        size_t available=FRAME_MAX-camera.used;
        if (!available) { snprintf(camera.error,sizeof camera.error,"Camera frame exceeds the supported size."); camera_stop(); return; }
        ssize_t n=read(camera.fd,camera.pending+camera.used,available<65536?available:65536);
        if (n<=0) break;
        camera.used+=(size_t)n;
        size_t start=0;
        while (start+1<camera.used&&(camera.pending[start]!=0xff||camera.pending[start+1]!=0xd8)) ++start;
        if (start) { memmove(camera.pending,camera.pending+start,camera.used-start); camera.used-=start; }
        for (size_t i=2;i+1<camera.used;++i) if (camera.pending[i]==0xff&&camera.pending[i+1]==0xd9) {
            camera.size=i+2; memcpy(camera.jpeg,camera.pending,camera.size);
            memmove(camera.pending,camera.pending+camera.size,camera.used-camera.size); camera.used-=camera.size;
            camera.frame_at=now; ++camera.sequence; i=1;
        }
    }
    if (now-(camera.frame_at?camera.frame_at:camera.started)>5) {
        snprintf(camera.error,sizeof camera.error,"No fresh webcam frames. Reconnect the Logitech camera and press Start camera."); camera_stop();
    }
}
const unsigned char *camera_frame(double now,size_t *size)
{
    *size=0;
    if (!camera.wanted||!camera.size||now-camera.frame_at>1.5) return NULL;
    *size=camera.size; return camera.jpeg;
}
cJSON *camera_state(double now)
{
    cJSON *s=cJSON_CreateObject();
    cJSON_AddBoolToObject(s,"configured",*camera.device!=0);
    cJSON_AddBoolToObject(s,"running",camera.wanted&&camera.pid);
    cJSON_AddBoolToObject(s,"fresh",camera.wanted&&camera.size&&now-camera.frame_at<=1.5);
    cJSON_AddNumberToObject(s,"sequence",(double)camera.sequence);
    cJSON_AddStringToObject(s,"name","Logitech C930e");
    cJSON_AddStringToObject(s,"error",camera.error);
    return s;
}
void camera_close(void)
{
    camera_stop();
    if (camera.pid) { kill(camera.pid,SIGKILL); while (waitpid(camera.pid,NULL,0)<0&&errno==EINTR) {} }
    free(camera.pending); free(camera.jpeg);
}

# Dot-source only. The one source of the standalone Windows dashboard's
# launch defaults, shared by Start-WindowsDashboard.ps1 and
# Restart-WindowsDashboard.ps1 so the two can never disagree.
#
# Precedence: an explicit script argument, then the saved Pika assignment in
# <state-dir>/pika-default.json ({ "device": ..., "distribution": ... }), then
# the built-in defaults below. The port matches Piper.Host's
# HostRoutes.DefaultHttpPort.
function Get-PiperDashboardDefaults([Parameter(Mandatory=$true)][string]$StateDir) {
    $taskSaved=$null
    $taskSavedPath=Join-Path $StateDir 'pika-default.json'
    if(Test-Path -LiteralPath $taskSavedPath) { $taskSaved=Get-Content -LiteralPath $taskSavedPath -Raw | ConvertFrom-Json }
    [pscustomobject]@{
        Port=8765
        Distribution=$(if($taskSaved -and $taskSaved.distribution){[string]$taskSaved.distribution}else{'Ubuntu'})
        # Null when nothing is saved; the supervisor may then discover one unambiguous Pika.
        PikaDevice=$(if($taskSaved){[string]$taskSaved.device}else{$null})
    }
}

# usbipd-win installs under Program Files. PIPER_USBIPD names another copy;
# a child process cannot be given a different Program Files, so this is also
# how the supervisor fixture supplies its stand-in.
function Get-PiperUsbipdPath { if($env:PIPER_USBIPD){$env:PIPER_USBIPD}else{Join-Path $env:ProgramFiles 'usbipd-win/usbipd.exe'} }

# One supervisor per port. The launcher holds this mutex for its whole life,
# so the name is defined once for the launcher and for whoever waits on it.
function Get-PiperSupervisorMutexName([Parameter(Mandatory=$true)][int]$Port) { "Local\LuckyRobotsPiperDashboard.$Port" }

# True once no supervisor holds the port's mutex. The backend exits before its
# supervisor does, so a relauncher waits here instead of guessing a delay.
function Wait-PiperSupervisorReleased([Parameter(Mandatory=$true)][int]$Port,[Parameter(Mandatory=$true)][int]$TimeoutSeconds) {
    $taskMutex=New-Object Threading.Mutex($false,(Get-PiperSupervisorMutexName $Port))
    try {
        # A supervisor that died without releasing still left the mutex free.
        try { $taskFree=$taskMutex.WaitOne([TimeSpan]::FromSeconds($TimeoutSeconds)) } catch [Threading.AbandonedMutexException] { $taskFree=$true }
        if($taskFree) { $taskMutex.ReleaseMutex() }
        return $taskFree
    } finally { $taskMutex.Dispose() }
}

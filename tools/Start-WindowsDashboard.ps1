# Omitted arguments come from WindowsDashboardDefaults.ps1.
param(
    [string]$Distribution='',
    [string]$PikaDevice='',
    [int]$Port=0
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'WindowsDashboardDefaults.ps1')
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskHost=Join-Path $taskRoot 'build/publish-windows/Piper.Host.exe'
$taskUsb=Get-PiperUsbipdPath
$taskStateDir=Join-Path $taskRoot 'data-windows-managed'
if (!(Test-Path $taskHost) -or !(Test-Path $taskUsb)) { throw 'Build the Windows host and install usbipd-win first. See managed/README.md.' }
$taskDefaults=Get-PiperDashboardDefaults $taskStateDir
if(!$PSBoundParameters.ContainsKey('Port')){$Port=$taskDefaults.Port}
if(!$PSBoundParameters.ContainsKey('Distribution')){$Distribution=$taskDefaults.Distribution}
if(!$PSBoundParameters.ContainsKey('PikaDevice')) {
    if($null -ne $taskDefaults.PikaDevice) {
        $PikaDevice=$taskDefaults.PikaDevice
    } else {
        # One unambiguous already-shared Pika is safe to discover. With several
        # identical units the user must save a default/assign robot profiles.
        $taskSharedPikas=@((& $taskUsb state | ConvertFrom-Json).Devices | Where-Object {
            $_.InstanceId -match '^USB\\VID_1A86&PID_7522\\' -and $_.IsForced -and $_.BusId
        })
        if($taskSharedPikas.Count -eq 1){$PikaDevice='/dev/pika/by-host/'+$taskSharedPikas[0].BusId}
    }
}
if($PikaDevice -and ($PikaDevice.Length -gt 1024 -or $PikaDevice -notlike '/dev/*' -or $PikaDevice -match '["\x00-\x1f]')){throw 'Invalid saved Pika Linux device identity.'}
if(!$Distribution -or $Distribution.Length -gt 128 -or $Distribution -match '["\x00-\x1f]'){throw 'Invalid saved WSL distribution.'}
if($Port -lt 1 -or $Port -gt 65535){throw 'Invalid dashboard port.'}
# Windows can retain ownerless Listen records briefly after process exit.
# The supervisor mutex and the server's exclusive bind remain authoritative.
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Where-Object {$_.OwningProcess -gt 0}) { throw 'A dashboard is already running on this port.' }
New-Item -ItemType Directory -Force $taskStateDir | Out-Null
$taskSupervisorMutex=New-Object Threading.Mutex($false,(Get-PiperSupervisorMutexName $Port))
try { $taskOwnsSupervisor=$taskSupervisorMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $taskOwnsSupervisor=$true }
if (!$taskOwnsSupervisor) { $taskSupervisorMutex.Dispose(); throw 'A dashboard supervisor is already running.' }
$taskKeepAlive=$null
$taskSupervisorLog=Join-Path $taskStateDir 'supervisor.log'
$taskSupervisorIdentity=[guid]::NewGuid().ToString('N')
function Write-Supervisor([string]$Message) { Add-Content -LiteralPath $taskSupervisorLog -Value ((Get-Date -Format o)+' '+$Message) }
function Connect-Pika {
    # Match only Pika; never bind drivers or capture the native CAN adapter.
    $taskFound=@((& $taskUsb state | ConvertFrom-Json).Devices | Where-Object { $_.InstanceId -match '^USB\\VID_1A86&PID_7522\\' })
    foreach($taskPika in $taskFound | Where-Object {$_.BusId -and $_.IsForced -and !$_.ClientIPAddress}) {
        $ErrorActionPreference='Continue'
        & $taskUsb attach --wsl --busid $taskPika.BusId 2>> (Join-Path $taskStateDir 'usb-attach-error.log')
        $taskAttachExit=$LASTEXITCODE
        $ErrorActionPreference='Stop'
        if ($taskAttachExit -ne 0) { Write-Supervisor 'Pika USB attachment unavailable; the arm dashboard continues.' }
    }
}
try {
    $taskKeepAlive=Start-Process wsl.exe -ArgumentList @('--distribution',$Distribution,'--exec','sleep','infinity') -WindowStyle Hidden -PassThru
    & wsl.exe --distribution $Distribution --exec true
    if ($LASTEXITCODE -ne 0) { throw 'WSL did not start' }
    try { Connect-Pika } catch { Write-Supervisor ('Pika setup: '+$_.Exception.Message) }
    $taskPackagedAgent=Join-Path (Split-Path $taskHost -Parent) 'pika-agent/Piper.Pika.Agent'
    # Compatibility for an existing standalone installation. Plugin packages
    # include this agent and never use the legacy /opt deployment.
    $taskAgent=if(Test-Path -LiteralPath $taskPackagedAgent){$taskPackagedAgent}else{'/opt/luckyrobots/piper/pika/Piper.Pika.Agent'}
    $taskArguments=@('--port',"$Port",'--web-root',('"'+(Join-Path $taskRoot 'web')+'"'),'--state-dir',('"'+$taskStateDir+'"'),'--pika-wsl',('"'+$Distribution+'"'),'--pika-device',('"'+$PikaDevice+'"'),'--pika-agent',('"'+$taskAgent+'"'))
    $taskArguments+=@('--restart-supervisor-id',$taskSupervisorIdentity,'--restart-supervisor-pid',"$PID")
    $taskCrashCount=0
    while ($true) {
        $taskStamp=Get-Date -Format 'yyyyMMdd-HHmmss-fff'
        $taskStarted=Get-Date
        $taskServer=Start-Process $taskHost -ArgumentList $taskArguments -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskStateDir "dashboard-$taskStamp.log") -RedirectStandardError (Join-Path $taskStateDir "dashboard-$taskStamp-error.log")
        # Cache the process handle before it exits. Windows PowerShell otherwise
        # sometimes exposes a null ExitCode for a Start-Process child.
        $null=$taskServer.Handle
        Write-Supervisor "Started backend PID $($taskServer.Id); monitoring only."
        Write-Output "Dashboard http://127.0.0.1:$Port/ (process $($taskServer.Id))."
        while (!$taskServer.WaitForExit(2000)) {
            try {
                if ($taskKeepAlive.HasExited) {
                    $taskKeepAlive.Dispose()
                    $taskKeepAlive=Start-Process wsl.exe -ArgumentList @('--distribution',$Distribution,'--exec','sleep','infinity') -WindowStyle Hidden -PassThru
                }
                Connect-Pika
            } catch { Write-Supervisor ('USB monitor: '+$_.Exception.Message) }
        }
        $taskServer.WaitForExit()
        $taskExit=$taskServer.ExitCode
        if ($null -eq $taskExit) { throw 'Backend exit code was unavailable; stopping supervisor instead of guessing.' }
        $taskServer.Dispose()
        if ($taskExit -eq 0) { Write-Supervisor 'Backend shut down cleanly; supervisor stopping.'; break }
        if ($taskExit -eq 75) {
            Write-Supervisor 'Backend completed the explicit idle maintenance restart; starting a fresh monitoring-only session. Motor targets and recovery are not restored or cleared.'
            $taskCrashCount=0
            continue
        }
        if (((Get-Date)-$taskStarted).TotalSeconds -gt 60) { $taskCrashCount=0 }
        $taskCrashCount++
        $taskDelay=[int][Math]::Min(30,2*[Math]::Pow(2,[Math]::Min($taskCrashCount-1,4)))
        Write-Supervisor "Backend exited unexpectedly ($taskExit). Restarting in $taskDelay seconds; motor state and movement are not restored."
        Start-Sleep -Seconds $taskDelay
    }
} finally {
    if ($taskOwnsSupervisor) { $taskSupervisorMutex.ReleaseMutex() }; $taskSupervisorMutex.Dispose()
    if ($taskKeepAlive) {
        if (!$taskKeepAlive.HasExited) { $taskKeepAlive.Kill() }
        $taskKeepAlive.Dispose()
    }
}

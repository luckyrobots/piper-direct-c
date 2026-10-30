param(
    [string]$Distribution='Ubuntu',
    [string]$PikaDevice='/dev/serial/by-id/usb-1a86_USB_Serial-if00-port0',
    [int]$Port=8765
)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskHost=Join-Path $taskRoot 'build/publish-windows/Piper.Host.exe'
$taskUsb=Join-Path $env:ProgramFiles 'usbipd-win/usbipd.exe'
$taskStateDir=Join-Path $taskRoot 'data-windows-managed'
if (!(Test-Path $taskHost) -or !(Test-Path $taskUsb)) { throw 'Build the Windows host and install usbipd-win first. See managed/README.md.' }
# Windows can retain ownerless Listen records briefly after process exit.
# The supervisor mutex and the server's exclusive bind remain authoritative.
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Where-Object {$_.OwningProcess -gt 0}) { throw 'A dashboard is already running on this port.' }
New-Item -ItemType Directory -Force $taskStateDir | Out-Null
$taskSupervisorMutex=New-Object Threading.Mutex($false,"Local\LuckyRobotsPiperDashboard.$Port")
try { $taskOwnsSupervisor=$taskSupervisorMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $taskOwnsSupervisor=$true }
if (!$taskOwnsSupervisor) { $taskSupervisorMutex.Dispose(); throw 'A dashboard supervisor is already running.' }
$taskKeepAlive=$null
$taskSupervisorLog=Join-Path $taskStateDir 'supervisor.log'
function Write-Supervisor([string]$Message) { Add-Content -LiteralPath $taskSupervisorLog -Value ((Get-Date -Format o)+' '+$Message) }
function Connect-Pika {
    # Match only Pika; never bind drivers or capture the native CAN adapter.
    $taskFound=@((& $taskUsb state | ConvertFrom-Json).Devices | Where-Object { $_.InstanceId -match '^USB\\VID_1A86&PID_7522\\' })
    if ($taskFound.Count -eq 1 -and $taskFound[0].BusId -and $taskFound[0].IsForced -and !$taskFound[0].ClientIPAddress) {
        $ErrorActionPreference='Continue'
        & $taskUsb attach --wsl $Distribution --busid $taskFound[0].BusId 2>> (Join-Path $taskStateDir 'usb-attach-error.log')
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
    $taskArguments=@('--port',"$Port",'--web-root',('"'+(Join-Path $taskRoot 'web')+'"'),'--state-dir',('"'+$taskStateDir+'"'),'--pika-wsl',$Distribution,'--pika-device',$PikaDevice,'--pika-agent',('"'+$taskAgent+'"'))
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

# Run without elevation. This restarts the dashboard only; it sends no motor commands.
# Omitted arguments come from WindowsDashboardDefaults.ps1, as for Start-WindowsDashboard.ps1.
param(
    [int]$Port=0,
    [string]$Distribution=''
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'WindowsDashboardDefaults.ps1')
$repo=Split-Path $PSScriptRoot -Parent
$publish=Join-Path $repo 'build\publish-windows'
$log=Join-Path $repo 'data-windows-managed'
$defaults=Get-PiperDashboardDefaults $log
if(!$PSBoundParameters.ContainsKey('Port')){$Port=$defaults.Port}
if(!$PSBoundParameters.ContainsKey('Distribution')){$Distribution=$defaults.Distribution}
if($Port -lt 1 -or $Port -gt 65535){throw 'Invalid dashboard port.'}
if(!$Distribution -or $Distribution.Length -gt 128 -or $Distribution -match '["\x00-\x1f]'){throw 'Invalid WSL distribution.'}
$uri="http://127.0.0.1:$Port"
$state=Invoke-RestMethod "$uri/api/state" -TimeoutSec 5
if ($state.motion.active) { throw 'Wait for the active robot action to finish before restarting.' }
$listener=Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
$serverId=$listener.OwningProcess
if (-not ('PiperProcessIdentity' -as [type])) { Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PiperProcessIdentity {
[DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
[DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageNameW(IntPtr process,uint flags,StringBuilder name,ref int size);
[DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
public static string Path(int pid){var h=OpenProcess(0x1000,false,pid);if(h==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();try{var b=new StringBuilder(32768);int n=b.Capacity;if(!QueryFullProcessImageNameW(h,0,b,ref n))throw new System.ComponentModel.Win32Exception();return b.ToString();}finally{CloseHandle(h);}}
}
"@
}
$expected=Join-Path $publish 'Piper.Host.exe'
if (!$serverId) {
    # Some Windows sessions omit the listener's State while still reporting
    # established connections. Their local-port owner is independently
    # checked against the exact executable path below.
    $portOwners=@(Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue |
        Where-Object { $_.OwningProcess -gt 0 } | Select-Object -ExpandProperty OwningProcess -Unique)
    if ($portOwners.Count -eq 1) { $serverId=$portOwners[0] }
}
if (!$serverId) {
    # Some Windows sessions hide the TCP ownership table. Accept only one
    # exact executable match; shutdown still uses the live API's auth token.
    $hostProcesses=@(Get-CimInstance Win32_Process -Filter "Name='Piper.Host.exe'" | Where-Object { $_.ExecutablePath -eq $expected })
    if ($state.kind -ne 'piper_direct_c' -or $hostProcesses.Count -ne 1) { throw 'Cannot uniquely identify the dashboard backend.' }
    $serverId=$hostProcesses[0].ProcessId
}
if ([PiperProcessIdentity]::Path($serverId) -ne $expected) { throw "Port $Port belongs to another program; refusing to stop it." }
Invoke-RestMethod -Method Post -Uri "$uri/api/shutdown" -Headers @{'X-Piper-Token'=$state.token} -ContentType application/json -Body '{}' -TimeoutSec 10 | Out-Null
$deadline=(Get-Date).AddSeconds(15)
while((Get-Process -Id $serverId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
if(Get-Process -Id $serverId -ErrorAction SilentlyContinue) { throw 'Backend did not exit cleanly; no process was killed.' }
# Apply already-built transport/core updates only after the old backend exits.
$pikaUpdate=Join-Path $repo 'managed\Piper.Pika\bin\Release\net9.0\Piper.Pika.dll'
if(Test-Path -LiteralPath $pikaUpdate) { Copy-Item -LiteralPath $pikaUpdate -Destination (Join-Path $publish 'Piper.Pika.dll') -Force }
& (Join-Path $PSScriptRoot 'build-windows.ps1') -StageOnly -StageTo $publish
Start-Sleep -Seconds 2
$agentUpdate=Join-Path $repo 'build\pika-agent-linux\Piper.Pika.Agent'
$packagedAgent=Join-Path $publish 'pika-agent'
if((Test-Path -LiteralPath $agentUpdate) -and (Test-Path -LiteralPath $packagedAgent)) {
    # The supervisor runs the packaged agent beside the host whenever it exists.
    Copy-Item -Path (Join-Path (Split-Path $agentUpdate -Parent) '*') -Destination $packagedAgent -Recurse -Force
} elseif(Test-Path -LiteralPath $agentUpdate) {
    # Legacy standalone installation without a packaged agent.
    $windowsSource=(Split-Path $agentUpdate -Parent).Replace('\','/')
    $linuxSource=(& wsl.exe -d $Distribution -- wslpath -a $windowsSource | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or !$linuxSource){throw 'Cannot resolve staged Linux agent path.'}
    & wsl.exe -d $Distribution -u root -- cp -a ($linuxSource+'/.') /opt/luckyrobots/piper/pika/
    if($LASTEXITCODE -ne 0){throw 'Could not install the updated Pika agent; dashboard remains stopped.'}
    & wsl.exe -d $Distribution -u root -- chmod +x /opt/luckyrobots/piper/pika/Piper.Pika.Agent
    if($LASTEXITCODE -ne 0){throw 'Could not set agent executable permission.'}
}
$launch=Join-Path $PSScriptRoot 'Start-WindowsDashboard.ps1'
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$supervisor=Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$launch+'"'),'-Port',"$Port",'-Distribution',('"'+$Distribution+'"')) -WorkingDirectory $repo -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $log "launcher-$stamp.log") -RedirectStandardError (Join-Path $log "launcher-$stamp-error.log")
$deadline=(Get-Date).AddSeconds(15)
$ready=$false
while((Get-Date) -lt $deadline) {
    try { $null=Invoke-RestMethod "$uri/api/state" -TimeoutSec 2; $ready=$true; break } catch { }
    if($supervisor.HasExited) { throw "Launcher exited. See $log\launcher-$stamp-error.log" }
    Start-Sleep -Milliseconds 500
}
if(!$ready) { throw "Launcher started but backend is not answering yet. See $log\launcher-$stamp-error.log" }
Write-Host "Dashboard restarted: $uri/"
Write-Host "Supervisor PID $($supervisor.Id) runs in the background. You may close this terminal."
Write-Host 'This confirms the backend is running; robot connection status appears in the dashboard.'

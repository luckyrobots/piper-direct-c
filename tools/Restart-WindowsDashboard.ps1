# Run without elevation. This restarts the dashboard only; it sends no motor commands.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$uri='http://127.0.0.1:8765'
$state=Invoke-RestMethod "$uri/api/state" -TimeoutSec 5
if ($state.motion.active) { throw 'Wait for the active robot action to finish before restarting.' }
$listener=Get-NetTCPConnection -LocalPort 8765 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
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
$expected=Join-Path $repo 'build\publish-windows\Piper.Host.exe'
if (!$serverId) {
    # Some Windows sessions omit the listener's State while still reporting
    # established connections. Their local-port owner is independently
    # checked against the exact executable path below.
    $portOwners=@(Get-NetTCPConnection -LocalPort 8765 -ErrorAction SilentlyContinue |
        Where-Object { $_.OwningProcess -gt 0 } | Select-Object -ExpandProperty OwningProcess -Unique)
    if ($portOwners.Count -eq 1) { $serverId=$portOwners[0] }
}
if (!$serverId) {
    # Some Windows sessions hide the TCP ownership table. Accept only one
    # exact executable match; shutdown still uses the live API's auth token.
    $matches=@(Get-CimInstance Win32_Process -Filter "Name='Piper.Host.exe'" | Where-Object { $_.ExecutablePath -eq $expected })
    if ($state.kind -ne 'piper_direct_c' -or $matches.Count -ne 1) { throw 'Cannot uniquely identify the dashboard backend.' }
    $serverId=$matches[0].ProcessId
}
if ([PiperProcessIdentity]::Path($serverId) -ne $expected) { throw 'Port 8765 belongs to another program; refusing to stop it.' }
Invoke-RestMethod -Method Post -Uri "$uri/api/shutdown" -Headers @{'X-Piper-Token'=$state.token} -ContentType application/json -Body '{}' -TimeoutSec 10 | Out-Null
$deadline=(Get-Date).AddSeconds(15)
while((Get-Process -Id $serverId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
if(Get-Process -Id $serverId -ErrorAction SilentlyContinue) { throw 'Backend did not exit cleanly; no process was killed.' }
# Apply already-built transport/core updates only after the old backend exits.
$pikaUpdate=Join-Path $repo 'managed\Piper.Pika\bin\Release\net9.0\Piper.Pika.dll'
if(Test-Path -LiteralPath $pikaUpdate) { Copy-Item -LiteralPath $pikaUpdate -Destination (Join-Path $repo 'build\publish-windows\Piper.Pika.dll') -Force }
foreach($file in @('piper-core.dll','piper-cando.dll')) {
    $built=Join-Path $repo ('build\windows-x64\'+$file)
    $live=Join-Path $repo ('build\publish-windows\'+$file)
    if((Test-Path -LiteralPath $built) -and ((Get-FileHash -LiteralPath $built).Hash -ne (Get-FileHash -LiteralPath $live).Hash)) {
        Copy-Item -LiteralPath $built -Destination $live -Force
    }
}
Start-Sleep -Seconds 2
$agentUpdate=Join-Path $repo 'build\pika-agent-linux\Piper.Pika.Agent'
if(Test-Path -LiteralPath $agentUpdate) {
    $windowsSource=(Split-Path $agentUpdate -Parent).Replace('\','/')
    $linuxSource=(& wsl.exe -d Ubuntu -- wslpath -a $windowsSource | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or !$linuxSource){throw 'Cannot resolve staged Linux agent path.'}
    & wsl.exe -d Ubuntu -u root -- cp -a ($linuxSource+'/.') /opt/luckyrobots/piper/pika/
    if($LASTEXITCODE -ne 0){throw 'Could not install the updated Pika agent; dashboard remains stopped.'}
    & wsl.exe -d Ubuntu -u root -- chmod +x /opt/luckyrobots/piper/pika/Piper.Pika.Agent
    if($LASTEXITCODE -ne 0){throw 'Could not set agent executable permission.'}
}
$launch=Join-Path $PSScriptRoot 'Start-WindowsDashboard.ps1'
$log=Join-Path $repo 'data-windows-managed'
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$supervisor=Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$launch+'"')) -WorkingDirectory $repo -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $log "launcher-$stamp.log") -RedirectStandardError (Join-Path $log "launcher-$stamp-error.log")
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

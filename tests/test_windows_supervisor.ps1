# Runs the real tools/Start-WindowsDashboard.ps1 against stand-in executables for
# Piper.Host.exe, usbipd.exe and wsl.exe. No hardware, WSL or usbipd-win is needed.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$root=Join-Path ([IO.Path]::GetTempPath()) ('piper-supervisor-'+[guid]::NewGuid().ToString('N'))
$stubs=Join-Path $root 'stubs'
$publish=Join-Path $root 'build/publish-windows'
$state=Join-Path $root 'data-windows-managed'
$launcher=Join-Path $root 'tools/Start-WindowsDashboard.ps1'
$savedProgramFiles=$env:ProgramFiles
$savedPath=$env:PATH
$started=New-Object Collections.Generic.List[Diagnostics.Process]

function Assert([bool]$Condition,[string]$Message) { if(!$Condition){throw "FAIL $Message"} }
function Wait-Until([scriptblock]$Condition,[string]$Message,[int]$Seconds=20) {
    $deadline=(Get-Date).AddSeconds($Seconds)
    while(!(& $Condition)) { if((Get-Date) -gt $deadline){throw "FAIL timed out: $Message"}; Start-Sleep -Milliseconds 100 }
}
# The supervisor exactly as Restart-WindowsDashboard.ps1 starts it.
function Start-Supervisor([int]$Port) {
    $stamp=[guid]::NewGuid().ToString('N')
    $process=Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$launcher+'"'),'-Port',"$Port",'-Distribution','TestDistro') -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root "out-$stamp.log") -RedirectStandardError (Join-Path $root "err-$stamp.log")
    $null=$process.Handle
    $started.Add($process)
    [pscustomobject]@{Process=$process;Error=(Join-Path $root "err-$stamp.log")}
}
function Wait-Supervisor($Supervisor,[int]$Seconds=30) {
    Assert ($Supervisor.Process.WaitForExit($Seconds*1000)) 'supervisor did not exit'
    $Supervisor.Process.WaitForExit()
    [pscustomobject]@{Exit=$Supervisor.Process.ExitCode;Error=[string](Get-Content -LiteralPath $Supervisor.Error -Raw -ErrorAction SilentlyContinue)}
}
function Get-FreePort {
    $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
    $listener.Start(); $port=$listener.LocalEndpoint.Port; $listener.Stop(); $port
}
function Get-Launches { @(Get-Content -LiteralPath (Join-Path $publish 'launches.log') -ErrorAction SilentlyContinue) }
function Get-StubProcesses { @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) }) }

try {
    New-Item -ItemType Directory -Force $stubs,$publish,(Join-Path $root 'tools'),(Join-Path $root 'ProgramFiles/usbipd-win') | Out-Null
    Copy-Item (Join-Path $repo 'tools/Start-WindowsDashboard.ps1'),(Join-Path $repo 'tools/WindowsDashboardDefaults.ps1') (Join-Path $root 'tools')
    . (Join-Path $repo 'tools/WindowsDashboardDefaults.ps1')

    # One stand-in program; it behaves by the name it was copied to.
    $source=Join-Path $stubs 'stub.cs'
    Set-Content -LiteralPath $source -Encoding ASCII -Value @'
using System;
using System.IO;
using System.Threading;
static class Stub {
    static int Main(string[] args) {
        string self = System.Reflection.Assembly.GetExecutingAssembly().Location;
        string name = Path.GetFileNameWithoutExtension(self).ToLowerInvariant(), dir = Path.GetDirectoryName(self);
        if (name == "usbipd") { Console.WriteLine("{\"Devices\":[]}"); return 0; }
        if (name == "wsl") { if (Array.IndexOf(args, "sleep") >= 0) Thread.Sleep(Timeout.Infinite); return 0; }
        // Piper.Host: record the launch, then follow the next line of the script.
        File.AppendAllText(Path.Combine(dir, "launches.log"), string.Join(" ", args) + Environment.NewLine);
        string plan = Path.Combine(dir, "plan.txt");
        string[] steps = File.ReadAllLines(plan);
        File.WriteAllLines(plan, steps.Length > 1 ? new ArraySegment<string>(steps, 1, steps.Length - 1) : new ArraySegment<string>(steps, 0, 0));
        if (steps[0] == "hold") { while (!File.Exists(Path.Combine(dir, "release"))) Thread.Sleep(50); return 0; }
        return int.Parse(steps[0]);
    }
}
'@
    $compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    Assert (Test-Path -LiteralPath $compiler) "the .NET Framework compiler is missing at $compiler"
    $stub=Join-Path $stubs 'stub.exe'
    $compileLog=& $compiler /nologo "/out:$stub" $source 2>&1
    Assert ($LASTEXITCODE -eq 0) "stand-in program did not compile: $compileLog"
    Copy-Item $stub (Join-Path $stubs 'wsl.exe')
    Copy-Item $stub (Join-Path $root 'ProgramFiles/usbipd-win/usbipd.exe')
    $env:ProgramFiles=Join-Path $root 'ProgramFiles'
    $env:PATH=$stubs+';'+$env:PATH

    # Refusal: without a built host nothing is started and no supervisor is claimed.
    $port=Get-FreePort
    $result=Wait-Supervisor (Start-Supervisor $port)
    Assert ($result.Exit -ne 0 -and $result.Error -match 'Build the Windows host') "missing host must be refused: $($result.Exit) $($result.Error)"
    Assert (Wait-PiperSupervisorReleased $port 1) 'a refused launch must not hold the supervisor mutex'
    Assert (!(Test-Path -LiteralPath $state)) 'a refused launch must not create state'
    Write-Output 'PASS missing host refused before anything starts'

    Copy-Item $stub (Join-Path $publish 'Piper.Host.exe')

    # Refusal: invalid arguments never reach the host.
    Set-Content -LiteralPath (Join-Path $publish 'plan.txt') -Value '0'
    $bad=Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$launcher+'"'),'-Port','70000') -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $root 'err-bad.log')
    $null=$bad.Handle; $started.Add($bad); $bad.WaitForExit()
    $badError=[string](Get-Content -Raw (Join-Path $root 'err-bad.log'))
    Assert ($bad.ExitCode -ne 0 -and $badError -match 'Invalid dashboard port') "an invalid port must be refused: $($bad.ExitCode) $badError"
    Assert ((Get-Launches).Count -eq 0) 'an invalid port must not start the host'
    Write-Output 'PASS invalid port refused'

    # Exit 75 relaunches at once, an unexpected exit backs off and relaunches, exit 0 ends supervision.
    Set-Content -LiteralPath (Join-Path $publish 'plan.txt') -Value @('75','7','0')
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $result=Wait-Supervisor (Start-Supervisor $port)
    Assert ($result.Exit -eq 0) "supervisor must end cleanly after exit 0: $($result.Exit) $($result.Error)"
    $launches=Get-Launches
    Assert ($launches.Count -eq 3) "expected three host launches, saw $($launches.Count)"
    Assert ($clock.Elapsed.TotalSeconds -ge 2) 'an unexpected exit must back off before relaunching'
    foreach($line in $launches) {
        Assert ($line -match "--port $port ") "launch must carry the port: $line"
        Assert ($line -match '--pika-wsl TestDistro ') "launch must carry the distribution: $line"
        Assert ($line -match '--restart-supervisor-id [0-9a-f]{32} --restart-supervisor-pid \d+$') "launch must carry the supervisor identity: $line"
    }
    Assert (@($launches | ForEach-Object { ($_ -split ' ')[-3] } | Select-Object -Unique).Count -eq 1) 'one supervisor keeps one identity across relaunches'
    $log=Get-Content -LiteralPath (Join-Path $state 'supervisor.log') -Raw
    Assert ($log -match 'explicit idle maintenance restart') 'exit 75 must be logged as a maintenance restart'
    Assert ($log -match 'exited unexpectedly \(7\)\. Restarting in 2 seconds') 'exit 7 must be logged with its backoff'
    Assert ($log -match 'shut down cleanly; supervisor stopping') 'exit 0 must be logged as a clean stop'
    Assert (Wait-PiperSupervisorReleased $port 1) 'a finished supervisor must release its mutex'
    Wait-Until { (Get-StubProcesses).Count -eq 0 } 'the WSL keepalive must be ended with the supervisor'
    Write-Output 'PASS restart on 75, backoff on crash, stop on 0, keepalive ended'

    # A live supervisor refuses a second one, and a relauncher can wait for it.
    Remove-Item -LiteralPath (Join-Path $publish 'launches.log')
    Set-Content -LiteralPath (Join-Path $publish 'plan.txt') -Value 'hold'
    $first=Start-Supervisor $port
    Wait-Until { (Get-Launches).Count -eq 1 } 'first supervisor must start the host'
    $second=Wait-Supervisor (Start-Supervisor $port)
    Assert ($second.Exit -ne 0 -and $second.Error -match 'A dashboard supervisor is already running') "second supervisor must be refused: $($second.Exit) $($second.Error)"
    Assert ((Get-Launches).Count -eq 1) 'a refused supervisor must not start a second host'
    Assert (!$first.Process.HasExited) 'the refused supervisor must not disturb the running one'
    Assert (!(Wait-PiperSupervisorReleased $port 1)) 'the wait must report a supervisor that is still running'
    Assert (Wait-PiperSupervisorReleased (Get-FreePort) 1) 'another port has its own supervisor mutex'
    Set-Content -LiteralPath (Join-Path $publish 'release') -Value ''
    Assert (Wait-PiperSupervisorReleased $port 20) 'the wait must return once the supervisor has exited'
    Assert ((Wait-Supervisor $first).Exit -eq 0) 'the first supervisor must end cleanly'
    Write-Output 'PASS second supervisor refused; relaunch waits for the mutex'

    # Restart-WindowsDashboard.ps1 waits on that mutex instead of sleeping.
    $restart=Get-Content -LiteralPath (Join-Path $repo 'tools/Restart-WindowsDashboard.ps1') -Raw
    Assert ($restart -match 'Wait-PiperSupervisorReleased \$Port' -and $restart -notmatch 'Start-Sleep -Seconds') 'the restart script must wait for the old supervisor'
    Write-Output 'PASS restart script waits for the previous supervisor'
} finally {
    $env:ProgramFiles=$savedProgramFiles
    $env:PATH=$savedPath
    foreach($process in $started) { if(!$process.HasExited){ $process.Kill() }; $process.Dispose() }
    Get-StubProcesses | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 300
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

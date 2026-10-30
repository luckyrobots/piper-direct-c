$ErrorActionPreference='Stop'
foreach($expected in @(0,7)) {
    $child=Start-Process powershell.exe -ArgumentList @('-NoProfile','-Command',"exit $expected") -WindowStyle Hidden -PassThru
    $null=$child.Handle
    $child.WaitForExit()
    $actual=$child.ExitCode
    $child.Dispose()
    if($null -eq $actual -or $actual -ne $expected){throw "Process exit check failed: $expected / $actual"}
    Write-Output "PASS Windows PowerShell cached process exit code $actual"
}
$first=New-Object Threading.Mutex($false,'Local\LuckyRobotsPiperSupervisorTest')
if(-not $first.WaitOne(0)){throw 'Cannot acquire test supervisor mutex'}
$first.ReleaseMutex();$first.Dispose()

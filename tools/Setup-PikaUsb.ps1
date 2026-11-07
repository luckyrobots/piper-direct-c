#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory=$true)][string]$InstanceId,
    [string]$Distribution='Ubuntu'
)
$ErrorActionPreference='Stop'
if($InstanceId -notmatch '^USB\\VID_1A86&PID_7522\\[^\r\n]+$'){throw 'Choose one Pika CH340K (1a86:7522) USB instance; no other device will be shared.'}
if(!$Distribution -or $Distribution.Length -gt 128 -or $Distribution -match '[\x00-\x1f]'){throw 'Invalid WSL distribution name.'}
$taskUsb=Join-Path $env:ProgramFiles 'usbipd-win/usbipd.exe'
if(!(Test-Path -LiteralPath $taskUsb)){throw 'Install usbipd-win first. Native Windows WCH COM access is disabled.'}
function Invoke-Usb([string[]]$Arguments,[switch]$Json) {
    # Windows PowerShell 5 presents native stderr (including usbipd's INFO
    # lines) as ErrorRecords. Only the native exit code indicates failure.
    $taskPreviousPreference=$ErrorActionPreference
    try {
        $ErrorActionPreference='Continue'
        $taskOutput=@(& $taskUsb @Arguments 2>&1)
        $taskExitCode=$LASTEXITCODE
    } finally { $ErrorActionPreference=$taskPreviousPreference }
    if($taskExitCode){throw ('usbipd '+($Arguments -join ' ')+' failed: '+(($taskOutput | ForEach-Object {$_.ToString()}) -join [Environment]::NewLine))}
    if($Json){return (($taskOutput | ForEach-Object {$_.ToString()}) -join [Environment]::NewLine) | ConvertFrom-Json}
    foreach($taskLine in $taskOutput){Write-Host $taskLine.ToString()}
}
function Read-Pika {
    $taskFound=@((Invoke-Usb -Arguments @('state') -Json).Devices | Where-Object {$_.InstanceId -ieq $InstanceId})
    if($taskFound.Count -ne 1 -or !$taskFound[0].BusId){throw 'The selected Pika USB instance is not uniquely connected; refresh devices and select it again.'}
    return $taskFound[0]
}
$taskDevice=Read-Pika
if(!$taskDevice.IsForced){
    Invoke-Usb -Arguments @('bind','--force','--busid',$taskDevice.BusId)
    $taskDevice=Read-Pika
}
if(!$taskDevice.ClientIPAddress){
    # USB attachment is shared by the WSL2 kernel, not scoped to a distro.
    # Start the chosen distro explicitly, then use the version-stable --wsl
    # switch without its optional distro value.
    & wsl.exe --distribution $Distribution --exec true
    if($LASTEXITCODE){throw 'The selected WSL distribution did not start.'}
    Invoke-Usb -Arguments @('attach','--wsl','--busid',$taskDevice.BusId)
    $taskDevice=Read-Pika
}
[pscustomobject]@{
    ok=$true
    identity=$taskDevice.InstanceId
    device=('/dev/pika/by-host/'+$taskDevice.BusId)
    distribution=$Distribution
    shared=[bool]$taskDevice.IsForced
    attached=[bool]$taskDevice.ClientIPAddress
    message='Selected USB gripper shared and attached to WSL. No serial port, motor enable or position command was used. Refresh gripper devices in Settings.'
} | ConvertTo-Json -Compress

$ErrorActionPreference='Stop'
trap { $_ | Out-String | Set-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'data-windows-managed\firewall-refresh-install-error.txt'); exit 1 }
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator access required.'}
$dir=Join-Path $env:ProgramFiles 'LuckyRobotsPiperStartup'
if(Test-Path $dir){
    if(@(Get-Item $dir -Force; Get-ChildItem $dir -Recurse -Force)|Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }){throw 'Refusing reparse points.'}
}else{New-Item -ItemType Directory $dir|Out-Null}
$acl=New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true,$false)
foreach($entry in @(@('S-1-5-18','FullControl'),@('S-1-5-32-544','FullControl'),@('S-1-5-32-545','ReadAndExecute'))){
    $sid=New-Object Security.Principal.SecurityIdentifier($entry[0])
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,$entry[1],'ContainerInherit,ObjectInherit','None','Allow')))
}
Set-Acl $dir $acl
$target=Join-Path $dir 'Refresh-WslUsbFirewall.ps1'
Copy-Item (Join-Path $PSScriptRoot 'Refresh-WslUsbFirewall.ps1') $target -Force
$exe=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$action=New-ScheduledTaskAction -Execute $exe -Argument ('-NoProfile -ExecutionPolicy Bypass -File "'+$target+'"')
$triggers=@((New-ScheduledTaskTrigger -AtStartup),(New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)))
$settings=New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName LuckyRobotsPiperWslFirewall -Action $action -Trigger $triggers -Settings $settings -User SYSTEM -RunLevel Highest -Force|Out-Null
& $target
Get-ScheduledTask -TaskName LuckyRobotsPiperWslFirewall | Select-Object TaskName,State
Write-Output 'Installed persistent WSL USB firewall refresh.'

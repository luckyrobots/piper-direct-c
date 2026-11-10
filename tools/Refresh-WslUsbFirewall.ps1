$ErrorActionPreference='Stop'
# Windows recreates the WSL NIC with a new GUID after reboot. Resolve its name
# each time; retain the WSL-only interface restriction on USB sharing.
$wsl=@(Get-NetAdapter -IncludeHidden | Where-Object Name -like 'vEthernet (WSL*')
if($wsl.Count -ne 1){return}
$rule=@(Get-NetFirewallRule -DisplayName usbipd)
if($rule.Count -ne 1){throw 'Expected exactly one usbipd rule.'}
$port=$rule[0]|Get-NetFirewallPortFilter
if($port.LocalPort -ne '3240'){throw 'Unexpected USB sharing port.'}
$current=@(($rule[0]|Get-NetFirewallInterfaceFilter).InterfaceAlias)
$guid=$wsl[0].InterfaceGuid.ToString().Trim('{}')
if($current.Count -ne 1 -or ($current[0] -ne $wsl[0].Name -and $current[0].Trim('{}') -ne $guid)){
    $rule[0]|Set-NetFirewallRule -RemoteAddress LocalSubnet -InterfaceAlias $wsl[0].Name
}

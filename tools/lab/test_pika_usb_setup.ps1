param([string]$Repository=(Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
$ErrorActionPreference='Stop'
# Exercise the actual native-process wrapper under Windows PowerShell 5 without
# running setup, requiring elevation, sharing USB, or opening any serial port.
$taskTokens=$null; $taskParseErrors=$null
$taskAst=[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $Repository 'tools/Setup-PikaUsb.ps1'),[ref]$taskTokens,[ref]$taskParseErrors)
if($taskParseErrors){throw 'Setup-PikaUsb.ps1 has parser errors.'}
$taskFunction=$taskAst.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Usb'},$true)
if(!$taskFunction){throw 'Native wrapper is missing.'}
Invoke-Expression $taskFunction.Extent.Text
$taskUsb=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
function Fixture-Arguments([string]$Command) { return @('-NoProfile','-EncodedCommand',[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Command))) }
Invoke-Usb -Arguments @(Fixture-Arguments '[Console]::Error.WriteLine("usbipd: info: harmless setup information"); exit 0')
$taskRejected=$false
try { Invoke-Usb -Arguments @(Fixture-Arguments '[Console]::Error.WriteLine("real fixture failure"); exit 7') }
catch { $taskRejected=$_.Exception.Message -match 'real fixture failure' }
if(!$taskRejected){throw 'A real native failure was swallowed.'}
$taskState=Invoke-Usb -Arguments @(Fixture-Arguments '[Console]::WriteLine(''{"Devices":[]}''); exit 0') -Json
if($null -eq $taskState.Devices -or $taskState.Devices.Count -ne 0){throw 'Native JSON output was altered.'}
Write-Output 'Pika USB setup wrapper passed: PS5 native INFO stderr is nonfatal; exit 7 rejects; JSON preserved; no USB operations.'
exit 0

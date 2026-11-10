# Dot-source only. The one source of the standalone Windows dashboard's
# launch defaults, shared by Start-WindowsDashboard.ps1 and
# Restart-WindowsDashboard.ps1 so the two can never disagree.
#
# Precedence: an explicit script argument, then the saved Pika assignment in
# <state-dir>/pika-default.json ({ "device": ..., "distribution": ... }), then
# the built-in defaults below. The port matches Piper.Host's
# HostRoutes.DefaultHttpPort.
function Get-PiperDashboardDefaults([Parameter(Mandatory=$true)][string]$StateDir) {
    $taskSaved=$null
    $taskSavedPath=Join-Path $StateDir 'pika-default.json'
    if(Test-Path -LiteralPath $taskSavedPath) { $taskSaved=Get-Content -LiteralPath $taskSavedPath -Raw | ConvertFrom-Json }
    [pscustomobject]@{
        Port=8765
        Distribution=$(if($taskSaved -and $taskSaved.distribution){[string]$taskSaved.distribution}else{'Ubuntu'})
        # Null when nothing is saved; the supervisor may then discover one unambiguous Pika.
        PikaDevice=$(if($taskSaved){[string]$taskSaved.device}else{$null})
    }
}

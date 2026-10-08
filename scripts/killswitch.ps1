<#
.SYNOPSIS
    Turns the MihomoService kill-switch on or off, or shows its state.

.DESCRIPTION
    on/off write service.kill-switch to config.yaml and apply it immediately; the running service picks
    the change up without restarting the core. remove only deletes the WFP filters (config is untouched).
    Must be run as Administrator.
#>
#Requires -RunAsAdministrator
param(
    [ValidateSet('on', 'off', 'status', 'remove')][string]$Action = 'status',
    [string]$Dir
)

$ErrorActionPreference = 'Stop'
if (-not $Dir) {
    $Dir = if (Test-Path (Join-Path $PSScriptRoot 'MihomoService.exe')) { $PSScriptRoot }
           else { Join-Path $PSScriptRoot '..\dist\MihomoService' }
}
$exe = Join-Path (Resolve-Path $Dir) 'MihomoService.exe'
if (-not (Test-Path $exe)) { throw "$exe not found" }

& $exe killswitch $Action
exit $LASTEXITCODE

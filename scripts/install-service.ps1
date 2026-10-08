<#
.SYNOPSIS
    Registers MihomoService as a Windows service (until the MSI installer exists). Must be run as Administrator.

.PARAMETER Dir
    Folder with MihomoService.exe. Default: the script's own folder when it sits next to MihomoService.exe
    (a deployed copy of dist\MihomoService), otherwise ..\dist\MihomoService (repository checkout).
#>
#Requires -RunAsAdministrator
param(
    [string]$Dir,
    [string]$Name = 'MihomoService'
)

$ErrorActionPreference = 'Stop'
if (-not $Dir) {
    $Dir = if (Test-Path (Join-Path $PSScriptRoot 'MihomoService.exe')) { $PSScriptRoot }
           else { Join-Path $PSScriptRoot '..\dist\MihomoService' }
}
$exe = Join-Path (Resolve-Path $Dir) 'MihomoService.exe'
if (-not (Test-Path $exe)) { throw "$exe not found: run scripts\build.ps1 first" }

& $exe check
if ($LASTEXITCODE -ne 0) { throw "config check failed, service not installed" }

if (Get-Service $Name -ErrorAction SilentlyContinue) { throw "service $Name already exists: run uninstall-service.ps1 first" }

# Start automatically at boot, after the TCP/IP stack (Tcpip driver, Network Store Interface).
# Both exist on every Windows edition; actual network readiness (a default route through a real
# adapter) is awaited by the service itself, see NetworkWaiter.
New-Service -Name $Name -BinaryPathName "`"$exe`"" -DisplayName 'Mihomo Service' `
    -Description 'Mihomo proxy core in TUN mode (fallback group from config.yaml)' -StartupType Automatic `
    -DependsOn Tcpip, Nsi | Out-Null
# Restart on failure: 5s, 10s, 30s; reset the failure counter after a day.
sc.exe failure $Name reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
# A fresh install always starts protected; turn it off later with killswitch.cmd off if needed.
& $exe killswitch on
if ($LASTEXITCODE -ne 0) { throw "failed to enable the kill-switch" }
Start-Service $Name
Get-Service $Name

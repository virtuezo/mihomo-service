<#
.SYNOPSIS
    Registers MihomoService as a Windows service (until the MSI installer exists). Must be run as Administrator.

.PARAMETER Dir
    Folder with MihomoService.exe. Default: the script's own folder when it sits next to MihomoService.exe
    (a deployed copy of dist\MihomoService), otherwise ..\dist\MihomoService (repository checkout).

.PARAMETER KeepPermissions
    Leave the folder's permissions as they are. By default only SYSTEM and Administrators may change it
    (see below); use this only for a development copy that a non-elevated build must keep writing to.
#>
#Requires -RunAsAdministrator
param(
    [string]$Dir,
    [string]$Name = 'MihomoService',
    [switch]$KeepPermissions
)

$ErrorActionPreference = 'Stop'
if (-not $Dir) {
    $Dir = if (Test-Path (Join-Path $PSScriptRoot 'MihomoService.exe')) { $PSScriptRoot }
           else { Join-Path $PSScriptRoot '..\dist\MihomoService' }
}
$Dir = (Resolve-Path $Dir).Path
$exe = Join-Path $Dir 'MihomoService.exe'
if (-not (Test-Path $exe)) { throw "$exe not found: run scripts\build.ps1 first" }
if (Get-Service $Name -ErrorAction SilentlyContinue) { throw "service $Name already exists: run uninstall-service.ps1 first" }

function Invoke-Icacls {
    & icacls.exe @args | Out-Null
    if ($LASTEXITCODE) { throw "icacls $($args -join ' ') failed with code $LASTEXITCODE" }
}

# The service runs as LocalSystem from this folder. Anyone who can write here could replace mihomo.exe or
# MihomoService.exe (their code would run as SYSTEM) or edit config.yaml (turn the kill-switch off), and a
# folder created at the root of C: is writable by every signed-in user by default. So: owner Administrators
# (an owner can always grant itself access back), full control for SYSTEM and Administrators, read-only for
# Users; config.yaml and data\ (runtime.yaml) hold proxy credentials and are closed to Users entirely.
# Done before running anything from the folder.
$system = '*S-1-5-18'; $admins = '*S-1-5-32-544'; $users = '*S-1-5-32-545'
if ($KeepPermissions) {
    Write-Warning "permissions of $Dir are left as is: make sure non-administrators cannot write there"
} else {
    Invoke-Icacls $Dir /setowner $admins /T /C /Q
    Invoke-Icacls $Dir /reset /T /C /Q
    Invoke-Icacls $Dir /inheritance:r /grant:r "${system}:(OI)(CI)F" "${admins}:(OI)(CI)F" "${users}:(OI)(CI)RX" /Q
    $config = Join-Path $Dir 'config.yaml'
    if (Test-Path $config) { Invoke-Icacls $config /inheritance:r /grant:r "${system}:F" "${admins}:F" /Q }
    $data = Join-Path $Dir 'data'
    New-Item -ItemType Directory -Force $data | Out-Null
    Invoke-Icacls $data /inheritance:r /grant:r "${system}:(OI)(CI)F" "${admins}:(OI)(CI)F" /Q
    Write-Host "permissions: $Dir can be changed only by SYSTEM and Administrators; config.yaml and data\ are readable only by them"
}

& $exe check
if ($LASTEXITCODE -ne 0) { throw "config check failed, service not installed" }

# Start automatically at boot, after the TCP/IP stack (Tcpip driver, Network Store Interface) and the
# Base Filtering Engine that holds the kill-switch filters. All exist on every Windows edition; actual
# network readiness (a default route through a real adapter) is awaited by the service itself, see NetworkWaiter.
New-Service -Name $Name -BinaryPathName "`"$exe`"" -DisplayName 'Mihomo Service' `
    -Description 'Mihomo proxy core in TUN mode (fallback group from config.yaml)' -StartupType Automatic `
    -DependsOn Tcpip, Nsi, BFE | Out-Null
# Restart on failure: 5s, 10s, 30s; reset the failure counter after a day. On a fatal error the service
# terminates its process (see Supervisor), which SCM counts as a failure.
sc.exe failure $Name reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
if ($LASTEXITCODE) { Write-Warning "sc failure returned $($LASTEXITCODE): the service will not be restarted after a crash" }
# A fresh install always starts protected; turn it off later with killswitch.cmd off if needed.
& $exe killswitch on
if ($LASTEXITCODE -ne 0) { throw "failed to enable the kill-switch" }
try {
    Start-Service $Name
} catch {
    Write-Warning "service $Name failed to start: $($_.Exception.Message)"
    Write-Warning "the kill-switch is ON, so this machine has no internet until the service runs; see logs\service.log"
    Write-Warning "to restore the network: `"$exe`" killswitch remove (or uninstall-service.cmd)"
    exit 1
}
Get-Service $Name

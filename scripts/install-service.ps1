<#
.SYNOPSIS
    Registers MihomoService as a Windows service from a built folder (dev helper; the installer will replace it).
    Must be run as Administrator.
#>
#Requires -RunAsAdministrator
param(
    [string]$Dir = (Join-Path $PSScriptRoot '..\dist\MihomoService'),
    [string]$Name = 'MihomoService'
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path (Resolve-Path $Dir) 'MihomoService.exe'
if (-not (Test-Path $exe)) { throw "$exe not found: run scripts\build.ps1 first" }

& $exe check
if ($LASTEXITCODE -ne 0) { throw "config check failed, service not installed" }

if (Get-Service $Name -ErrorAction SilentlyContinue) { throw "service $Name already exists: run uninstall-service.ps1 first" }

New-Service -Name $Name -BinaryPathName "`"$exe`"" -DisplayName 'Mihomo Service' `
    -Description 'Mihomo proxy core in TUN mode (fallback group from config.yaml)' -StartupType Automatic | Out-Null
# Restart on failure: 5s, 10s, 30s; reset the failure counter after a day.
sc.exe failure $Name reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Start-Service $Name
Get-Service $Name

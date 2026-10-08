#Requires -RunAsAdministrator
param([string]$Name = 'MihomoService')

$ErrorActionPreference = 'Stop'
$service = Get-Service $Name -ErrorAction SilentlyContinue
if (-not $service) { Write-Host "service $Name is not installed"; return }

if ($service.Status -ne 'Stopped') { Stop-Service $Name -Force }
sc.exe delete $Name | Out-Null
Write-Host "service $Name removed"

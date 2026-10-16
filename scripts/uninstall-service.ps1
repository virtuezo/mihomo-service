#Requires -RunAsAdministrator
param([string]$Name = 'MihomoService')

$ErrorActionPreference = 'Stop'
$service = Get-CimInstance Win32_Service -Filter "Name='$Name'"

if ($service) {
    $exe = $service.PathName.Trim('"')
    if ($service.State -ne 'Stopped') { Stop-Service $Name -Force }
    # Disabled first: even if the deletion below fails, the service won't start at boot and re-enable the filters.
    Set-Service $Name -StartupType Disabled
    $output = sc.exe delete $Name
    switch ($LASTEXITCODE) {
        0 { Write-Host "service $Name removed" }
        1072 { Write-Warning "service $Name is marked for deletion: close Services (services.msc) or reboot to finish" }
        default { Write-Warning "sc delete failed ($LASTEXITCODE): $output; the service is disabled and will not start" }
    }
} else {
    Write-Host "service $Name is not installed"
    # Still clear the kill-switch: the service may have been removed by hand.
    $exe = @((Join-Path $PSScriptRoot 'MihomoService.exe'), (Join-Path $PSScriptRoot '..\dist\MihomoService\MihomoService.exe')) |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}

# The kill-switch filters are persistent: without the service they would keep the machine offline.
# 'remove' keeps config.yaml as is, so a later reinstall is protected again.
if ($exe -and (Test-Path $exe)) {
    & $exe killswitch remove
    if ($LASTEXITCODE -ne 0) { Write-Warning "kill-switch was not removed: run `"$exe`" killswitch remove" }
} else {
    Write-Warning "MihomoService.exe not found: if the internet stays blocked, run 'MihomoService.exe killswitch remove' from any copy"
}

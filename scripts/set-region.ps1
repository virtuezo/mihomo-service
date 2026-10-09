<#
.SYNOPSIS
    Sets the country/region and time zone for the machine and every user profile; display language is kept.

.DESCRIPTION
    Installed apps that are unavailable in some markets check the Windows region (GeoId) and time zone,
    not only the external IP. The region is a per-user setting, so it is written to every existing profile
    (signed in or not), the default profile (users created later) and .DEFAULT (sign-in screen).
    The time zone is machine-wide: it is set, automatic time zone detection is turned off and RDP time zone
    redirection is disabled by policy, so remote sessions do not take the client's zone.
    Display language, regional formats and the system locale are not touched.
    Signed-in users see the new region after signing out and in again.
    Must be run as Administrator. -WhatIf shows the changes without making them.

.EXAMPLE
    .\set-region.ps1
    Netherlands, W. Europe Standard Time (Amsterdam).

.EXAMPLE
    .\set-region.ps1 -Country RU -TimeZone 'Russian Standard Time' -AllowRdpTimeZoneRedirection
    Reverts to Russia, Moscow time, RDP policy not configured.
#>
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Country = 'NL',
    [string]$TimeZone = 'W. Europe Standard Time',
    [switch]$AllowRdpTimeZoneRedirection
)

$ErrorActionPreference = 'Stop'
$failed = 0
$region = New-Object System.Globalization.RegionInfo $Country
$iso2 = $region.TwoLetterISORegionName
$geoId = $region.GeoId
$null = [System.TimeZoneInfo]::FindSystemTimeZoneById($TimeZone)   # unknown id fails before anything changes

function Step([string]$What, [scriptblock]$Do) {
    if ($WhatIfPreference) { Write-Host "What if: $What"; return }
    & $Do
    Write-Host "OK: $What"
}

# Same values Set-WinHomeLocation writes for the current user.
function Set-Geo([string]$UserKey) {
    $key = "Registry::$UserKey\Control Panel\International\Geo"
    if (-not (Test-Path $key)) { New-Item $key -Force | Out-Null }
    Set-ItemProperty $key -Name Nation -Value "$geoId" -Type String
    Set-ItemProperty $key -Name Name -Value $iso2 -Type String
}

# A profile that cannot be loaded is reported and skipped; the rest are still processed.
function Set-GeoInHive([string]$Hive, [string]$Label) {
    $what = "region $iso2 ($geoId) for $Label"
    if (-not (Test-Path $Hive)) { Write-Warning "${Label}: $Hive not found, skipped"; return }
    if ($WhatIfPreference) { Write-Host "What if: $what"; return }
    $mount = 'MihomoRegion_' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    & reg.exe load "HKU\$mount" $Hive | Out-Null
    if ($LASTEXITCODE) { Write-Warning "${Label}: reg load $Hive failed, skipped"; $script:failed++; return }
    try { Set-Geo "HKEY_USERS\$mount" }
    finally {
        # Registry handles held by the PowerShell provider must be released before unloading.
        [gc]::Collect(); [gc]::WaitForPendingFinalizers()
        & reg.exe unload "HKU\$mount" | Out-Null
    }
    Write-Host "OK: $what"
}

Write-Host "Region: $($region.EnglishName) ($iso2, GeoId $geoId); time zone: $TimeZone"

# Time zone (machine-wide).
Step "time zone $TimeZone" { Set-TimeZone -Id $TimeZone }
Step 'automatic time zone detection off' {
    Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\tzautoupdate' -Name Start -Value 4 -Type DWord
}
$tsPolicy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
if ($AllowRdpTimeZoneRedirection) {
    Step 'RDP time zone redirection policy: not configured' {
        Remove-ItemProperty $tsPolicy -Name fEnableTimeZoneRedirection -ErrorAction SilentlyContinue
    }
} else {
    Step 'RDP time zone redirection policy: disabled' {
        if (-not (Test-Path $tsPolicy)) { New-Item $tsPolicy -Force | Out-Null }
        Set-ItemProperty $tsPolicy -Name fEnableTimeZoneRedirection -Value 0 -Type DWord
    }
}

# Region for every local and domain user profile.
$profileList = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList'
foreach ($p in Get-ChildItem $profileList | Where-Object PSChildName -match '^S-1-5-21-[\d-]+$') {
    $sid = $p.PSChildName
    $path = $p.GetValue('ProfileImagePath')
    $name = try { (New-Object System.Security.Principal.SecurityIdentifier $sid).Translate([System.Security.Principal.NTAccount]).Value }
            catch { $sid }
    if (Test-Path "Registry::HKEY_USERS\$sid") {
        Step "region $iso2 ($geoId) for $name (signed in)" { Set-Geo "HKEY_USERS\$sid" }
    } else {
        Set-GeoInHive (Join-Path $path 'NTUSER.DAT') $name
    }
}

# Users created later get their profile from Default; the sign-in screen uses .DEFAULT.
$defaultProfile = [Environment]::ExpandEnvironmentVariables((Get-ItemProperty $profileList).Default)
Set-GeoInHive (Join-Path $defaultProfile 'NTUSER.DAT') 'new users (Default profile)'
Step "region $iso2 ($geoId) for sign-in screen (.DEFAULT)" { Set-Geo 'HKEY_USERS\.DEFAULT' }

if ($failed) { Write-Warning "$failed profile(s) skipped, see warnings above"; exit 1 }
if (-not $WhatIfPreference) { Write-Host 'Done. Signed-in users must sign out and in again; restart running apps.' }
exit 0

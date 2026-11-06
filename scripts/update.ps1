<#
.SYNOPSIS
    Updates MihomoService and the mihomo core in place from their GitHub releases. Must be run as Administrator.

.DESCRIPTION
    Compares the installed versions with the latest stable releases: the service (MihomoService.exe and the
    scripts) from github.com/virtuezo/mihomo-service, the core (mihomo.exe) from github.com/MetaCubeX/mihomo.
    Shows what would change and asks before installing; -WhatIf only shows it.
    Downloads go through the tunnel of the running service (the kill-switch lets nothing else out), so the
    service must be running. Every archive is checked against the SHA-256 GitHub publishes for it.
    Installing: the files being replaced are copied to backup\, the service is stopped (the kill-switch keeps
    the network closed meanwhile), the files are replaced, `MihomoService.exe check` validates config.yaml with
    the new versions, and the service is started. If the check fails or the core does not come up, the
    previous files are put back and the service is started on them. config.yaml, data\ and logs\ are not touched.

.PARAMETER CoreBuild
    mihomo build: amd64-v3 (needs AVX2, default), amd64-v2, amd64-v1, amd64-compatible.

.PARAMETER Force
    Reinstall even if the installed versions are the latest. Only files that differ from the downloaded ones
    are replaced; if none differ, the service is not restarted.

.PARAMETER Yes
    Install without asking.
#>
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Dir,
    [string]$Name = 'MihomoService',
    [ValidateSet('amd64-v3', 'amd64-v2', 'amd64-v1', 'amd64-compatible')][string]$CoreBuild = 'amd64-v3',
    [switch]$SkipService,
    [switch]$SkipCore,
    [switch]$Force,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
# -WhatIf means "only show the plan"; cmdlets below must not inherit it (module auto-loading prints noise).
$planOnly = [bool]$WhatIfPreference
$WhatIfPreference = $false
$ServiceRepo = 'virtuezo/mihomo-service'
$CoreRepo = 'MetaCubeX/mihomo'

if (-not $Dir) {
    $Dir = if (Test-Path (Join-Path $PSScriptRoot 'MihomoService.exe')) { $PSScriptRoot }
           else { Join-Path $PSScriptRoot '..\dist\MihomoService' }
}
$Dir = (Resolve-Path $Dir).Path
$exe = Join-Path $Dir 'MihomoService.exe'
$core = Join-Path $Dir 'mihomo.exe'
if (-not (Test-Path $exe)) { throw "$exe not found" }

$service = Get-CimInstance Win32_Service -Filter "Name='$Name'"
if ($service -and $service.PathName.Trim('"') -ne $exe) {
    throw "service $Name runs $($service.PathName), not $exe - run update from that folder"
}

# Windows PowerShell 5.1 does not offer TLS 1.2 by default, and its progress bar slows downloads many times.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'
$headers = @{ 'User-Agent' = 'MihomoService-update'; 'Accept' = 'application/vnd.github+json' }

function Get-Version([string]$Text) {
    if ($Text -match '(\d+\.\d+\.\d+)') { [version]$Matches[1] } else { [version]'0.0.0' }
}

function Get-CoreText {
    if (Test-Path $core) { (& $core -v | Select-Object -First 1) } else { 'none' }
}

# ProductVersion carries the full commit hash after "+"; 7 characters, as in the service log (BuildInfo).
function Get-ServiceText {
    $version = (Get-Item $exe).VersionInfo.ProductVersion
    if ($version -match '^([^+]*\+[0-9a-f]{7})[0-9a-f]+$') { $Matches[1] } else { $version }
}

function Get-LatestRelease([string]$Repo) {
    try { Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers }
    catch {
        $response = $_.Exception.Response
        if ($response -and [int]$response.StatusCode -eq 404) { throw "github.com/$Repo has no published release (or the repository is private)" }
        if ($response) { throw "github.com/${Repo}: $($_.Exception.Message)" }
        throw "github.com/$Repo is unreachable ($($_.Exception.Message)): the service must be running - its kill-switch lets only the tunnel out"
    }
}

function Get-Asset($Release, [string]$AssetName) {
    $asset = @($Release.assets | Where-Object { $_.name -eq $AssetName })
    if ($asset.Count -ne 1) { throw "release $($Release.tag_name) has no $AssetName" }
    if ($asset[0].digest -match '^sha256:([0-9a-f]{64})$') { $sha256 = $Matches[1] }
    else { throw "GitHub gives no SHA-256 for $AssetName - not installing it" }
    [pscustomobject]@{ Name = $AssetName; Url = $asset[0].browser_download_url; Sha256 = $sha256; Size = $asset[0].size }
}

# Downloads and unpacks an asset into its own folder under $To; the archive must match GitHub's SHA-256.
function Save-Asset($Asset, [string]$To) {
    $file = Join-Path $To $Asset.Name
    Write-Host "downloading $($Asset.Name) ($([math]::Round($Asset.Size / 1MB, 1)) MB)"
    Invoke-WebRequest $Asset.Url -OutFile $file -Headers $headers -UseBasicParsing
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $Asset.Sha256) { throw "$($Asset.Name): SHA-256 $hash does not match GitHub's $($Asset.Sha256)" }
    $out = Join-Path $To ([IO.Path]::GetFileNameWithoutExtension($Asset.Name))
    Expand-Archive $file $out
    $out
}

function Stop-ServiceWithin([int]$Seconds) {
    $controller = Get-Service $Name
    if ($controller.Status -eq 'Stopped') { return }
    if ($controller.Status -ne 'StopPending') { $controller.Stop() }
    $controller.WaitForStatus('Stopped', [TimeSpan]::FromSeconds($Seconds))
}

# The service waits for the network and validates config.yaml before starting the core.
function Wait-Core([int]$Seconds) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        if ((Get-Service $Name).Status -eq 'Stopped') { return $false }
        if (Get-CimInstance Win32_Process -Filter "Name='mihomo.exe'" | Where-Object { $_.ExecutablePath -eq $core }) { return $true }
        Start-Sleep -Seconds 1
    }
    $false
}

# A file can stay locked for a moment after the service stops (the core dies with its job object, antivirus).
function Copy-WithRetry([string]$From, [string]$To) {
    for ($i = 1; ; $i++) {
        try { Copy-Item $From $To -Force; return }
        catch { if ($i -ge 10) { throw }; Start-Sleep -Seconds 1 }
    }
}

# --- What is installed and what is new ---
$installedService = Get-ServiceText
$installedCore = Get-CoreText
Write-Host "installed: MihomoService $installedService; $installedCore"

$serviceAsset = $null; $coreAsset = $null
if (-not $SkipService) {
    $release = Get-LatestRelease $ServiceRepo
    $latest = Get-Version $release.tag_name
    if ($Force -or $latest -gt (Get-Version $installedService)) {
        $serviceAsset = Get-Asset $release "MihomoService-$($release.tag_name).zip"
        Write-Host "update:    MihomoService $(Get-Version $installedService) -> $latest"
    } else {
        Write-Host "latest:    MihomoService $latest - up to date"
    }
}
if (-not $SkipCore) {
    $release = Get-LatestRelease $CoreRepo
    $latest = Get-Version $release.tag_name
    if ($Force -or $latest -gt (Get-Version $installedCore)) {
        $coreAsset = Get-Asset $release "mihomo-windows-$CoreBuild-$($release.tag_name).zip"
        Write-Host "update:    mihomo $(Get-Version $installedCore) -> $latest ($CoreBuild)"
    } else {
        Write-Host "latest:    mihomo $latest - up to date"
    }
}
if (-not $serviceAsset -and -not $coreAsset) { Write-Host 'Nothing to update.'; exit 0 }
if ($planOnly) { exit 0 }
if (-not $Yes) {
    $answer = Read-Host 'Install? The network is closed for a few seconds while the service restarts [y/N]'
    if ($answer -notmatch '^\s*y(es)?\s*$') { Write-Host 'Cancelled.'; exit 1 }
}

# --- Download and check everything before touching the installation ---
$stage = Join-Path ([IO.Path]::GetTempPath()) ('mihomo-update-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $stage | Out-Null
try {
    $files = @()   # @{ From; To } - what replaces what
    if ($serviceAsset) {
        $unpacked = Save-Asset $serviceAsset $stage
        if (-not (Test-Path (Join-Path $unpacked 'MihomoService.exe'))) { throw "$($serviceAsset.Name) has no MihomoService.exe" }
        foreach ($file in Get-ChildItem $unpacked -File | Where-Object { $_.Name -eq 'MihomoService.exe' -or $_.Extension -in '.ps1', '.cmd' }) {
            $files += @{ From = $file.FullName; To = Join-Path $Dir $file.Name }
        }
    }
    if ($coreAsset) {
        $unpacked = Save-Asset $coreAsset $stage
        $newCore = @(Get-ChildItem $unpacked -Filter 'mihomo*.exe' -File)
        if ($newCore.Count -ne 1) { throw "$($coreAsset.Name): expected one mihomo*.exe inside" }
        Write-Host "new core:  $(& $newCore[0].FullName -v | Select-Object -First 1)"
        $files += @{ From = $newCore[0].FullName; To = $core }
    }

    # Unchanged files are left alone. update.cmd is not replaced under itself: cmd reads a running batch file
    # line by line, so this window's wrapper would continue from the same offset in the new file.
    $pendingCmd = $null
    $changed = @()
    foreach ($f in $files) {
        if ((Test-Path $f.To) -and (Get-FileHash $f.To).Hash -eq (Get-FileHash $f.From).Hash) { continue }
        if ((Split-Path $f.To -Leaf) -eq 'update.cmd' -and (Test-Path $f.To)) {
            $pendingCmd = "$($f.To).new"
            Copy-Item $f.From $pendingCmd -Force
            continue
        }
        $changed += $f
    }
    $files = $changed
    $pendingNote = if ($pendingCmd) {
        "update.cmd changed: after this window closes run  move /y `"$pendingCmd`" `"$(Join-Path $Dir 'update.cmd')`""
    }
    # E.g. -Force on the latest versions: nothing to replace, so no reason to stop the service and close the network.
    if ($files.Count -eq 0) {
        Write-Host 'Nothing to replace: the downloaded files are the installed ones, the service was not restarted.'
        if ($pendingNote) { Write-Warning $pendingNote }
        exit 0
    }

    # --- Install, with a way back ---
    $backup = Join-Path $Dir 'backup'
    if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
    New-Item -ItemType Directory $backup | Out-Null
    foreach ($f in $files) { if (Test-Path $f.To) { Copy-Item $f.To $backup -Force } }

    if ($service) { Write-Host "stopping $Name (the kill-switch keeps the network closed)"; Stop-ServiceWithin 60 }
    foreach ($f in $files) { Copy-WithRetry $f.From $f.To; Write-Host "replaced:  $(Split-Path $f.To -Leaf)" }

    $failure = $null
    & $exe check
    if ($LASTEXITCODE -ne 0) { $failure = 'config check with the new versions failed' }
    elseif ($service) {
        Start-Service $Name
        if (-not (Wait-Core 120)) { $failure = 'the service did not start the core (see logs\service.log)' }
    }

    if ($failure) {
        Write-Warning "$failure - restoring the previous files"
        if ($service) { Stop-ServiceWithin 60 }
        foreach ($f in $files) {
            $saved = Join-Path $backup (Split-Path $f.To -Leaf)
            if (Test-Path $saved) { Copy-WithRetry $saved $f.To } else { Remove-Item $f.To -Force }
        }
        if ($service) { Start-Service $Name }
        exit 1
    }

    Write-Host ''
    Write-Host "Done: MihomoService $(Get-ServiceText); $(Get-CoreText)"
    Write-Host "Previous files: $backup"
    if ($pendingNote) { Write-Warning $pendingNote }
} finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

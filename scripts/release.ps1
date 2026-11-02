<#
.SYNOPSIS
    Publishes a MihomoService release on GitHub: build (tests + NativeAOT), archive, gh release create.

.DESCRIPTION
    The version is <Version> from src\MihomoService\MihomoService.csproj, the tag v<Version>. The working tree
    must be clean and HEAD pushed, so that the commit in the built version is the released one. The archive
    MihomoService-v<Version>.zip holds what update.ps1 and a fresh install need: MihomoService.exe, mihomo.exe,
    the service scripts and the third-party licenses - never config.yaml, data\ or logs\. Asks before
    publishing (-Yes skips that).
    Needs gh, signed in with write access to the repository.
#>
param([switch]$Yes)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repo = 'virtuezo/mihomo-service'
$output = Join-Path $root 'dist\MihomoService'

[xml]$project = Get-Content (Join-Path $root 'src\MihomoService\MihomoService.csproj')
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if (-not $version) { throw 'no <Version> in MihomoService.csproj' }
$tag = "v$version"

if (git -C $root status --porcelain) { throw 'uncommitted changes: commit them first, the release must match a commit' }
git -C $root fetch --quiet origin
if ($LASTEXITCODE) { throw 'git fetch failed' }
$head = git -C $root rev-parse HEAD
if (-not (git -C $root branch -r --contains $head)) { throw "HEAD $head is not pushed: git push first" }
gh release view $tag --repo $repo *> $null
if ($LASTEXITCODE -eq 0) { throw "release $tag already exists: raise <Version> in MihomoService.csproj" }

& (Join-Path $PSScriptRoot 'build.ps1')

$built = (Get-Item (Join-Path $output 'MihomoService.exe')).VersionInfo.ProductVersion
if ($built -notlike "$version+$head*") { throw "built $built, expected $version+$head" }
$core = & (Join-Path $output 'mihomo.exe') -v | Select-Object -First 1

# Only the service files: dist also holds the local config.yaml (proxy credentials) and logs.
$stage = Join-Path ([IO.Path]::GetTempPath()) "MihomoService-$tag"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null
Copy-Item (Join-Path $output 'MihomoService.exe'), (Join-Path $output 'mihomo.exe'), (Join-Path $output '*.ps1'), (Join-Path $output '*.cmd'),
    (Join-Path $output 'THIRD-PARTY-NOTICES.md'), (Join-Path $output 'mihomo-GPL-3.0.txt') $stage
$zip = Join-Path $root "dist\MihomoService-$tag.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive (Join-Path $stage '*') $zip
$names = (Get-ChildItem $stage -File | ForEach-Object Name) -join ', '
Remove-Item $stage -Recurse -Force

Write-Host ''
Write-Host "Release:  $tag ($built)"
Write-Host "Core:     $core"
Write-Host "Archive:  $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB): $names"
if (-not $Yes) {
    $answer = Read-Host "Publish $tag to github.com/$repo? [y/N]"
    if ($answer -notmatch '^\s*y(es)?\s*$') { Write-Host 'Not published.'; exit 1 }
}

gh release create $tag $zip --repo $repo --target $head --title "MihomoService $version" --notes "Core: $core" --generate-notes
if ($LASTEXITCODE) { throw 'gh release create failed' }

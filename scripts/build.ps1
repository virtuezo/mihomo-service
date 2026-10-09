<#
.SYNOPSIS
    Builds MihomoService (NativeAOT) and assembles a ready-to-run folder in dist\MihomoService.

.PARAMETER Core
    Path to the mihomo core executable. Copied as mihomo.exe.
    Default: the newest bin\mihomo-windows-amd64*.exe.

.PARAMETER Config
    Optional config.yaml to put into the output folder (an existing one there is kept otherwise).
#>
param(
    [string]$Core,
    [string]$Config,
    [string]$Output = (Join-Path $PSScriptRoot '..\dist\MihomoService')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

if (-not $Core) {
    $Core = Get-ChildItem (Join-Path $root 'bin') -Filter 'mihomo-windows-amd64*.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Core) { throw "mihomo core not found: put mihomo-windows-amd64*.exe into bin\ or pass -Core" }
}

# A running service locks MihomoService.exe and mihomo.exe in the output folder.
$outputFull = [IO.Path]::GetFullPath($Output).TrimEnd('\')
$running = Get-CimInstance Win32_Service -Filter "State='Running'" |
    Where-Object { $_.PathName -and $_.PathName.Trim('"') -like "$outputFull\*" }
if ($running) {
    throw "service '$($running.Name)' is running from $outputFull - stop it first (Stop-Service $($running.Name), as Administrator)"
}

dotnet test (Join-Path $root 'MihomoService.slnx') --nologo
if ($LASTEXITCODE -ne 0) { throw "tests failed" }

# NativeAOT links via vcvarsall.bat, which expects vswhere.exe on PATH.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path $vsInstaller) -and ($env:PATH -notlike "*$vsInstaller*")) { $env:PATH = "$vsInstaller;$env:PATH" }

dotnet publish (Join-Path $root 'src\MihomoService\MihomoService.csproj') -c Release -o $Output --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Get-ChildItem $Output -Filter '*.pdb' | Remove-Item
Copy-Item $Core (Join-Path $Output 'mihomo.exe') -Force
Copy-Item (Join-Path $root 'config\config.example.yaml') $Output -Force
if ($Config) { Copy-Item $Config (Join-Path $Output 'config.yaml') -Force }
# Service scripts make the folder self-contained: copy it to the target machine and run install-service.cmd.
foreach ($script in 'install-service', 'uninstall-service', 'killswitch', 'diag', 'set-region') {
    Copy-Item (Join-Path $PSScriptRoot "$script.ps1"), (Join-Path $PSScriptRoot "$script.cmd") $Output -Force
}

Write-Host ""
Write-Host "Output: $(Resolve-Path $Output)"
Write-Host "Core:   $(& (Join-Path $Output 'mihomo.exe') -v | Select-Object -First 1)"
if (-not (Test-Path (Join-Path $Output 'config.yaml'))) {
    Write-Host "Next:   create config.yaml next to MihomoService.exe (see config.example.yaml)"
}
Write-Host "Deploy: copy the folder to the target machine and run install-service.cmd there"

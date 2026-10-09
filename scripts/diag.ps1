<#
.SYNOPSIS
    Collects a network diagnostics report for MihomoService (routes, DNS, proxies, kill-switch, logs).

.DESCRIPTION
    Writes logs\diag-<timestamp>\ next to MihomoService.exe: report.txt plus copies of the service/core logs
    and config.yaml with secrets masked. Answers "does traffic really go through the tunnel": which interface
    each destination is routed to, what external IP apps see with and without the system proxy, what DNS returns.
    Run as Administrator on the machine where the service is installed.
#>
#Requires -RunAsAdministrator
param([string]$Dir)

$ErrorActionPreference = 'Continue'
if (-not $Dir) {
    $Dir = if (Test-Path (Join-Path $PSScriptRoot 'MihomoService.exe')) { $PSScriptRoot }
           else { Join-Path $PSScriptRoot '..\dist\MihomoService' }
}
$Dir = (Resolve-Path $Dir).Path
$exe = Join-Path $Dir 'MihomoService.exe'
$out = Join-Path $Dir ("logs\diag-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $out | Out-Null
$report = Join-Path $out 'report.txt'

function Section([string]$Title, [scriptblock]$Body) {
    Write-Host "  $Title"
    # Native stderr arrives as ErrorRecords in PowerShell 5.1: keep just the text.
    $text = try {
        & $Body 2>&1 | ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.ToString() } else { $_ } } |
            Out-String -Width 250
    } catch { "ERROR: $_" }
    Add-Content -Path $report -Encoding UTF8 -Value ("`r`n===== $Title =====`r`n" + $text.TrimEnd())
}

# What an application sees: external IP and country, fetched through the system (IE/WinINet) proxy or directly.
function Get-ExternalIp([bool]$SystemProxy) {
    $req = [System.Net.WebRequest]::Create('https://ipinfo.io/json')
    $req.Timeout = 10000
    $req.Proxy = if ($SystemProxy) { [System.Net.WebRequest]::GetSystemWebProxy() } else { $null }
    $resp = $req.GetResponse()
    try { (New-Object System.IO.StreamReader($resp.GetResponseStream())).ReadToEnd() } finally { $resp.Close() }
}

$targets = [ordered]@{
    'internet 1.1.1.1' = '1.1.1.1'
    'internet 8.8.8.8' = '8.8.8.8'
    'internet v6 2606:4700:4700::1111' = '2606:4700:4700::1111'
}
# Proxy servers from config.yaml: they must be routed outside the tunnel (mihomo dials them itself).
$configPath = Join-Path $Dir 'config.yaml'
$proxyPorts = @{}
if (Test-Path $configPath) {
    Select-String -Path $configPath -Pattern '^\s*server:\s*["'']?([^"''\s#]+)' -Context 3, 3 | ForEach-Object {
        $server = $_.Matches[0].Groups[1].Value
        $targets["proxy $server"] = $server
        $port = ($_.Context.PreContext + $_.Context.PostContext) | Where-Object { $_ -match '^\s*port:\s*(\d+)' } |
            Select-Object -First 1 | ForEach-Object { [int]($_ -replace '^\s*port:\s*(\d+).*$', '$1') }
        $proxyPorts[$server] = if ($port) { $port } else { 443 }
    }
}

Write-Host "Collecting diagnostics into $out"

Section 'System' {
    Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'
    [Environment]::MachineName
    (Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber | Format-List | Out-String).Trim()
}

Section 'Service' {
    $svc = Get-Service MihomoService -ErrorAction SilentlyContinue
    if ($svc) { $svc | Format-List Name, Status, StartType } else { 'MihomoService is not installed' }
    Get-Process mihomo, MihomoService -ErrorAction SilentlyContinue | Format-Table Id, ProcessName, StartTime, Path -AutoSize
}
Section 'MihomoService.exe check' { & $exe check }
Section 'MihomoService.exe killswitch status' { & $exe killswitch status }

Section 'Adapters' {
    Get-NetAdapter -IncludeHidden | Sort-Object ifIndex |
        Format-Table ifIndex, Name, InterfaceDescription, Status, MacAddress, LinkSpeed -AutoSize
}
Section 'IP interfaces (metrics, forwarding)' {
    Get-NetIPInterface | Where-Object ConnectionState -eq Connected | Sort-Object InterfaceMetric |
        Format-Table ifIndex, InterfaceAlias, AddressFamily, InterfaceMetric, AutomaticMetric, Dhcp, Forwarding, NlMtu -AutoSize
}
Section 'IP addresses' {
    Get-NetIPAddress | Sort-Object InterfaceIndex |
        Format-Table InterfaceIndex, InterfaceAlias, AddressFamily, IPAddress, PrefixLength, PrefixOrigin, AddressState -AutoSize
}
Section 'ipconfig /all' { ipconfig /all }

Section 'Routes IPv4 (active + persistent)' {
    Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore | Sort-Object { [int]$_.DestinationPrefix.Split('/')[1] } -Descending |
        Format-Table ifIndex, InterfaceAlias, DestinationPrefix, NextHop, RouteMetric, InterfaceMetric, Protocol -AutoSize
    'Persistent:'
    Get-NetRoute -AddressFamily IPv4 -PolicyStore PersistentStore -ErrorAction SilentlyContinue |
        Format-Table ifIndex, DestinationPrefix, NextHop, RouteMetric -AutoSize
}
Section 'Routes IPv6 (non link-local)' {
    Get-NetRoute -AddressFamily IPv6 -PolicyStore ActiveStore |
        Where-Object { $_.DestinationPrefix -notlike 'fe80*' -and $_.DestinationPrefix -notlike 'ff00*' } |
        Format-Table ifIndex, InterfaceAlias, DestinationPrefix, NextHop, RouteMetric -AutoSize
}
Section 'route print' { route print }

Section 'Route chosen per destination' {
    foreach ($t in $targets.GetEnumerator()) {
        $ip = $t.Value
        if (-not [System.Net.IPAddress]::TryParse($ip, [ref]$null)) {
            $ip = (Resolve-DnsName $t.Value -Type A -ErrorAction SilentlyContinue | Where-Object IPAddress | Select-Object -First 1).IPAddress
            if (-not $ip) { "$($t.Key): cannot resolve"; continue }
        }
        $r = Find-NetRoute -RemoteIPAddress $ip -ErrorAction SilentlyContinue
        if (-not $r) { "$($t.Key) [$ip]: no route"; continue }
        $route = $r | Where-Object DestinationPrefix | Select-Object -First 1
        $src = $r | Where-Object IPAddress | Select-Object -First 1
        "{0} [{1}]: if {2} {3} via {4} (prefix {5}), source {6}" -f $t.Key, $ip, $route.ifIndex, $route.InterfaceAlias,
            $route.NextHop, $route.DestinationPrefix, $src.IPAddress
    }
}

Section 'DNS servers' {
    Get-DnsClientServerAddress | Where-Object ServerAddresses |
        Format-Table InterfaceIndex, InterfaceAlias, AddressFamily, ServerAddresses -AutoSize
}
Section 'DNS: DoH, NRPT, global settings' {
    'DoH servers:'
    Get-DnsClientDohServerAddress -ErrorAction SilentlyContinue | Format-Table -AutoSize
    'Per-interface DoH settings:'
    Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.ValueCount -gt 0 } | ForEach-Object {
            $key = $_
            "$($key.Name): " + (($key.Property | ForEach-Object { "$_=$($key.GetValue($_))" }) -join ', ')
        }
    'NRPT rules:'
    Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Format-Table Namespace, NameServers, DirectAccessEnabled -AutoSize
    Get-DnsClientGlobalSetting | Format-List
}
Section 'DNS resolution (fake-ip 198.18.x.x expected)' {
    foreach ($name in 'www.google.com', 'ipinfo.io', 'www.msftconnecttest.com') {
        "-- $name (system resolver):"
        Resolve-DnsName $name -DnsOnly -ErrorAction SilentlyContinue | Where-Object IPAddress | Format-Table Name, Type, IPAddress -AutoSize
    }
    '-- nslookup www.google.com:'
    nslookup www.google.com 2>&1
}

Section 'Proxy settings (WinHTTP, WinINet, env)' {
    netsh winhttp show proxy
    'WinINet HKCU:'
    Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue |
        Format-List ProxyEnable, ProxyServer, ProxyOverride, AutoConfigURL, AutoDetect
    'WinINet policies HKLM:'
    Get-ItemProperty 'HKLM:\Software\Policies\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue | Format-List
    'Environment:'
    Get-ChildItem Env: | Where-Object Name -match 'proxy' | Format-Table Name, Value -AutoSize
}

Section 'External IP as apps see it' {
    '-- via system proxy settings (browsers, most apps):'
    try { Get-ExternalIp $true } catch { "ERROR: $($_.Exception.GetBaseException().Message)" }
    '-- direct, no proxy:'
    try { Get-ExternalIp $false } catch { "ERROR: $($_.Exception.GetBaseException().Message)" }
    '-- curl.exe, IPv6 only:'
    curl.exe -s -6 -m 10 https://api64.ipify.org 2>&1
}

Section 'Direct connection to proxy servers (not from mihomo.exe)' {
    # Kill-switch lets only mihomo.exe reach proxy servers. A success here means any app can use the proxy
    # (or the host behind it) directly, bypassing the tunnel: true for LAN addresses while allow-lan is on.
    foreach ($server in $proxyPorts.Keys) {
        $r = Test-NetConnection $server -Port $proxyPorts[$server] -WarningAction SilentlyContinue
        "{0}:{1} reachable={2} (interface {3}, source {4})" -f $server, $proxyPorts[$server], $r.TcpTestSucceeded,
            $r.InterfaceAlias, $r.SourceAddress.IPAddress
    }
}

Section 'Firewall profiles' { Get-NetFirewallProfile | Format-Table Name, Enabled, DefaultInboundAction, DefaultOutboundAction -AutoSize }
Section 'Network profiles' { Get-NetConnectionProfile | Format-Table InterfaceAlias, Name, NetworkCategory, IPv4Connectivity, IPv6Connectivity -AutoSize }

# Logs and config (secrets masked).
foreach ($log in 'service.log', 'service.log.1', 'mihomo.log', 'mihomo.log.1') {
    $p = Join-Path $Dir "logs\$log"
    if (Test-Path $p) { Copy-Item $p $out }
}
$secret = '^(\s*-?\s*(password|uuid|private-key|public-key|short-id|pre-shared-key|auth|auth-str|token|secret|obfs-password)\s*:\s*).*$'
foreach ($cfg in @(@{ Src = $configPath; Dst = 'config.masked.yaml' },
                   @{ Src = (Join-Path $Dir 'data\runtime.yaml'); Dst = 'runtime.masked.yaml' })) {
    if (Test-Path $cfg.Src) {
        (Get-Content $cfg.Src -Encoding UTF8) -replace $secret, '$1***' | Set-Content (Join-Path $out $cfg.Dst) -Encoding UTF8
    }
}

Write-Host "Done: $out"
exit 0

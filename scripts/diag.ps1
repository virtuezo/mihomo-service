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

# PowerShell decodes native output as OEM, but on localized Windows netsh writes UTF-8 and nslookup ANSI.
function Invoke-Native([System.Text.Encoding]$Encoding, [scriptblock]$Command) {
    $saved = [Console]::OutputEncoding
    try { [Console]::OutputEncoding = $Encoding; & $Command } finally { [Console]::OutputEncoding = $saved }
}

# What an application sees: external IP and country, fetched through the system (IE/WinINet) proxy or directly.
function Get-ExternalIp([bool]$SystemProxy) {
    $req = [System.Net.WebRequest]::Create('https://ipinfo.io/json')
    $req.Timeout = 10000
    $req.Proxy = if ($SystemProxy) { [System.Net.WebRequest]::GetSystemWebProxy() } else { $null }
    $resp = $req.GetResponse()
    try { (New-Object System.IO.StreamReader($resp.GetResponseStream())).ReadToEnd() } finally { $resp.Close() }
}

# Public IP as seen over UDP: STUN Binding Request, answer carries XOR-MAPPED-ADDRESS. UDP can take another
# path than TCP (e.g. a proxy without UDP support), so a leak may show up only here.
function Get-StunIp([string]$Server, [int]$Port) {
    $udp = New-Object System.Net.Sockets.UdpClient
    try {
        $udp.Client.ReceiveTimeout = 5000
        $tx = New-Object byte[] 12; (New-Object Random).NextBytes($tx)
        $req = [byte[]](0x00, 0x01, 0x00, 0x00, 0x21, 0x12, 0xA4, 0x42) + $tx
        [void]$udp.Send($req, $req.Length, $Server, $Port)
        $ep = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
        $resp = try { $udp.Receive([ref]$ep) } catch { return "${Server}:${Port} no UDP answer" }
        for ($i = 20; $i + 12 -le $resp.Length; $i += 4 + $len + ((4 - $len % 4) % 4)) {
            $type = $resp[$i] * 256 + $resp[$i + 1]; $len = $resp[$i + 2] * 256 + $resp[$i + 3]
            if ($type -eq 0x0020 -and $resp[$i + 5] -eq 1) {
                return "${Server}:${Port} sees " + ('{0}.{1}.{2}.{3}' -f ($resp[$i + 8] -bxor 0x21),
                    ($resp[$i + 9] -bxor 0x12), ($resp[$i + 10] -bxor 0xA4), ($resp[$i + 11] -bxor 0x42))
            }
        }
        "${Server}:${Port} answer without IPv4 XOR-MAPPED-ADDRESS"
    } finally { $udp.Close() }
}

$targets = [ordered]@{
    'internet 1.1.1.1' = '1.1.1.1'
    'internet 8.8.8.8' = '8.8.8.8'
    'internet v6 2606:4700:4700::1111' = '2606:4700:4700::1111'
}
# Proxy servers from config.yaml and the provider caches: they must be routed outside the tunnel (mihomo dials
# them itself). MihomoService.exe parses the YAML, so flow-style entries and any key order are understood.
$configPath = Join-Path $Dir 'config.yaml'
$proxies = @()
$serverNotes = @()
if (Test-Path $exe) {
    foreach ($line in & $exe servers) {
        if ($line.StartsWith('#')) { $serverNotes += $line; continue }
        $type, $server, $port = $line -split "`t"
        $proxies += [pscustomobject]@{ Type = $type; Server = $server; Port = $port }
        $targets["proxy $server"] = $server
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
    Invoke-Native ([System.Text.Encoding]::Default) { nslookup www.google.com 2>&1 }
}

Section 'Proxy settings (WinHTTP, WinINet, env)' {
    Invoke-Native ([System.Text.Encoding]::UTF8) { netsh winhttp show proxy }
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
    '-- over UDP (STUN), must match the IP above:'
    Get-StunIp 'stun.cloudflare.com' 3478
    Get-StunIp 'stun.l.google.com' 19302
}

Section 'Direct connection to proxy servers (not from mihomo.exe)' {
    # Kill-switch lets only mihomo.exe reach proxy servers. A success here means any app can use the proxy
    # (or the host behind it) directly, bypassing the tunnel: true for LAN addresses while allow-lan is on.
    $serverNotes
    foreach ($p in $proxies | Sort-Object Server, Port -Unique) {
        if ($p.Type -in 'hysteria', 'hysteria2', 'tuic', 'wireguard') {
            "{0}:{1} ({2}): UDP only, TCP test not applicable" -f $p.Server, $p.Port, $p.Type; continue
        }
        if ($p.Port -notmatch '^\d+$') { "{0}: port '{1}' is not a single number, skipped" -f $p.Server, $p.Port; continue }
        $r = Test-NetConnection $p.Server -Port ([int]$p.Port) -WarningAction SilentlyContinue
        "{0}:{1} ({2}) reachable={3} (interface {4}, source {5})" -f $p.Server, $p.Port, $p.Type, $r.TcpTestSucceeded,
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
# MihomoService.exe parses the YAML and hides every value that may be a secret, in any YAML style (a line
# regex missed flow-style entries, multi-line keys and subscription URLs). Without it nothing is copied.
foreach ($cfg in @(@{ Src = $configPath; Dst = 'config.masked.yaml' },
                   @{ Src = (Join-Path $Dir 'data\runtime.yaml'); Dst = 'runtime.masked.yaml' })) {
    if (-not (Test-Path $cfg.Src)) { continue }
    if (-not (Test-Path $exe)) { Write-Warning "$exe not found: $($cfg.Src) not copied"; continue }
    & $exe mask-config $cfg.Src (Join-Path $out $cfg.Dst)
    if ($LASTEXITCODE) { Write-Warning "masking $($cfg.Src) failed: not copied" }
}

Write-Host "Done: $out"
exit 0

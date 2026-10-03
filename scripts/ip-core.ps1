# Pure IP/CIDR helpers shared by optimization.ps1 and the test runner.
# Keep this file ASCII-only: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI.
# Official public Cloudflare IPv4 ranges (https://www.cloudflare.com/ips-v4)
$CloudflareRanges = @(
    '173.245.48.0/20', '103.21.244.0/22', '103.22.200.0/22', '103.31.4.0/22',
    '141.101.64.0/18', '108.162.192.0/18', '190.93.240.0/20', '188.114.96.0/20',
    '197.234.240.0/22', '198.41.128.0/17', '162.158.0.0/15', '104.16.0.0/12',
    '172.64.0.0/17', '172.64.128.0/18', '172.64.192.0/19', '172.64.224.0/22',
    '172.64.229.0/24', '172.64.230.0/23', '172.64.232.0/21', '172.64.240.0/21',
    '172.64.248.0/21', '172.65.0.0/16', '172.66.0.0/16', '172.67.0.0/16', '131.0.72.0/22'
)

# Strict dotted-quad check. The naive regex '^\d{1,3}(\.\d{1,3}){3}$' accepts two classes of input
# that .NET then interprets differently from what the user typed:
#   '010.1.1.1'   -> accepted, but [IPAddress]::Parse reads the leading zero as OCTAL -> 8.1.1.1
#   '256.1.1.1'   -> accepted, but Parse throws, which with $ErrorActionPreference='Stop' aborts a
#                    half-finished cf-apply run
# Both are rejected here: no leading zeros, every octet within 0-255.
function Test-StrictIPv4([string]$Value) {
    if (-not $Value) { return $false }
    if ($Value -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { return $false }
    foreach ($part in $Value.Split('.')) {
        if ($part.Length -gt 1 -and $part.StartsWith('0')) { return $false }
        $n = 0
        if (-not [int]::TryParse($part, [ref]$n)) { return $false }
        if ($n -lt 0 -or $n -gt 255) { return $false }
    }
    return $true
}

function Test-IpInCidr([string]$Ip, [string]$Cidr) {
    $parts = $Cidr -split '/'
    $netBytes = [System.Net.IPAddress]::Parse($parts[0]).GetAddressBytes()
    $ipBytes = [System.Net.IPAddress]::Parse($Ip).GetAddressBytes()
    $prefix = [int]$parts[1]
    $fullBytes = [math]::Ceiling($prefix / 8)
    for ($i = 0; $i -lt $fullBytes; $i++) {
        $bits = [math]::Min(8, $prefix - ($i * 8))
        if ($bits -lt 8) {
            $mask = 0xFF -shl (8 - $bits)
            if (($ipBytes[$i] -band $mask) -ne ($netBytes[$i] -band $mask)) { return $false }
        } elseif ($ipBytes[$i] -ne $netBytes[$i]) { return $false }
    }
    return $true
}

function Test-IpInCloudflare([string]$Ip) {
    foreach ($cidr in $CloudflareRanges) { if (Test-IpInCidr $Ip $cidr) { return $true } }
    return $false
}

function ConvertTo-IpString([long]$Value) {
    $a = [int]([math]::Floor($Value / 16777216) % 256)
    $b = [int]([math]::Floor($Value / 65536) % 256)
    $c = [int]([math]::Floor($Value / 256) % 256)
    $d = [int]($Value % 256)
    return "$a.$b.$c.$d"
}

function ConvertFrom-IpString([string]$Ip) {
    $bytes = [System.Net.IPAddress]::Parse($Ip).GetAddressBytes()
    return ([long]$bytes[0] * 16777216) + ([long]$bytes[1] * 65536) + ([long]$bytes[2] * 256) + [long]$bytes[3]
}

# Builds a large candidate pool spread evenly across every official Cloudflare IPv4 range.
# Brute-forcing the whole space is pointless (millions of addresses), so each range is walked
# with a fixed stride: broad coverage of every anycast prefix within a bounded time.
function Get-CfCandidatePool([int]$Target = 2000) {
    $pool = [System.Collections.Generic.List[string]]::new()
    $seen = @{}
    $perRange = [math]::Max(1, [int][math]::Ceiling($Target / [double]$CloudflareRanges.Count))
    foreach ($cidr in $CloudflareRanges) {
        if ($pool.Count -ge $Target) { break }
        $parts = $cidr -split '/'
        $prefix = [int]$parts[1]
        $baseValue = ConvertFrom-IpString $parts[0]
        $size = [long][math]::Pow(2, 32 - $prefix)
        $usable = [math]::Max(1, $size - 2)
        $step = [math]::Max(1, [long][math]::Floor($usable / [double]$perRange))
        $added = 0
        $offset = [long]1
        while ($added -lt $perRange -and $offset -le $usable -and $pool.Count -lt $Target) {
            $ip = ConvertTo-IpString ($baseValue + $offset)
            if (-not $seen.ContainsKey($ip)) {
                $seen[$ip] = $true
                $pool.Add($ip)
                $added++
            }
            $offset += $step
        }
    }
    return @($pool)
}

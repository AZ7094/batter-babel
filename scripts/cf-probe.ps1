# Generic HTTPS endpoint probing for Mode='probe' targets.
#
# This file is dot-sourced by optimization.ps1. It must stay ASCII-only: Windows PowerShell 5.1
# reads a BOM-less .ps1 as ANSI, and any non-ASCII byte breaks parsing.
#
# Why this module exists (defects D1-D7 in the design note):
#   D1  the candidate list was capped at 5 while real domains return 12+, and system DNS filled the
#       slots first, so the DoH sources were structurally starved -> round-robin merge now.
#   D3  every three-digit status code counted as success, so 33% of measured domains could "win"
#       with a 403/404/503 and get written into hosts -> per-domain rules + three-state verdict.
#   D4  all failure reasons collapsed into $null -> structured ProbeFailure enum instead.
#   D5  "median of 2 samples" actually returned the maximum (Floor(2/2)=1) -> odd sample count.
#
# The transport is curl (exit codes map directly onto failure classes) while all judgement lives in
# Resolve-ProbeOutcome, which is a pure function and therefore testable without any network.

# ---------------------------------------------------------------------------------------------
# Endpoint rules
# ---------------------------------------------------------------------------------------------
# A rule says what a healthy response for that domain looks like. StatusMin/StatusMax define the
# acceptable range; HeaderName (optional) must be present; BodyMustContain (optional, all of them)
# must appear in the first MaxBodyBytes of the body.
#
# Calibration evidence (all 12 candidates of each domain, see the design note):
#   www.limbuscompanyapi.com  -> 12/12 returned HTTP 400 with x-amz-apigw-id and an EMPTY body.
#                                Requiring 2xx would have failed the tool's most important domain.
#   notice.limbuscompanyapi.com -> 12/12 returned HTTP 200 with a 2701-byte API payload.
#   everything else           -> measured mix: 2xx 14%, 3xx 43%, 4xx 33%, transport failure 10%.
#
# Re-verified live while wiring this in: the two domains are served by DIFFERENT backends, so they
# must not share a rule. www answers via API Gateway (x-amz-apigw-id present, empty body at 400),
# while notice is plain S3 behind CloudFront (Server: AmazonS3, X-Amz-Cf-Pop, no apigw header) and
# carries the business payload. Requiring the apigw header on notice marks every candidate unusable.
$ProbeEndpointRules = @(
    @{ Domain = 'www.limbuscompanyapi.com'; StatusMin = 200; StatusMax = 499;
       HeaderName = 'x-amz-apigw-id'; BodyMustContain = @(); MaxBodyBytes = 0 },
    @{ Domain = 'notice.limbuscompanyapi.com'; StatusMin = 200; StatusMax = 200;
       HeaderName = $null; BodyMustContain = @('latestUpdateDate', 'noticeDetailList'); MaxBodyBytes = 32768 }
)

# Domains that answer with an anti-bot page (403/405) keep their result but rank last and are never
# written to hosts unless the caller explicitly opts in.
$ProbeKeepStatus = @(403, 405)

function Get-ProbeRule([string]$Domain) {
    foreach ($r in $ProbeEndpointRules) {
        if ($r.Domain -eq $Domain) { return $r }
    }
    # Default: a healthy endpoint answers 2xx or 3xx. 4xx/5xx means the address is not serving this
    # domain properly, and a transport failure means it is not serving anything at all.
    return @{ Domain = $Domain; StatusMin = 200; StatusMax = 399;
              HeaderName = $null; BodyMustContain = @(); MaxBodyBytes = 4096 }
}

# ---------------------------------------------------------------------------------------------
# Candidate discovery
# ---------------------------------------------------------------------------------------------
function Get-ProbeDohRecords([string]$DohUrl, [string]$Domain, [int]$TimeoutSec = 3) {
    $curl = "$env:SystemRoot\System32\curl.exe"
    try {
        $encoded = [uri]::EscapeDataString($Domain)
        $json = & $curl --ssl-no-revoke -s -m $TimeoutSec -H 'Accept: application/dns-json' `
            "$DohUrl`?name=$encoded&type=A" 2>$null
        if (-not $json) { return @() }
        $obj = ($json -join '') | ConvertFrom-Json
        if ($obj.Status -ne 0 -or -not $obj.Answer) { return @() }
        # type 1 = A record. type 5 = CNAME: followed by the resolver, so no extra work is needed
        # here -- but a CNAME-only answer yields no A record and therefore no candidate (D2).
        return @($obj.Answer | Where-Object { $_.type -eq 1 } | ForEach-Object { $_.data } |
            Where-Object { Test-StrictIPv4 $_ })
    } catch {
        return @()
    }
}

# Round-robin merge of every source. Interleaving (instead of "system DNS first, then DoH") is what
# fixes D1: with a 4-address system answer and a 5-slot cap, the DoH sources previously never got a
# slot at all.
function Get-ProbeCandidates {
    param(
        [string]$Domain,
        [string[]]$DohSources = @(
            'https://dns.alidns.com/resolve',
            'https://doh.pub/dns-query'
        ),
        [int]$Max = 24
    )

    $lists = @()
    $diagnostics = @()

    try {
        $system = @([System.Net.Dns]::GetHostAddresses($Domain) |
            Where-Object { $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork } |
            ForEach-Object { $_.IPAddressToString } |
            Where-Object { Test-StrictIPv4 $_ })
        $lists += , $system
        $diagnostics += "system dns: $($system.Count)"
    } catch {
        $lists += , @()
        $diagnostics += "system dns: FAILED"
    }

    foreach ($src in $DohSources) {
        $recs = @(Get-ProbeDohRecords $src $Domain)
        $lists += , $recs
        $diagnostics += "${src}: $($recs.Count)"
    }

    $out = @()
    $seen = @{}
    $index = 0
    while ($out.Count -lt $Max) {
        $added = $false
        foreach ($list in $lists) {
            if ($index -lt $list.Count) {
                $ip = $list[$index]
                if ($ip -and -not $seen.ContainsKey($ip)) {
                    $seen[$ip] = $true
                    $out += $ip
                    $added = $true
                    if ($out.Count -ge $Max) { break }
                }
            }
        }
        if (-not $added) { break }
        $index++
    }

    return [ordered]@{ ips = @($out); diagnostics = @($diagnostics) }
}

# ---------------------------------------------------------------------------------------------
# Transport (curl)
# ---------------------------------------------------------------------------------------------
# curl exit codes used as the failure classification:
#   0  = response received
#   6  = could not resolve host
#   7  = failed to connect
#   28 = operation timeout
#   35 = SSL connect error          (TLS handshake failed)
#   60 = SSL certificate problem    (certificate rejected)
# https://curl.se/docs/manpage.html#EXITCODES
function Invoke-ProbeRequest {
    param(
        [string]$Domain,
        [string]$Ip,
        [int]$TimeoutSec = 5,
        [int]$MaxBodyBytes = 4096
    )

    $curl = "$env:SystemRoot\System32\curl.exe"
    $bodyFile = [System.IO.Path]::GetTempFileName()
    $headFile = [System.IO.Path]::GetTempFileName()
    try {
        # The URI, TLS SNI, certificate check and Host header all use the real domain; only the
        # address is pinned with --resolve. Certificate verification is never disabled.
        # ${Domain} braces are required: "$Domain`:443" would parse the colon as a scope separator.
        $raw = & $curl --ssl-no-revoke --resolve "${Domain}:443:$Ip" "https://$Domain/" `
            -o $bodyFile -D $headFile -s -m $TimeoutSec `
            -w '%{time_starttransfer}|%{http_code}|%{size_download}' 2>$null
        $exit = $LASTEXITCODE
        $parts = ($raw -join '') -split '\|'
        $ttfb = 0.0
        $status = 0
        $size = 0
        if ($parts.Count -ge 3) {
            [void][double]::TryParse($parts[0], [ref]$ttfb)
            [void][int]::TryParse($parts[1], [ref]$status)
            [void][int]::TryParse($parts[2], [ref]$size)
        }

        $headers = ''
        $body = ''
        try {
            if (Test-Path -LiteralPath $headFile) {
                $headers = [System.IO.File]::ReadAllText($headFile, [System.Text.UTF8Encoding]::new($false))
            }
            if (Test-Path -LiteralPath $bodyFile) {
                $bytes = [System.IO.File]::ReadAllBytes($bodyFile)
                if ($MaxBodyBytes -gt 0 -and $bytes.Length -gt $MaxBodyBytes) {
                    $bytes = $bytes[0..($MaxBodyBytes - 1)]
                }
                $body = [System.Text.Encoding]::UTF8.GetString($bytes)
            }
        } catch {}

        return [ordered]@{
            exitCode = $exit
            statusCode = $status
            elapsedMs = [math]::Round($ttfb * 1000, 0)
            headers = $headers
            body = $body
            bodyBytes = $size
        }
    } catch {
        return [ordered]@{ exitCode = -1; statusCode = 0; elapsedMs = 0; headers = ''; body = ''; bodyBytes = 0 }
    } finally {
        Remove-Item -LiteralPath $bodyFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $headFile -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------------------------
# Judgement (pure -- no network, fully testable)
# ---------------------------------------------------------------------------------------------
# Verdicts:
#   Usable    the endpoint answered exactly the way this domain is supposed to -> may be ranked
#             and written to hosts
#   Keep      reachable, but the response looks like an anti-bot page -> ranked last, not written
#   Unusable  transport failure or a response that does not match the rule -> never written
function Resolve-ProbeOutcome {
    param($Probe, $Rule)

    if (-not $Probe) { return [ordered]@{ verdict = 'Unusable'; reason = 'NoResponse' } }

    switch ($Probe.exitCode) {
        0  { }
        6  { return [ordered]@{ verdict = 'Unusable'; reason = 'DnsFailure' } }
        7  { return [ordered]@{ verdict = 'Unusable'; reason = 'ConnectFailure' } }
        28 { return [ordered]@{ verdict = 'Unusable'; reason = 'Timeout' } }
        35 { return [ordered]@{ verdict = 'Unusable'; reason = 'TlsHandshake' } }
        60 { return [ordered]@{ verdict = 'Unusable'; reason = 'Certificate' } }
        default { return [ordered]@{ verdict = 'Unusable'; reason = "CurlExit$($Probe.exitCode)" } }
    }

    $status = [int]$Probe.statusCode
    if ($status -lt 100 -or $status -gt 599) {
        return [ordered]@{ verdict = 'Unusable'; reason = 'NoStatus' }
    }

    if ($ProbeKeepStatus -contains $status) {
        return [ordered]@{ verdict = 'Keep'; reason = "HttpStatus$status" }
    }

    if ($status -lt $Rule.StatusMin -or $status -gt $Rule.StatusMax) {
        return [ordered]@{ verdict = 'Unusable'; reason = "HttpStatus$status" }
    }

    if ($Rule.HeaderName) {
        if (-not $Probe.headers -or $Probe.headers -notmatch [regex]::Escape($Rule.HeaderName)) {
            return [ordered]@{ verdict = 'Unusable'; reason = 'MissingHeader' }
        }
    }

    if ($Rule.BodyMustContain -and @($Rule.BodyMustContain).Count -gt 0) {
        foreach ($needle in $Rule.BodyMustContain) {
            if (-not $Probe.body -or -not $Probe.body.Contains($needle)) {
                return [ordered]@{ verdict = 'Unusable'; reason = 'BusinessContent' }
            }
        }
    }

    return [ordered]@{ verdict = 'Usable'; reason = 'None' }
}

# Median of the successful samples. With an even count the two middle values are averaged rather
# than picking the larger one -- picking index Floor(n/2) is exactly how the old "median of 2"
# silently returned the maximum (D5).
function Get-ProbeMedian([double[]]$Values) {
    $clean = @($Values | Where-Object { $_ -gt 0 } | Sort-Object)
    if ($clean.Count -eq 0) { return $null }
    $mid = [math]::Floor($clean.Count / 2)
    if ($clean.Count % 2 -eq 1) { return [math]::Round($clean[$mid], 0) }
    return [math]::Round(($clean[$mid - 1] + $clean[$mid]) / 2.0, 0)
}

# Probes one domain across all its candidates and returns every result, ranked.
# Usable first (by median), then Keep, then Unusable. Unusable entries are kept in the list so the
# UI can explain why a domain fell back (D4) -- they are simply never written to hosts.
function Invoke-ProbeDomain {
    param(
        [string]$Domain,
        [string[]]$Candidates,
        [int]$Samples = 3,
        [int]$TimeoutSec = 5,
        [int]$DeadlineSeconds = 45
    )

    $rule = Get-ProbeRule $Domain
    $deadline = (Get-Date).AddSeconds($DeadlineSeconds)
    $results = @()
    $completed = 0

    foreach ($ip in @($Candidates)) {
        if ((Get-Date) -gt $deadline) {
            $results += [ordered]@{
                ip = $ip; verdict = 'Unusable'; reason = 'DeadlineExceeded'
                medianMs = $null; samples = 0
            }
            continue
        }

        $times = @()
        $lastProbe = $null
        $lastOutcome = $null
        for ($s = 0; $s -lt $Samples; $s++) {
            $probe = Invoke-ProbeRequest -Domain $Domain -Ip $ip -TimeoutSec $TimeoutSec -MaxBodyBytes $rule.MaxBodyBytes
            $outcome = Resolve-ProbeOutcome -Probe $probe -Rule $rule
            $lastProbe = $probe
            $lastOutcome = $outcome
            if ($outcome.verdict -ne 'Usable' -and $outcome.verdict -ne 'Keep') { break }
            if ($probe.elapsedMs -gt 0) { $times += [double]$probe.elapsedMs }
        }

        $results += [ordered]@{
            ip = $ip
            verdict = $lastOutcome.verdict
            reason = $lastOutcome.reason
            medianMs = (Get-ProbeMedian $times)
            samples = $times.Count
            statusCode = $lastProbe.statusCode
        }
        $completed++
    }

    $ranked = @(
        $results | Where-Object { $_.verdict -eq 'Usable' } | Sort-Object { if ($_.medianMs -eq $null) { [double]::MaxValue } else { [double]$_.medianMs } }
    ) + @($results | Where-Object { $_.verdict -eq 'Keep' } | Sort-Object { if ($_.medianMs -eq $null) { [double]::MaxValue } else { [double]$_.medianMs } }) `
      + @($results | Where-Object { $_.verdict -eq 'Unusable' })

    $usable = @($results | Where-Object { $_.verdict -eq 'Usable' })
    $best = $null
    if ($usable.Count -gt 0) {
        $best = @($usable | Sort-Object { [double]$_.medianMs })[0]
    }

    return [ordered]@{
        domain = $Domain
        rule = $rule
        candidates = @($Candidates).Count
        probed = $completed
        results = @($ranked)
        best = $best
        ok = ($best -ne $null)
    }
}

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
# Turns the artifacts of one curl run (exit code, the -w line, the header/body files) into the
# probe record Resolve-ProbeOutcome expects. The serial and the parallel transport share this, so
# both report exactly the same fields and the judgement layer stays oblivious to how it was fetched.
function ConvertTo-ProbeResult {
    param(
        [int]$ExitCode,
        [string]$StatText,
        [string]$HeadPath,
        [string]$BodyPath,
        [int]$MaxBodyBytes = 4096
    )

    $parts = ($StatText -join '') -split '\|'
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
        if ($HeadPath -and (Test-Path -LiteralPath $HeadPath)) {
            $headers = [System.IO.File]::ReadAllText($HeadPath, [System.Text.UTF8Encoding]::new($false))
        }
        if ($BodyPath -and (Test-Path -LiteralPath $BodyPath)) {
            $bytes = [System.IO.File]::ReadAllBytes($BodyPath)
            if ($MaxBodyBytes -gt 0 -and $bytes.Length -gt $MaxBodyBytes) {
                $bytes = $bytes[0..($MaxBodyBytes - 1)]
            }
            $body = [System.Text.Encoding]::UTF8.GetString($bytes)
        }
    } catch {}

    return [ordered]@{
        exitCode = $ExitCode
        statusCode = $status
        elapsedMs = [math]::Round($ttfb * 1000, 0)
        headers = $headers
        body = $body
        bodyBytes = $size
    }
}

# Start-Process joins the argument array with spaces and there is no shell to re-split them, so an
# argument containing whitespace (a temp path under "C:\Users\John Doe\...") has to be quoted here.
function Format-CurlArg([string]$Value) {
    if ($Value -match '\s') { return '"' + $Value + '"' }
    return $Value
}

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
        return ConvertTo-ProbeResult -ExitCode $LASTEXITCODE -StatText ($raw -join '') `
            -HeadPath $headFile -BodyPath $bodyFile -MaxBodyBytes $MaxBodyBytes
    } catch {
        return [ordered]@{ exitCode = -1; statusCode = 0; elapsedMs = 0; headers = ''; body = ''; bodyBytes = 0 }
    } finally {
        Remove-Item -LiteralPath $bodyFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $headFile -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------------------------
# Parallel transport
# ---------------------------------------------------------------------------------------------
# Every candidate of one round is launched at once and the whole round is waited on together.
#
# Why (measured on www.limbuscompanyapi.com, 12 candidates, 3 samples): the serial loop needed
# 45.5s -- each healthy sample cost 0.7-2.8s and each unhealthy one the full 5s timeout -- which
# both exhausted the per-domain deadline (6 of the 12 candidates were never probed at all, so the
# "best" address was really "fastest among the ones DNS happened to list first") and left the UI
# without a single progress update for that whole minute. A parallel round costs the SLOWEST single
# request instead of the sum, and each finished round is a natural place to report progress.
#
# Windows PowerShell 5.1 has no ForEach-Object -Parallel, and runspace pools would drag the whole
# module into session-state juggling, so the concurrency is plain child processes: one curl per
# candidate, each writing its own -w line, header file and body file.
function Start-ProbeRequest {
    param(
        [string]$Domain,
        [string]$Ip,
        [int]$TimeoutSec = 5,
        [string]$WorkDir,
        [int]$Index = 0
    )

    $curl = "$env:SystemRoot\System32\curl.exe"
    $bodyFile = Join-Path $WorkDir "p$Index.body"
    $headFile = Join-Path $WorkDir "p$Index.head"
    $statFile = Join-Path $WorkDir "p$Index.stat"
    $errFile = Join-Path $WorkDir "p$Index.err"

    # ${Domain} braces are required: "$Domain`:443" would parse the colon as a scope separator.
    $argList = @(
        '--ssl-no-revoke'
        '--resolve', "${Domain}:443:$Ip"
        "https://$Domain/"
        '-o', (Format-CurlArg $bodyFile)
        '-D', (Format-CurlArg $headFile)
        '-s'
        '-m', "$TimeoutSec"
        '-w', '%{time_starttransfer}|%{http_code}|%{size_download}'
    )

    $proc = Start-Process -FilePath $curl -ArgumentList $argList -NoNewWindow -PassThru `
        -RedirectStandardOutput $statFile -RedirectStandardError $errFile

    return [ordered]@{
        ip = $Ip
        proc = $proc
        bodyFile = $bodyFile
        headFile = $headFile
        statFile = $statFile
        timedOut = $false
    }
}

# Runs one round over $Ips and returns an ordered map ip -> probe record.
function Invoke-ProbeBatch {
    param(
        [string]$Domain,
        [string[]]$Ips,
        [int]$TimeoutSec = 5,
        [int]$MaxBodyBytes = 4096
    )

    $out = [ordered]@{}
    $targets = @($Ips)
    if ($targets.Count -eq 0) { return $out }

    $workDir = Join-Path $env:TEMP ("bb-probe-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $workDir -Force | Out-Null

    try {
        $states = @()
        $index = 0
        foreach ($ip in $targets) {
            try {
                $states += Start-ProbeRequest -Domain $Domain -Ip $ip -TimeoutSec $TimeoutSec -WorkDir $workDir -Index $index
            } catch {
                # A spawn failure must not abort the round: the candidate simply reports no response.
                $states += [ordered]@{ ip = $ip; proc = $null; bodyFile = ''; headFile = ''; statFile = ''; timedOut = $false }
            }
            $index++
        }

        # ONE deadline for the whole round, evaluated from a single start point. curl also carries
        # its own -m, so this only catches a process that never returns at all; waiting per process
        # with a per-process timeout is what made the old latency screen take 211s (design note).
        $roundDeadline = (Get-Date).AddSeconds($TimeoutSec + 8)
        foreach ($st in $states) {
            if (-not $st.proc) { continue }
            try {
                $remaining = [int][math]::Max(0, ($roundDeadline - (Get-Date)).TotalMilliseconds)
                if (-not $st.proc.WaitForExit($remaining)) {
                    try { $st.proc.Kill() } catch {}
                    $st.timedOut = $true
                }
            } catch {}
        }

        foreach ($st in $states) {
            $exit = -1
            $stat = ''
            if ($st.proc) {
                try { $exit = $st.proc.ExitCode } catch { $exit = -1 }
                # Killed by our own deadline => the same classification curl would have produced.
                if ($st.timedOut) { $exit = 28 }
                try {
                    if ($st.statFile -and (Test-Path -LiteralPath $st.statFile)) {
                        $stat = [System.IO.File]::ReadAllText($st.statFile, [System.Text.UTF8Encoding]::new($false))
                    }
                } catch {}
            }
            $out[$st.ip] = ConvertTo-ProbeResult -ExitCode $exit -StatText $stat `
                -HeadPath $st.headFile -BodyPath $st.bodyFile -MaxBodyBytes $MaxBodyBytes
        }
    } finally {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    return $out
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
#
# Sampling is organised in ROUNDS, not per candidate: round 1 measures every candidate at once, and
# each following round only re-measures the ones that still look usable. A candidate that fails
# (or that is a Keep/anti-bot answer) stops immediately, exactly as before -- only the waiting is
# shared now. $OnProgress is invoked after every round as ($workDone, $workTotal, $roundsDone).
function Invoke-ProbeDomain {
    param(
        [string]$Domain,
        [string[]]$Candidates,
        [int]$Samples = 3,
        [int]$TimeoutSec = 5,
        [int]$DeadlineSeconds = 45,
        [scriptblock]$OnProgress
    )

    $rule = Get-ProbeRule $Domain
    $deadline = (Get-Date).AddSeconds($DeadlineSeconds)
    $candidateList = @($Candidates)
    $total = $candidateList.Count

    # Per-candidate accumulators, keyed by address but reported back in candidate order.
    $sampleTimes = @{}
    $lastProbe = @{}
    $lastOutcome = @{}
    foreach ($ip in $candidateList) {
        $sampleTimes[$ip] = @()
        $lastProbe[$ip] = $null
        $lastOutcome[$ip] = $null
    }

    $pending = @($candidateList)
    # Progress is measured in WORK UNITS, not finished candidates: with three samples a candidate
    # is only "finished" after the third round, so counting finished candidates would leave the bar
    # frozen at its start value until the very last round. A candidate that stops early (failure or
    # anti-bot answer) is credited its whole share at once, because that work really is over.
    $workTotal = [math]::Max(1, $total * $Samples)
    if ($OnProgress) { & $OnProgress 0 $workTotal 0 }
    for ($round = 0; $round -lt $Samples -and $pending.Count -gt 0; $round++) {
        if ((Get-Date) -gt $deadline) {
            foreach ($ip in $pending) {
                $lastOutcome[$ip] = [ordered]@{ verdict = 'Unusable'; reason = 'DeadlineExceeded' }
            }
            $pending = @()
            if ($OnProgress) { & $OnProgress $workTotal $workTotal ($round + 1) }
            break
        }

        $batch = Invoke-ProbeBatch -Domain $Domain -Ips $pending -TimeoutSec $TimeoutSec -MaxBodyBytes $rule.MaxBodyBytes

        $stillPending = @()
        foreach ($ip in $pending) {
            $probe = $batch[$ip]
            $outcome = Resolve-ProbeOutcome -Probe $probe -Rule $rule
            $lastProbe[$ip] = $probe
            $lastOutcome[$ip] = $outcome
            # Unusable (and Keep) results are final after one sample: no point measuring an anti-bot
            # page twice. Only candidates that may be written keep collecting samples.
            if ($outcome.verdict -ne 'Usable' -and $outcome.verdict -ne 'Keep') { continue }
            if ($probe.elapsedMs -gt 0) { $sampleTimes[$ip] += [double]$probe.elapsedMs }
            if ($round + 1 -lt $Samples) { $stillPending += $ip }
        }
        $pending = $stillPending

        $workDone = 0
        foreach ($ip in $candidateList) {
            if ($stillPending -contains $ip) { $workDone += @($sampleTimes[$ip]).Count } else { $workDone += $Samples }
        }
        if ($OnProgress) { & $OnProgress $workDone $workTotal ($round + 1) }
    }

    $results = @()
    foreach ($ip in $candidateList) {
        $times = @($sampleTimes[$ip])
        $probe = $lastProbe[$ip]
        $outcome = $lastOutcome[$ip]
        if (-not $outcome) { $outcome = [ordered]@{ verdict = 'Unusable'; reason = 'DeadlineExceeded' } }
        $results += [ordered]@{
            ip = $ip
            verdict = $outcome.verdict
            reason = $outcome.reason
            medianMs = (Get-ProbeMedian $times)
            samples = $times.Count
            statusCode = $(if ($probe) { $probe.statusCode } else { 0 })
        }
    }
    $completed = @($candidateList | Where-Object { @($sampleTimes[$_]).Count -gt 0 }).Count

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

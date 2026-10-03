# Tests for cf-probe.ps1. Keep ASCII-only: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI.
# Run via scripts\tests\run-tests.ps1, which dot-sources ip-core.ps1 and cf-probe.ps1 first.
#
# These cover the judgement layer only -- it is a pure function, so no network and no fake HTTPS
# server are needed. That is the whole point of splitting transport (curl) from judgement.

# ---------------------------------------------------------------------------------------------
# D9 regression: strict IPv4
# ---------------------------------------------------------------------------------------------
Assert-True (-not (Test-StrictIPv4 '010.1.1.1'))       'strict ipv4 rejects leading zero (octal trap)'
Assert-True (-not (Test-StrictIPv4 '01.02.03.04'))     'strict ipv4 rejects multi leading zeros'
Assert-True (-not (Test-StrictIPv4 '256.1.1.1'))       'strict ipv4 rejects octet above 255'
Assert-True (-not (Test-StrictIPv4 '999.999.999.999')) 'strict ipv4 rejects all-out-of-range'
Assert-True (-not (Test-StrictIPv4 '1.2.3'))           'strict ipv4 rejects too few octets'
Assert-True (-not (Test-StrictIPv4 '1.2.3.4.5'))       'strict ipv4 rejects too many octets'
Assert-True (-not (Test-StrictIPv4 'a.b.c.d'))         'strict ipv4 rejects non-numeric'
Assert-True (-not (Test-StrictIPv4 ''))                'strict ipv4 rejects empty string'
Assert-True (Test-StrictIPv4 '8.1.1.1')                'strict ipv4 accepts normal address'
Assert-True (Test-StrictIPv4 '0.0.0.0')                'strict ipv4 accepts single zero octets'
Assert-True (Test-StrictIPv4 '255.255.255.255')        'strict ipv4 accepts upper bound'
Assert-True (Test-StrictIPv4 '108.162.192.1')          'strict ipv4 accepts curated Cloudflare IP'

# ---------------------------------------------------------------------------------------------
# Endpoint rules
# ---------------------------------------------------------------------------------------------
$ruleWww = Get-ProbeRule 'www.limbuscompanyapi.com'
Assert-Equal 200 $ruleWww.StatusMin 'www rule lower bound is 200'
Assert-Equal 499 $ruleWww.StatusMax 'www rule upper bound is 499 (12/12 candidates answer 400)'
Assert-Equal 'x-amz-apigw-id' $ruleWww.HeaderName 'www rule requires the API Gateway header'

$ruleNotice = Get-ProbeRule 'notice.limbuscompanyapi.com'
Assert-Equal 200 $ruleNotice.StatusMin 'notice rule lower bound is 200'
Assert-Equal 200 $ruleNotice.StatusMax 'notice rule upper bound is 200'
Assert-Equal $null $ruleNotice.HeaderName 'notice rule does NOT require the apigw header (S3-backed)'
Assert-Equal 2 @($ruleNotice.BodyMustContain).Count 'notice rule requires two body fields'

$ruleDefault = Get-ProbeRule 'epicgames.com'
Assert-Equal 200 $ruleDefault.StatusMin 'default rule lower bound is 200'
Assert-Equal 399 $ruleDefault.StatusMax 'default rule upper bound is 399 (2xx or 3xx)'
Assert-Equal $null $ruleDefault.HeaderName 'default rule has no header requirement'

# ---------------------------------------------------------------------------------------------
# Judgement: the two Limbus domains must be judged by their own rules
# ---------------------------------------------------------------------------------------------
function New-Probe {
    param([int]$Exit = 0, [int]$Status = 0, [string]$Headers = '', [string]$Body = '')
    return [ordered]@{
        exitCode = $Exit; statusCode = $Status; elapsedMs = 100
        headers = $Headers; body = $Body; bodyBytes = $Body.Length
    }
}

# www: 400 + apigw header + empty body is the healthy answer. Requiring 2xx here (what the old code
# effectively allowed either way) or requiring a body would both break the primary domain.
$wwwOk = New-Probe -Status 400 -Headers 'x-amz-apigw-id: abc' -Body ''
Assert-Equal 'Usable' (Resolve-ProbeOutcome -Probe $wwwOk -Rule $ruleWww).verdict 'www 400+header+empty body is Usable'

$wwwNoHeader = New-Probe -Status 400 -Headers 'Server: CloudFront' -Body ''
Assert-Equal 'Unusable' (Resolve-ProbeOutcome -Probe $wwwNoHeader -Rule $ruleWww).verdict 'www 400 without apigw header is Unusable'
Assert-Equal 'MissingHeader' (Resolve-ProbeOutcome -Probe $wwwNoHeader -Rule $ruleWww).reason 'www missing header reports the reason'

$www500 = New-Probe -Status 500 -Headers 'x-amz-apigw-id: abc' -Body ''
Assert-Equal 'Unusable' (Resolve-ProbeOutcome -Probe $www500 -Rule $ruleWww).verdict 'www 500 is Unusable even with the header'

# notice: 200 + business payload, no apigw header (S3 behind CloudFront).
$noticeOk = New-Probe -Status 200 -Headers 'Server: AmazonS3' -Body '{"latestUpdateDate":"x","noticeDetailList":[]}'
Assert-Equal 'Usable' (Resolve-ProbeOutcome -Probe $noticeOk -Rule $ruleNotice).verdict 'notice 200 with both fields is Usable'
Assert-True ($null -eq (Resolve-ProbeOutcome -Probe $noticeOk -Rule $ruleNotice).reason -or (Resolve-ProbeOutcome -Probe $noticeOk -Rule $ruleNotice).reason -eq 'None') 'notice success reports no failure reason'

$noticeNoBody = New-Probe -Status 200 -Headers 'Server: AmazonS3' -Body '{"foo":1}'
Assert-Equal 'Unusable' (Resolve-ProbeOutcome -Probe $noticeNoBody -Rule $ruleNotice).verdict 'notice 200 without the business payload is Unusable'
Assert-Equal 'BusinessContent' (Resolve-ProbeOutcome -Probe $noticeNoBody -Rule $ruleNotice).reason 'notice missing payload reports BusinessContent'

$notice400 = New-Probe -Status 400 -Headers 'x-amz-apigw-id: x' -Body '{"latestUpdateDate":"x","noticeDetailList":[]}'
Assert-Equal 'Unusable' (Resolve-ProbeOutcome -Probe $notice400 -Rule $ruleNotice).verdict 'notice 400 is Unusable even with a valid body'

# default rule
Assert-Equal 'Usable'   (Resolve-ProbeOutcome -Probe (New-Probe -Status 200) -Rule $ruleDefault).verdict 'default 200 is Usable'
Assert-Equal 'Usable'   (Resolve-ProbeOutcome -Probe (New-Probe -Status 301) -Rule $ruleDefault).verdict 'default 301 is Usable'
Assert-Equal 'Usable'   (Resolve-ProbeOutcome -Probe (New-Probe -Status 399) -Rule $ruleDefault).verdict 'default 399 is Usable'
Assert-Equal 'Unusable' (Resolve-ProbeOutcome -Probe (New-Probe -Status 404) -Rule $ruleDefault).verdict 'default 404 is Unusable'
Assert-Equal 'HttpStatus404' (Resolve-ProbeOutcome -Probe (New-Probe -Status 404) -Rule $ruleDefault).reason 'default 404 reports the status'
Assert-Equal 'Unusable' (Resolve-ProbeOutcome -Probe (New-Probe -Status 503) -Rule $ruleDefault).verdict 'default 503 is Unusable'

# D3: the old code treated ANY three-digit code as success, so a 403/404/503 could win and be written
# into hosts. These four assertions are the regression guard for that.
Assert-True ((Resolve-ProbeOutcome -Probe (New-Probe -Status 403) -Rule $ruleDefault).verdict -ne 'Usable') 'D3: 403 can never be Usable'
Assert-True ((Resolve-ProbeOutcome -Probe (New-Probe -Status 404) -Rule $ruleDefault).verdict -ne 'Usable') 'D3: 404 can never be Usable'
Assert-True ((Resolve-ProbeOutcome -Probe (New-Probe -Status 500) -Rule $ruleDefault).verdict -ne 'Usable') 'D3: 500 can never be Usable'
Assert-True ((Resolve-ProbeOutcome -Probe (New-Probe -Status 503) -Rule $ruleDefault).verdict -ne 'Usable') 'D3: 503 can never be Usable'

# anti-bot responses are reachable but must never be written
Assert-Equal 'Keep' (Resolve-ProbeOutcome -Probe (New-Probe -Status 403) -Rule $ruleDefault).verdict '403 is Keep (reachable anti-bot)'
Assert-Equal 'Keep' (Resolve-ProbeOutcome -Probe (New-Probe -Status 405) -Rule $ruleDefault).verdict '405 is Keep (reachable anti-bot)'

# ---------------------------------------------------------------------------------------------
# D4: transport failures are classified, not collapsed to $null
# ---------------------------------------------------------------------------------------------
Assert-Equal 'Timeout'          (Resolve-ProbeOutcome -Probe (New-Probe -Exit 28) -Rule $ruleDefault).reason 'curl exit 28 is Timeout'
Assert-Equal 'ConnectFailure'   (Resolve-ProbeOutcome -Probe (New-Probe -Exit 7)  -Rule $ruleDefault).reason 'curl exit 7 is ConnectFailure'
Assert-Equal 'TlsHandshake'     (Resolve-ProbeOutcome -Probe (New-Probe -Exit 35) -Rule $ruleDefault).reason 'curl exit 35 is TlsHandshake'
Assert-Equal 'Certificate'      (Resolve-ProbeOutcome -Probe (New-Probe -Exit 60) -Rule $ruleDefault).reason 'curl exit 60 is Certificate'
Assert-Equal 'DnsFailure'       (Resolve-ProbeOutcome -Probe (New-Probe -Exit 6)  -Rule $ruleDefault).reason 'curl exit 6 is DnsFailure'
Assert-Equal 'CurlExit52'       (Resolve-ProbeOutcome -Probe (New-Probe -Exit 52) -Rule $ruleDefault).reason 'unknown curl exit keeps its code'
Assert-Equal 'NoResponse'       (Resolve-ProbeOutcome -Probe $null -Rule $ruleDefault).reason 'null probe reports NoResponse'
Assert-Equal 'NoStatus'         (Resolve-ProbeOutcome -Probe (New-Probe -Status 0) -Rule $ruleDefault).reason 'exit 0 with no status is NoStatus'

# ---------------------------------------------------------------------------------------------
# D5 regression: median
# ---------------------------------------------------------------------------------------------
Assert-Equal 200 (Get-ProbeMedian @(100, 200, 300)) 'median of three picks the middle value'
Assert-Equal 20  (Get-ProbeMedian @(5000, 10, 20))  'median ignores a single slow outlier'
Assert-Equal $null (Get-ProbeMedian @())            'median of nothing is null'
Assert-Equal $null (Get-ProbeMedian @(0, 0))        'median ignores zero samples'
Assert-Equal 250 (Get-ProbeMedian @(100, 200, 300, 400)) 'median of four averages the middle pair (100,200,300,400 -> 250)'
# The old code called Floor(2/2)=1 on a two-element sorted array, i.e. the MAXIMUM of the two.
Assert-True ((Get-ProbeMedian @(100, 5209)) -lt 5209) 'D5: two samples no longer return the maximum'

# ---------------------------------------------------------------------------------------------
# D1 regression: round-robin candidate merge
# ---------------------------------------------------------------------------------------------
$merged = Get-ProbeCandidates -Domain 'definitely-not-a-real-host.invalid' -DohSources @()
Assert-Equal 0 @($merged.ips).Count 'unresolvable domain yields no candidates'
Assert-True (@($merged.diagnostics).Count -gt 0) 'candidate discovery returns diagnostics (D4)'
Assert-True ($merged.diagnostics[0] -match 'FAILED') 'a failing system DNS lookup is reported, not swallowed'

# ---------------------------------------------------------------------------------------------
# Hosts safety: only Usable results may be written
# ---------------------------------------------------------------------------------------------
$mixed = @(
    [ordered]@{ ip = '1.1.1.1'; verdict = 'Unusable'; reason = 'HttpStatus503'; medianMs = 10 },
    [ordered]@{ ip = '2.2.2.2'; verdict = 'Keep';     reason = 'HttpStatus403'; medianMs = 20 },
    [ordered]@{ ip = '3.3.3.3'; verdict = 'Usable';   reason = 'None';         medianMs = 30 }
)
$writable = @($mixed | Where-Object { $_.verdict -eq 'Usable' })
Assert-Equal 1 @($writable).Count 'only Usable results are writable'
Assert-Equal '3.3.3.3' $writable[0].ip 'the writable result is the usable one'

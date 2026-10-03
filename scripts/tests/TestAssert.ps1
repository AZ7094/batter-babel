# Minimal assertion helpers with a script-scoped tally. Keep ASCII-only.
$script:TestPassed = 0
$script:TestFailed = 0
$script:TestFailures = New-Object System.Collections.Generic.List[string]

function Reset-TestTally {
    $script:TestPassed = 0
    $script:TestFailed = 0
    $script:TestFailures = New-Object System.Collections.Generic.List[string]
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Name)
    if ($Expected -eq $Actual) {
        $script:TestPassed++
        Write-Output "ok - $Name"
    } else {
        $script:TestFailed++
        $script:TestFailures.Add("$Name : expected [$Expected], got [$Actual]")
        Write-Output "fail - $Name : expected [$Expected], got [$Actual]"
    }
}

function Assert-True {
    param([bool]$Condition, [string]$Name)
    if ($Condition) {
        $script:TestPassed++
        Write-Output "ok - $Name"
    } else {
        $script:TestFailed++
        $script:TestFailures.Add("$Name : expected true")
        Write-Output "fail - $Name : expected true"
    }
}

function Assert-Throws {
    param([scriptblock]$Script, [string]$Name)
    $threw = $false
    try { & $Script | Out-Null } catch { $threw = $true }
    if ($threw) {
        $script:TestPassed++
        Write-Output "ok - $Name"
    } else {
        $script:TestFailed++
        $script:TestFailures.Add("$Name : expected an exception")
        Write-Output "fail - $Name : expected an exception"
    }
}

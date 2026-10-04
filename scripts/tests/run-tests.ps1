# Entry point for the hand-rolled test runner. Keep ASCII-only.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tests\run-tests.ps1
$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent $PSScriptRoot

. (Join-Path $scriptsRoot 'ip-core.ps1')
. (Join-Path $scriptsRoot 'cf-probe.ps1')
. (Join-Path $scriptsRoot 'net-profiles.ps1')
. (Join-Path $PSScriptRoot 'TestAssert.ps1')
Reset-TestTally

Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.tests.ps1' | ForEach-Object {
    . $_.FullName
}

$total = $script:TestPassed + $script:TestFailed
Write-Output ""
Write-Output "passed: $($script:TestPassed)  failed: $($script:TestFailed)  total: $total"
if ($script:TestFailures.Count -gt 0) {
    Write-Output "--- failures ---"
    $script:TestFailures | ForEach-Object { Write-Output $_ }
}
if ($script:TestFailed -gt 0) { exit 1 } else { exit 0 }

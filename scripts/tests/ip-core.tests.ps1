# Regression tests for scripts/ip-core.ps1. Executed inline when dot-sourced.
# Keep ASCII-only.

# --- uint32 <-> dotted-decimal ---
Assert-Equal -Expected '0.0.0.0' -Actual (ConvertTo-IpString 0) -Name 'ConvertTo-IpString 0'
Assert-Equal -Expected '255.255.255.255' -Actual (ConvertTo-IpString 4294967295) -Name 'ConvertTo-IpString max u32'
Assert-Equal -Expected '173.245.48.1' -Actual (ConvertTo-IpString (ConvertFrom-IpString '173.245.48.1')) -Name 'round-trip 173.245.48.1'
Assert-Equal -Expected 0 -Actual (ConvertFrom-IpString '0.0.0.0') -Name 'ConvertFrom-IpString 0.0.0.0'
Assert-Equal -Expected 4294967295 -Actual (ConvertFrom-IpString '255.255.255.255') -Name 'ConvertFrom-IpString max u32'
# 173 * 16777216 overflows int32; the [long] casts keep it exact (documented pitfall).
Assert-Equal -Expected 2918526977 -Actual (ConvertFrom-IpString '173.245.48.1') -Name 'no int32 overflow on 173.x'

# --- CIDR membership ---
Assert-True -Condition (Test-IpInCidr '173.245.48.1' '173.245.48.0/20') -Name 'CIDR member'
Assert-True -Condition (-not (Test-IpInCidr '173.245.64.1' '173.245.48.0/20')) -Name 'CIDR non-member'
Assert-True -Condition (Test-IpInCloudflare '173.245.48.1') -Name 'known Cloudflare IP'
Assert-True -Condition (-not (Test-IpInCloudflare '192.0.2.1')) -Name 'non-Cloudflare IP'

# --- candidate pool invariants ---
$pool = @(Get-CfCandidatePool -Target 2000)
Assert-Equal -Expected 2000 -Actual $pool.Count -Name 'pool reaches target size'
Assert-Equal -Expected 2000 -Actual @($pool | Select-Object -Unique).Count -Name 'pool has no duplicates'
Assert-Equal -Expected 0 -Actual @($pool | Where-Object { -not (Test-IpInCloudflare $_) }).Count -Name 'every pool IP is in a Cloudflare range'

$small = @(Get-CfCandidatePool -Target 100)
Assert-Equal -Expected 100 -Actual $small.Count -Name 'small pool size'

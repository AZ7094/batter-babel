# Tests for scripts/net-profiles.ps1. Keep ASCII-only.
# Cases come from docs/superpowers/plans/2026-10-04-per-game-network-profiles.md (Task 1).

Assert-Equal 'latency' (Get-NetTier 'latency').Id 'tier latency id'
Assert-Equal 'throughput' (Get-NetTier 'throughput').Id 'tier throughput id'
Assert-Throws { Get-NetTier 'nope' } 'unknown tier throws'
Assert-Equal 'latency,throughput' ((Get-NetTierIds) -join ',') 'exactly two tiers'
Assert-Equal 'normal' (Get-NetTier 'latency').AutoTuning 'latency uses normal autotuning'
Assert-Equal 'normal' (Get-NetTier 'throughput').AutoTuning 'throughput uses normal autotuning'
Assert-Equal 'Restore' (Get-NetTier 'throughput').Nagle 'throughput restores Nagle'
Assert-Equal $true (Get-NetTier 'latency').Nagle 'latency disables Nagle'
Assert-Equal 'latency' (Get-NetRecipe 'cs2').Tier 'cs2 uses the latency tier'
Assert-Equal 'throughput' (Get-NetRecipe 'limbus').Tier 'limbus uses the throughput tier'
Assert-Equal 4 (Get-NetRecipe 'cs2').ExecutableParentDepth 'cs2 install root depth'
Assert-Equal 1 (Get-NetRecipe 'cs2').ConfigFiles.Count 'cs2 has one config file'
Assert-Equal 'game\csgo\cfg\autoexec.cfg' (Get-NetRecipe 'cs2').ConfigFiles[0].RelativePath 'cs2 config path'
Assert-Equal 4 (Get-NetRecipe 'cs2').ConfigFiles[0].Lines.Count 'cs2 managed block is four lines'
Assert-True ((Get-NetRecipe 'cs2').ConfigFiles[0].Lines -contains 'cl_allow_animated_avatars 0') 'cs2 keeps the verified non-network tweak'
Assert-Equal $true (Get-NetRecipe 'cs2').HasSurface 'cs2 reports a config surface'
Assert-Equal 'latency' (Get-NetRecipe 'steam-4001890').Tier 'unknown game falls back to latency'
Assert-Equal 0 (Get-NetRecipe 'steam-4001890').ConfigFiles.Count 'unknown game has no config file'
Assert-Equal $false (Get-NetRecipe 'steam-4001890').HasSurface 'unknown game reports no surface'

$same = Get-NetTierDelta 'latency' 'latency'
Assert-Equal 0 $same.Set.Count 'same tier sets nothing'
Assert-Equal 0 $same.Restore.Count 'same tier restores nothing'

$down = Get-NetTierDelta 'latency' 'throughput'
Assert-True ($down.Restore -contains 'Nagle') 'latency to throughput restores Nagle'
Assert-True (-not ($down.Set -contains 'Nagle')) 'latency to throughput does not set Nagle'
Assert-True ($down.Set -contains 'InterruptModeration') 'latency to throughput sets interrupt moderation'
Assert-True (-not ($down.Set -contains 'Eee')) 'unchanged keys stay out of the delta'

$up = Get-NetTierDelta 'throughput' 'latency'
Assert-True ($up.Set -contains 'Nagle') 'throughput to latency sets Nagle'
Assert-True (-not ($up.Restore -contains 'Nagle')) 'throughput to latency does not restore Nagle'

$fresh = Get-NetTierDelta $null 'latency'
Assert-True ($fresh.Set -contains 'Nagle') 'fresh apply sets Nagle'
Assert-True ($fresh.Set -contains 'Throttling') 'fresh apply sets every concrete key'
Assert-Equal 0 $fresh.Restore.Count 'fresh latency apply restores nothing'

$freshTp = Get-NetTierDelta $null 'throughput'
Assert-True ($freshTp.Restore -contains 'Nagle') 'fresh throughput apply restores Nagle'

Assert-Equal 'TcpAckFrequency,TCPNoDelay,TcpDelAckTicks' ((Get-NagleValueNames) -join ',') 'Nagle triad names'

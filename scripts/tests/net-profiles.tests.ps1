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

$lines = @('cl_allow_animated_avatars 0')
$merged = Merge-ManagedBlock "host_writeconfig`r`n" $lines
Assert-True (Test-HasManagedBlock $merged) 'merge inserts a complete block'
Assert-True ($merged -match 'host_writeconfig') 'merge keeps text outside the block'
Assert-Equal $merged (Merge-ManagedBlock $merged $lines) 'merge is idempotent'
Assert-Equal 1 ([regex]::Matches($merged, [regex]::Escape('// >>> Batter Babel >>>')).Count) 'merge leaves one start marker'

$replaced = Merge-ManagedBlock $merged @('cl_allow_animated_avatars 1')
Assert-True ($replaced -match 'cl_allow_animated_avatars 1') 'merge replaces the block body'
Assert-True (-not ($replaced -match 'cl_allow_animated_avatars 0')) 'merge drops the old block body'
Assert-Equal 1 ([regex]::Matches($replaced, [regex]::Escape('// >>> Batter Babel >>>')).Count) 'replace leaves one start marker'

$half = "// >>> Batter Babel >>>`r`ncl_allow_animated_avatars 0`r`n"
Assert-Equal $half (Merge-ManagedBlock $half $lines) 'incomplete markers are untouched on merge'
Assert-Equal $half (Remove-ManagedBlock $half) 'incomplete markers are untouched on remove'
Assert-Equal "host_writeconfig`r`n" (Remove-ManagedBlock $merged) 'remove strips the block and keeps the rest'
Assert-Equal "host_writeconfig`r`n" (Remove-ManagedBlock "host_writeconfig`r`n") 'remove leaves a file with no block unchanged'
Assert-Equal '' (Remove-ManagedBlock $null) 'remove tolerates null'
Assert-Equal $false (Test-HasManagedBlock '') 'empty text has no block'

$legacy = @('// Batter Babel network tuning (generated)','rate 196608','cl_interp 0.031','cl_interp_ratio 2','cl_net_buffer_ticks 64','net_graph 1','cl_allow_animated_avatars false') -join "`r`n"
Assert-True (Test-NetLegacyBlock $legacy) 'legacy output is recognised'
Assert-True (-not (Test-NetLegacyBlock "host_writeconfig`r`n")) 'foreign config is not legacy'
Assert-True (-not (Test-NetLegacyBlock '')) 'empty text is not legacy'
Assert-True (-not (Test-NetLegacyBlock ($legacy + "`r`nbind f1 noclip"))) 'a legacy file with a user line added is not legacy'

$cs2Exe = 'E:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe'
Assert-Equal 'E:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive' (Get-NetGameRoot $cs2Exe 4) 'depth 4 reaches the install root'
Assert-Equal $cs2Exe (Get-NetGameRoot $cs2Exe 0) 'depth 0 leaves the exe path alone'
Assert-Equal '' (Get-NetGameRoot '' 4) 'empty exe path yields empty root'
Assert-Equal $cs2Exe (Get-NetGameRoot $cs2Exe -1) 'negative depth behaves as zero'

$root = 'E:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive'
$cs2Plan = Get-NetTunePlan 'cs2' $null $root
Assert-Equal 'latency' $cs2Plan.Tier 'cs2 plan names its tier'
Assert-True ($cs2Plan.Set -contains 'Nagle') 'fresh cs2 plan sets Nagle'
Assert-Equal 1 $cs2Plan.ConfigWrites.Count 'cs2 plan writes one file'
Assert-Equal ([IO.Path]::Combine($root, 'game\csgo\cfg\autoexec.cfg')) $cs2Plan.ConfigWrites[0].Path 'cs2 plan resolves the config path'
Assert-True ($cs2Plan.ConfigWrites[0].Lines -contains 'cl_allow_animated_avatars 0') 'cs2 plan keeps the non-network tweak'
Assert-True (($cs2Plan.Notes -join ' ') -match 'not a network parameter') 'cs2 plan labels the non-network tweak'
Assert-Equal 0 (Get-NetTunePlan 'cs2' $null '').ConfigWrites.Count 'cs2 plan with no install root writes nothing'
Assert-True (((Get-NetTunePlan 'cs2' $null '').Notes -join ' ') -match 'install path') 'cs2 plan with no install root says why'

$limbusPlan = Get-NetTunePlan 'limbus' 'latency' ''
Assert-Equal 'throughput' $limbusPlan.Tier 'limbus plan names its tier'
Assert-Equal 0 $limbusPlan.ConfigWrites.Count 'limbus plan writes no file'
Assert-True (($limbusPlan.Notes -join ' ') -match 'no client-side network parameters') 'limbus plan reports no surface'

$switchPlan = Get-NetTunePlan 'limbus' 'latency' ''
Assert-True ($switchPlan.Restore -contains 'Nagle') 'switching to throughput plans a Nagle restore'
Assert-True (-not ($switchPlan.Set -contains 'Nagle')) 'switching to throughput does not plan a Nagle set'

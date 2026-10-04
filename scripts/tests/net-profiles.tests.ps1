# Tests for scripts/net-profiles.ps1. Keep ASCII-only.
# Cases come from docs/superpowers/plans/2026-10-04-per-game-network-profiles.md (Task 1).

Assert-Equal 'latency' (Get-NetTier 'latency').Id 'tier latency id'
Assert-Equal 'throughput' (Get-NetTier 'throughput').Id 'tier throughput id'
Assert-Throws { Get-NetTier 'nope' } 'unknown tier throws'
Assert-Equal 'latency,throughput' ((Get-NetTierIds) -join ',') 'exactly two tiers'
Assert-Equal 'normal' (Get-NetTier 'latency').AutoTuning 'latency uses normal autotuning'
Assert-Equal 'normal' (Get-NetTier 'throughput').AutoTuning 'throughput uses normal autotuning'
Assert-Equal 'Restore' (Get-NetTier 'throughput').Nagle 'throughput restores Nagle'
Assert-True ((Get-NetTier 'latency').Nagle -is [bool]) 'latency Nagle is a boolean, not the Restore sentinel'
Assert-True ((Get-NetTier 'latency').Nagle -eq $true) 'latency disables Nagle'
Assert-Equal 'latency' (Get-NetRecipe 'cs2').Tier 'cs2 uses the latency tier'
Assert-Equal 'throughput' (Get-NetRecipe 'limbus').Tier 'limbus uses the throughput tier'
Assert-Equal 4 (Get-NetRecipe 'cs2').ExecutableParentDepth 'cs2 install root depth'
Assert-Equal 1 (Get-NetRecipe 'cs2').ConfigFiles.Count 'cs2 has one config file'
Assert-Equal 'game\csgo\cfg\autoexec.cfg' (Get-NetRecipe 'cs2').ConfigFiles[0].RelativePath 'cs2 config path'
Assert-Equal 4 (Get-NetRecipe 'cs2').ConfigFiles[0].Lines.Count 'cs2 managed block is four lines'
Assert-True ((Get-NetRecipe 'cs2').ConfigFiles[0].Lines -contains 'cl_allow_animated_avatars 0') 'cs2 keeps the verified non-network tweak'
Assert-True ((Get-NetRecipe 'cs2').HasSurface -is [bool] -and (Get-NetRecipe 'cs2').HasSurface) 'cs2 reports a config surface'
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
Assert-True (-not ($down.Set -contains 'Eee')) 'unchanged keys stay out of the set list'
Assert-True (-not ($down.Restore -contains 'Eee')) 'unchanged keys stay out of the restore list'

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

# The end marker can also sit BEFORE the start marker: a hand-edited file, or one an earlier build
# damaged. Both transforms used to splice from IndexOf(end, start) == -1, which duplicated a
# mangled chunk of the user's file and grew a second, unbalanced pair of markers, so both of them
# must now hand such a file back exactly as it arrived.
$reversed = "bind f1 noclip`r`n// <<< Batter Babel <<<`r`nbind f2 noclip`r`n// >>> Batter Babel >>>`r`ncl_allow_animated_avatars 0`r`nbind f3 noclip"
Assert-True ((Remove-ManagedBlock $reversed) -ceq $reversed) 'reversed markers are untouched on remove'
Assert-True ((Merge-ManagedBlock $reversed $lines) -ceq $reversed) 'reversed markers are untouched on merge'
Assert-Equal 1 ([regex]::Matches((Merge-ManagedBlock $reversed $lines), [regex]::Escape('// >>> Batter Babel >>>')).Count) 'reversed markers do not grow a second block'
Assert-True (Test-HasManagedBlock $reversed) 'a reversed pair still reports both markers present'
Assert-True (Test-WellFormedManagedBlock $merged) 'a merged block is well formed'
Assert-True (-not (Test-WellFormedManagedBlock $reversed)) 'a reversed pair is not well formed'
Assert-True (-not (Test-WellFormedManagedBlock $half)) 'a lone start marker is not well formed'
Assert-True (-not (Test-WellFormedManagedBlock "host_writeconfig`r`n")) 'a file with no markers is not well formed'
Assert-True (-not (Test-WellFormedManagedBlock ($merged + $NetBlockStart))) 'a stray second start marker is not well formed'
Assert-True (-not (Test-WellFormedManagedBlock ($merged + $NetBlockEnd))) 'a stray second end marker is not well formed'
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

# --- the user's own config encoding is preserved (N6) ---
# A game's autoexec.cfg belongs to the user and may be UTF-8, UTF-16, or the system code page.
# Rewriting it as UTF-8 would silently replace every non-ASCII character outside the managed block,
# which is why these tests compare bytes rather than text: the text in memory looks right either way.
$cnText = [string]([char]0x4E2D) + [char]0x6587
$utf8Plain = (New-Object System.Text.UTF8Encoding($false)).GetBytes($cnText)

Assert-Equal 'utf-8' (Get-ConfigEncodingFromBytes $utf8Plain).Name 'plain UTF-8 is detected'
Assert-Equal 'utf-8-bom' (Get-ConfigEncodingFromBytes ([byte[]](@(0xEF, 0xBB, 0xBF) + $utf8Plain))).Name 'a UTF-8 BOM is detected'
Assert-Equal 'utf-16le' (Get-ConfigEncodingFromBytes ([byte[]](@(0xFF, 0xFE) + $utf8Plain))).Name 'a UTF-16LE BOM is detected'
Assert-Equal 'utf-16be' (Get-ConfigEncodingFromBytes ([byte[]](@(0xFE, 0xFF) + $utf8Plain))).Name 'a UTF-16BE BOM is detected'
Assert-Equal 'utf-8' (Get-ConfigEncodingFromBytes ([byte[]](0x61, 0x62, 0x63))).Name 'plain ASCII reads as UTF-8'

# 0x23 0x20 then 0xD6 0xD0 0xCE 0xC4, which are the GBK bytes for the two characters above and are
# not valid UTF-8. They are written literally because what matters is the byte sequence, not which
# code page the machine happens to answer with.
$ansiBytes = [byte[]](0x23, 0x20, 0xD6, 0xD0, 0xCE, 0xC4, 0x0D, 0x0A)
Assert-Equal 'ansi' (Get-ConfigEncodingFromBytes $ansiBytes).Name 'bytes that are not valid UTF-8 fall back to the system code page'

$encDir = Join-Path $env:TEMP ('bb-enc-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $encDir | Out-Null
try {
    $ansiPath = Join-Path $encDir 'ansi.cfg'
    [System.IO.File]::WriteAllBytes($ansiPath, $ansiBytes)
    $reader = Read-ManagedConfigFile $ansiPath
    Assert-Equal 'ansi' $reader.Name 'a non-UTF-8 config is recognised on read'
    Assert-Equal ([System.Text.Encoding]::Default.GetString($ansiBytes)) $reader.Text 'a non-UTF-8 config decodes with the system code page'

    $merged = Merge-ManagedBlock $reader.Text @('// managed line')
    Write-ManagedConfigFile $ansiPath $reader $merged
    $expected = [System.Text.Encoding]::Default.GetBytes($merged)
    $actual = [System.IO.File]::ReadAllBytes($ansiPath)
    Assert-Equal $expected.Length $actual.Length 'the rewritten non-UTF-8 config keeps its byte length'
    $identical = $true
    for ($i = 0; $i -lt $actual.Length; $i++) { if ($actual[$i] -ne $expected[$i]) { $identical = $false; break } }
    Assert-True $identical 'the rewritten non-UTF-8 config is byte-identical to the system code page encoding'
    Assert-True (Test-HasManagedBlock (Read-ManagedConfigFile $ansiPath).Text) 'the managed block survives the round trip'

    $bomPath = Join-Path $encDir 'bom.cfg'
    [System.IO.File]::WriteAllBytes($bomPath, [byte[]](@(0xEF, 0xBB, 0xBF) + (New-Object System.Text.UTF8Encoding($false)).GetBytes("$cnText`r`n")))
    $bomReader = Read-ManagedConfigFile $bomPath
    Assert-Equal 'utf-8-bom' $bomReader.Name 'a UTF-8 BOM is detected on read'
    Assert-Equal "$cnText`r`n" $bomReader.Text 'the BOM is not part of the text'
    Write-ManagedConfigFile $bomPath $bomReader "$($bomReader.Text)x`r`n"
    $bomAfter = [System.IO.File]::ReadAllBytes($bomPath)
    Assert-True ($bomAfter[0] -eq 0xEF -and $bomAfter[1] -eq 0xBB -and $bomAfter[2] -eq 0xBF) 'the UTF-8 BOM is written back'
    Assert-Equal "$cnText`r`nx`r`n" (Read-ManagedConfigFile $bomPath).Text 'the BOM file round trips through UTF-8'
} finally {
    Remove-Item -LiteralPath $encDir -Recurse -Force -ErrorAction SilentlyContinue
}

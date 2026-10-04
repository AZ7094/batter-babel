# Tests for scripts/game-paths.ps1. Keep ASCII-only.
# The heuristic here decides which process a QoS rule is built for, so the cases that matter are the
# ones where a wrong answer would tag a redistributable, a crash handler, or an unrelated binary.

function New-ExeCandidate([string]$Name, [int64]$Size, [string]$Path) {
    return @{ Name = $Name; Size = $Size; Path = $Path }
}

# --- normalisation ---
Assert-Equal 'scpsecretlaboratory' (Get-ExeNameKey 'SCP: Secret Laboratory') 'game name normalises'
Assert-Equal 'scpsl' (Get-ExeNameKey 'SCPSL.exe') 'executable name normalises'
Assert-Equal 'limbuscompany' (Get-ExeNameKey 'Limbus Company') 'spaces are dropped'
Assert-Equal '' (Get-ExeNameKey '') 'an empty name normalises to empty'

# --- blocklist: things that are never the game ---
Assert-True (Test-GameExeBlocked 'UnityCrashHandler64.exe') 'a Unity crash handler is blocked'
Assert-True (Test-GameExeBlocked 'unins000.exe') 'an uninstaller is blocked'
Assert-True (Test-GameExeBlocked 'vcredist_x64.exe') 'a redistributable is blocked'
Assert-True (Test-GameExeBlocked 'EasyAntiCheat.exe') 'the anti-cheat launcher is blocked'
Assert-True (Test-GameExeBlocked '') 'an empty name is not usable'
Assert-True (-not (Test-GameExeBlocked 'SCPSL.exe')) 'a game binary is not blocked'

# --- name signal ---
Assert-Equal 3 (Get-GameExeNameScore 'Terraria.exe' 'Terraria') 'an exact match scores highest'
Assert-Equal 2 (Get-GameExeNameScore 'TerrariaServer.exe' 'Terraria') 'a prefix match scores second'
Assert-Equal 1 (Get-GameExeNameScore 'MyGameServerThing.exe' 'GameServer') 'a containment match scores third'
Assert-Equal 0 (Get-GameExeNameScore 'cs2.exe' "Don't Starve Together") 'an unrelated name scores nothing'
Assert-Equal 0 (Get-GameExeNameScore 'abc.exe' 'Terraria') 'a stem shorter than four characters never matches'

# --- selection ---
$nameBeatsSize = Select-GameExecutable @(
    (New-ExeCandidate 'BigTool.exe' 900000000 'tool')
    (New-ExeCandidate 'Terraria.exe' 1000 'game')
) 'Terraria'
Assert-Equal 'game' $nameBeatsSize 'a name match beats a much larger unrelated binary'

$sizeFallback = Select-GameExecutable @(
    (New-ExeCandidate 'Alpha.exe' 10 'small')
    (New-ExeCandidate 'Beta.exe' 99 'large')
) 'NothingLikeEitherOfThese'
Assert-Equal 'large' $sizeFallback 'with no name signal the largest binary wins'

$blocked = Select-GameExecutable @(
    (New-ExeCandidate 'UnityCrashHandler64.exe' 99999999 'crash')
) 'SCP: Secret Laboratory'
Assert-Equal $null $blocked 'a folder holding only a crash handler selects nothing'

$realScp = Select-GameExecutable @(
    (New-ExeCandidate 'UnityCrashHandler64.exe' 1529016 'crash')
    (New-ExeCandidate 'SCPSL.exe' 15404896 'real')
) 'SCP: Secret Laboratory'
Assert-Equal 'real' $realScp 'the real SCP:SL folder resolves to SCPSL.exe'

Assert-Equal $null (Select-GameExecutable @() 'Terraria') 'no candidates selects nothing'
Assert-Equal $null (Select-GameExecutable $null 'Terraria') 'a null candidate list selects nothing'
Assert-Equal $null (Select-GameExecutable @((New-ExeCandidate '' 5 'nameless')) 'Terraria') 'a nameless candidate is skipped'

$tie = Select-GameExecutable @(
    (New-ExeCandidate 'Zeta.exe' 50 'zzz')
    (New-ExeCandidate 'Alpha.exe' 50 'aaa')
) 'Unrelated'
Assert-Equal 'aaa' $tie 'an exact tie falls back to the name order so the result is stable'

$scoreBeatsSizeInTie = Select-GameExecutable @(
    (New-ExeCandidate 'Other.exe' 500 'big')
    (New-ExeCandidate 'Terraria.exe' 10 'named')
) 'Terraria'
Assert-Equal 'named' $scoreBeatsSizeInTie 'score is compared before size'

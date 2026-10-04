# Game executable discovery, kept in its own dot-sourceable module because the decision it makes
# cannot be tested inside optimization.ps1: that script has a mandatory parameter block and
# dispatches on it, so it can never be dot-sourced by the test runner.
#
# Keep this file pure ASCII: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI.

# Names that are never the game itself. A QoS rule matches one executable path, so pointing it at a
# redistributable or a crash handler would tag the wrong process and quietly do nothing for the game.
# Matching is case-insensitive, because -like is.
$GameExeBlocklist = @(
    'unins*',
    'unins000*',
    'UnityCrashHandler*',
    '*CrashHandler*',
    '*CrashReport*',
    '*CrashPad*',
    'vcredist*',
    'vc_redist*',
    'dxsetup*',
    'dxwebsetup*',
    'DirectX*',
    'oalinst*',
    'UE4PrereqSetup*',
    'UEPrereqSetup*',
    'EasyAntiCheat*',
    'BEService*',
    'BattlEye*',
    'steamerrorreporter*',
    'steamservice*',
    'dotnet*',
    'setup*',
    'install*'
)

# Lower-case, extension-free, alphanumeric-only form, so "SCP: Secret Laboratory" can be compared
# with "SCPSL.exe" at all.
function Get-ExeNameKey {
    param([string]$Name)
    if (-not $Name) { return '' }
    $stem = $Name
    # Deliberately not [IO.Path]::GetFileNameWithoutExtension: a game name such as
    # "SCP: Secret Laboratory" contains a colon, which Path reads as a drive separator, so it would
    # silently cut the name down to "Secret Laboratory" and lose the SCP prefix entirely.
    $slash = $stem.LastIndexOfAny([char[]]@('\', '/'))
    if ($slash -ge 0) { $stem = $stem.Substring($slash + 1) }
    $dot = $stem.LastIndexOf('.')
    if ($dot -gt 0 -and ($stem.Length - $dot) -le 5) { $stem = $stem.Substring(0, $dot) }
    return ($stem.ToLowerInvariant() -replace '[^a-z0-9]', '')
}

function Test-GameExeBlocked {
    param([string]$Name)
    if (-not $Name) { return $true }
    foreach ($pattern in $GameExeBlocklist) {
        if ($Name -like $pattern) { return $true }
    }
    return $false
}

# How well an executable name matches a game name. 3 is an exact match, 2 a prefix either way, 1 a
# containment either way, and 0 means no name signal at all -- which is what sends the caller to the
# size fallback. Stems shorter than four characters are never matched on a prefix or a containment:
# "cs2" and "abc" turn up inside unrelated names far too easily.
function Get-GameExeNameScore {
    param([string]$ExeName, [string]$GameName)
    $exe = Get-ExeNameKey $ExeName
    $game = Get-ExeNameKey $GameName
    if (-not $exe -or -not $game) { return 0 }
    if ($exe -eq $game) { return 3 }
    if ($exe.Length -ge 4 -and ($game.StartsWith($exe) -or $exe.StartsWith($game))) { return 2 }
    if ($exe.Length -ge 4 -and ($game.Contains($exe) -or $exe.Contains($game))) { return 1 }
    return 0
}

# Pick the executable a QoS rule should match. Candidates are hashtables with Name / Path / Size; the
# caller does the file system work so this stays pure and testable.
#
# A name match wins over a larger unrelated binary. When nothing carries a name signal the largest
# candidate wins, because a game's own binary is normally the biggest file in its folder once the
# blocklist has removed the helpers. Returning $null means "do not boost this game", which is better
# than tagging an arbitrary process.
function Select-GameExecutable {
    param($Candidates, [string]$GameName)
    $usable = @()
    foreach ($candidate in @($Candidates)) {
        if ($null -eq $candidate) { continue }
        $name = "$($candidate.Name)"
        if (-not $name) { continue }
        if (Test-GameExeBlocked $name) { continue }
        $size = 0
        if ($null -ne $candidate.Size) { $size = [int64]$candidate.Size }
        $usable += [ordered]@{
            Name = $name
            Path = "$($candidate.Path)"
            Size = $size
            Score = (Get-GameExeNameScore $name $GameName)
        }
    }
    if ($usable.Count -eq 0) { return $null }
    # Name is the final key so a genuine tie still resolves the same way on every run.
    $ranked = @($usable | Sort-Object -Property `
        @{ Expression = { $_.Score }; Descending = $true }, `
        @{ Expression = { $_.Size }; Descending = $true }, `
        @{ Expression = { $_.Name }; Descending = $false })
    return $ranked[0].Path
}

# Per-game network tuning profiles: tier data, recipe data, and pure lookups.
# Design: docs/superpowers/specs/2026-10-04-per-game-network-profiles-design.md
# Plan:   docs/superpowers/plans/2026-10-04-per-game-network-profiles.md
# Keep ASCII-only: PowerShell 5.1 reads a BOM-less .ps1 as ANSI, and non-ASCII
# then fails to parse.

# Tier keys are logical, not registry names: the executor maps each one onto the
# concrete registry / netsh / adapter call. The sentinel 'Restore' means "put
# this key back to the value recorded in tune-state.json" rather than "write a
# value". Only two keys actually differ between the tiers; the rest are shared
# so the delta logic can be uniform.
$NetProfileTiers = @{
    latency = [ordered]@{
        Id = 'latency'
        Label = 'Low latency (realtime)'
        Nagle = $true
        InterruptModeration = 0
        Eee = 0
        FlowControl = 0
        AutoTuning = 'normal'
        Rss = 'enabled'
        Throttling = 'off'
        AdapterPower = 'disabled'
        WifiPower = 'max'
    }
    throughput = [ordered]@{
        Id = 'throughput'
        Label = 'Throughput (downloads)'
        Nagle = 'Restore'
        InterruptModeration = 1
        Eee = 0
        FlowControl = 0
        AutoTuning = 'normal'
        Rss = 'enabled'
        Throttling = 'off'
        AdapterPower = 'disabled'
        WifiPower = 'max'
    }
}

# The managed block written into a game config. Every line here was checked
# against CS2's own command set: the five network cvars this app used to write
# were either CS:GO-only, removed, or undocumented, and rate merely capped
# bandwidth at a quarter of the default. cl_allow_animated_avatars is a real
# CS2 command but it is a rendering toggle, so it is labelled as one.
$NetCs2ConfigLines = @(
    '// Batter Babel (generated) - CS2 has no client-side network cvar worth setting;',
    '// see docs/superpowers/specs/2026-10-04-per-game-network-profiles-design.md',
    '// The line below is a rendering tweak, not a network parameter.',
    'cl_allow_animated_avatars 0'
)

# Per-game recipes. A game with no client-side config surface still gets a tier,
# because the system parameters are the part that genuinely applies to it.
$NetGameRecipes = @{
    cs2 = [ordered]@{
        GameId = 'cs2'
        Tier = 'latency'
        ExecutableParentDepth = 4
        ConfigFiles = @(
            [ordered]@{
                RelativePath = 'game\csgo\cfg\autoexec.cfg'
                Lines = $NetCs2ConfigLines
            }
        )
        NonNetworkNotes = @(
            'cl_allow_animated_avatars 0 is a rendering tweak, not a network parameter.'
        )
        HasSurface = $true
    }
    limbus = [ordered]@{
        GameId = 'limbus'
        Tier = 'throughput'
        ExecutableParentDepth = 0
        ConfigFiles = @()
        NonNetworkNotes = @()
        HasSurface = $false
    }
    bd2 = [ordered]@{
        GameId = 'bd2'
        Tier = 'throughput'
        ExecutableParentDepth = 0
        ConfigFiles = @()
        NonNetworkNotes = @()
        HasSurface = $false
    }
    apex = [ordered]@{
        GameId = 'apex'
        Tier = 'latency'
        ExecutableParentDepth = 0
        ConfigFiles = @()
        NonNetworkNotes = @()
        HasSurface = $false
    }
    hunt = [ordered]@{
        GameId = 'hunt'
        Tier = 'latency'
        ExecutableParentDepth = 0
        ConfigFiles = @()
        NonNetworkNotes = @()
        HasSurface = $false
    }
    dst = [ordered]@{
        GameId = 'dst'
        Tier = 'latency'
        ExecutableParentDepth = 0
        ConfigFiles = @()
        NonNetworkNotes = @()
        HasSurface = $false
    }
    terraria = [ordered]@{
        GameId = 'terraria'
        Tier = 'latency'
        ExecutableParentDepth = 0
        ConfigFiles = @()
        NonNetworkNotes = @()
        HasSurface = $false
    }
}

function Get-NetTierIds {
    # A literal list, not $NetProfileTiers.Keys: a plain hashtable does not
    # guarantee key order, and callers rely on this order being stable.
    return @('latency', 'throughput')
}

function Get-NetTier {
    param([string]$TierId)
    if (-not $TierId -or -not $NetProfileTiers.ContainsKey($TierId)) {
        throw "Unknown tier: $TierId"
    }
    return $NetProfileTiers[$TierId]
}

function Get-NetRecipe {
    param([string]$GameId)
    if ($GameId -and $NetGameRecipes.ContainsKey($GameId)) {
        return $NetGameRecipes[$GameId]
    }
    # Unknown or dynamic ids (steam-<appid>) get the conservative realtime tier
    # and no config files, so the run reports "no client-side surface" honestly
    # instead of inventing one.
    return [ordered]@{
        GameId = $GameId
        Tier = 'latency'
        ExecutableParentDepth = 0
        ConfigFiles = @()
        NonNetworkNotes = @()
        HasSurface = $false
    }
}

function Get-NagleValueNames {
    # The registry value names the logical 'Nagle' key stands for. Kept here so
    # the executor and the tests cannot drift apart on the triad.
    return @('TcpAckFrequency', 'TCPNoDelay', 'TcpDelAckTicks')
}

function Get-NetTierDelta {
    param([string]$FromTierId, [string]$ToTierId)
    $set = @()
    $restore = @()
    $target = Get-NetTier $ToTierId

    # Re-applying the tier that is already active is a deliberate no-op, so a
    # repeated run never rewrites the registry.
    if ($FromTierId -and $FromTierId -eq $ToTierId) {
        return @{ Set = $set; Restore = $restore }
    }

    $source = $null
    if ($FromTierId -and $NetProfileTiers.ContainsKey($FromTierId)) {
        $source = $NetProfileTiers[$FromTierId]
    }

    foreach ($key in $target.Keys) {
        if ($key -eq 'Id' -or $key -eq 'Label') { continue }
        $value = $target[$key]

        # The [string] test is load-bearing: PowerShell coerces the right-hand
        # operand to the left operand's type, so `$true -eq 'Restore'` is TRUE
        # (a non-empty string is a truthy boolean). Without the type check the
        # latency tier's Nagle would be classified as a restore, the exact
        # opposite of what the spec requires.
        if ($value -is [string] -and $value -eq 'Restore') {
            $restore += $key
            continue
        }
        if ($null -ne $source -and $source.Contains($key) -and $source[$key] -eq $value) {
            continue
        }
        $set += $key
    }

    return @{ Set = $set; Restore = $restore }
}

# Managed block markers. Every game config uses this one marker pair, and text
# outside the pair is never touched.
$NetBlockStart = '// >>> Batter Babel >>>'
$NetBlockEnd = '// <<< Batter Babel <<<'

# Exactly what 0.10.1 and earlier wrote into autoexec.cfg. Recognised only so an
# old-format file can be migrated; a user's own config must not match this.
$NetLegacyLines = @(
    '// Batter Babel network tuning (generated)',
    'rate 196608',
    'cl_interp 0.031',
    'cl_interp_ratio 2',
    'cl_net_buffer_ticks 64',
    'net_graph 1',
    'cl_allow_animated_avatars false'
)

function Test-HasManagedBlock {
    param([string]$Text)
    if (-not $Text) { return $false }
    return ($Text.Contains($NetBlockStart) -and $Text.Contains($NetBlockEnd))
}

function Merge-ManagedBlock {
    param([string]$Text, [string[]]$Lines)
    $original = $Text
    if ($null -eq $original) { $original = '' }
    $text = ($original -replace "`r`n", "`n") -replace "`n", "`r`n"

    $hasStart = $text.Contains($NetBlockStart)
    $hasEnd = $text.Contains($NetBlockEnd)
    if ($hasStart -ne $hasEnd) {
        # A half-written block means the file is in a state we do not
        # understand. Return it exactly as it arrived instead of guessing where
        # the block was going to end and eating whatever follows.
        return $original
    }

    $body = @()
    if ($null -ne $Lines) { $body = @($Lines) }
    $all = @($NetBlockStart) + $body + @($NetBlockEnd)
    $block = ($all -join "`r`n") + "`r`n"

    if (-not $hasStart) {
        if ($text.Length -gt 0 -and -not $text.EndsWith("`r`n")) { $text += "`r`n" }
        return $text + $block
    }

    # Index splice, not a greedy regex: take the first start marker and the
    # first end marker after it, so a second block later in the file survives.
    $start = $text.IndexOf($NetBlockStart)
    $end = $text.IndexOf($NetBlockEnd, $start)
    $after = $end + $NetBlockEnd.Length
    if ($after + 2 -le $text.Length -and $text.Substring($after, 2) -eq "`r`n") { $after += 2 }
    return $text.Substring(0, $start) + $block + $text.Substring($after)
}

function Remove-ManagedBlock {
    param([string]$Text)
    $original = $Text
    if ($null -eq $original) { $original = '' }
    $text = ($original -replace "`r`n", "`n") -replace "`n", "`r`n"

    if (-not $text.Contains($NetBlockStart) -or -not $text.Contains($NetBlockEnd)) {
        return $original
    }

    $start = $text.IndexOf($NetBlockStart)
    $end = $text.IndexOf($NetBlockEnd, $start)
    $after = $end + $NetBlockEnd.Length
    if ($after + 2 -le $text.Length -and $text.Substring($after, 2) -eq "`r`n") { $after += 2 }
    return $text.Substring(0, $start) + $text.Substring($after)
}

function Test-NetLegacyBlock {
    param([string]$Text)
    if (-not $Text) { return $false }
    $seen = @()
    foreach ($line in ($Text -split "`r?`n")) {
        $trimmed = $line.Trim()
        if ($trimmed) { $seen += $trimmed }
    }
    if ($seen.Count -ne $NetLegacyLines.Count) { return $false }
    foreach ($want in $NetLegacyLines) {
        $found = $false
        foreach ($got in $seen) {
            if ($got -eq $want) { $found = $true; break }
        }
        if (-not $found) { return $false }
    }
    return $true
}

function Get-NetGameRoot {
    param([string]$ExecutablePath, [int]$ParentDepth)
    if (-not $ExecutablePath) { return '' }
    $depth = $ParentDepth
    if ($depth -lt 0) { $depth = 0 }
    $path = $ExecutablePath
    for ($i = 0; $i -lt $depth; $i++) {
        $parent = Split-Path -Parent $path
        if (-not $parent) { return $path }
        $path = $parent
    }
    return $path
}

function Get-NetTunePlan {
    param([string]$GameId, [string]$FromTierId, [string]$InstallRoot)
    # Composes the decision and owns no I/O: the install root arrives as an
    # argument so this stays testable without a filesystem or a registry.
    $recipe = Get-NetRecipe $GameId
    $delta = Get-NetTierDelta $FromTierId $recipe.Tier

    $notes = @()
    if ($null -ne $recipe.NonNetworkNotes) { $notes += @($recipe.NonNetworkNotes) }

    # A plain array with +=, not a generic List: assigning a List[object] into an
    # [ordered]@{...} literal throws "Argument types do not match" at
    # construction time. Array addition does store the hashtable as a single
    # element, which is what is wanted here.
    $writes = @()
    $files = @($recipe.ConfigFiles)
    if ($files.Count -gt 0) {
        if (-not $InstallRoot) {
            $notes += "The game install path is not known, so no config file was written."
        } else {
            foreach ($file in $files) {
                $writes += [ordered]@{
                    Path = [IO.Path]::Combine($InstallRoot, $file.RelativePath)
                    Lines = @($file.Lines)
                }
            }
        }
    }

    if (-not $recipe.HasSurface) {
        $notes += "This game exposes no client-side network parameters; only the system tier applies."
    }

    return [ordered]@{
        Tier = $recipe.Tier
        Set = @($delta.Set)
        Restore = @($delta.Restore)
        ConfigWrites = @($writes)
        Notes = @($notes)
    }
}

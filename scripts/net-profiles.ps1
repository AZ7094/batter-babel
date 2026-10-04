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

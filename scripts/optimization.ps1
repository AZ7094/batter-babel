param(
    [Parameter(Mandatory = $true)]
    # Deliberately NO [ValidateSet] here. A hardcoded action list has to be kept in sync by hand, and
    # forgetting to add a new action made it fail with a parameter-binding error before the script
    # even ran -- which is exactly how the logon re-apply task silently broke. Unknown actions are
    # rejected by the runtime check in the dispatch section instead.
    [string]$Action,
    [string]$GameId,
    [string]$Target,
    [string]$Domains,
    [string]$ProgressFile,
    [string]$ResultFile
)

$ErrorActionPreference = 'Stop'

# Every action this script understands. Anything else is refused up front with a readable message
# (instead of silently doing nothing and leaving the caller without a result file).
$KnownActions = @(
    'scan', 'scan-force', 'status', 'probe',
    'cf-optimize', 'cf-apply', 'cf-restore',
    'optimize', 'restore', 'reapply-boost',
    'tune-system', 'restore-tune',
    'boost', 'apply-route'
)

if ($KnownActions -notcontains $Action) {
    # Inline (not Write-Result) because that helper is defined further down; PowerShell needs a
    # function to exist before it can be called.
    $errJson = @{ ok = $false; message = "Unknown action '$Action'." } | ConvertTo-Json -Depth 4 -Compress
    if ($ResultFile) {
        try { [System.IO.File]::WriteAllText($ResultFile, $errJson, [System.Text.UTF8Encoding]::new($false)) } catch {}
    } else {
        Write-Output $errJson
    }
    exit 0
}

# Reports coarse-grained progress back to the UI. The Rust side polls this file and raises a
# Tauri event, so the progress bar tracks real work instead of guessing.
function Write-Tick([int]$Percent, [string]$Message) {
    if (-not $ProgressFile) { return }
    try {
        [System.IO.File]::WriteAllText($ProgressFile, "$Percent|$Message", [System.Text.UTF8Encoding]::new($false))
    } catch {}
}

# Each game carries its own optimisation targets.
#   Mode 'cloudflare' = rank curated official Cloudflare IPs by download speed
#   Mode 'probe'      = resolve candidate IPs via system DNS + public DoH, then probe each per domain
$Catalog = [ordered]@{
    limbus = @{
        # The only title here whose in-game experience actually runs over ranked endpoints: content
        # download goes through Cloudflare, the game API through CloudFront. So optimisation helps
        # the game itself, not just a platform.
        Id = 'limbus'; Name = 'Limbus Company'; AppId = '1973530'; Executable = 'LimbusCompany.exe'; RelativePath = 'LimbusCompany.exe'; Vendor = 'Steam'; HelpsGameplay = $true
        OptGroups = @(
            @{ Label = 'Cloudflare (download CDN)'; Tag = 'CF';     Mode = 'cloudflare'; Domains = @('download.limbuscompanycdn.org', 'downloadcommon.limbuscompanycdn.org', 'downloadfmod.limbuscompanycdn.org') }
            @{ Label = 'CloudFront (API)';          Tag = 'AMAZON'; Mode = 'probe';      Domains = @('www.limbuscompanyapi.com', 'notice.limbuscompanyapi.com') }
        )
    }
    cs2 = @{
        # In-match traffic is UDP via Steam Datagram Relay to dynamically assigned servers: no
        # domain is involved, so hosts ranking cannot touch it. Only the platform can be ranked.
        Id = 'cs2'; Name = 'Counter-Strike 2'; AppId = '730'; Executable = 'cs2.exe'; RelativePath = 'game\bin\win64\cs2.exe'; Vendor = 'Steam'
        OptGroups = @(
            @{ Label = 'Steam API (login / startup)'; Tag = 'STEAM'; Mode = 'probe'; Domains = @('api.steampowered.com', 'login.steampowered.com') }
            @{ Label = 'Steam CDN'; Tag = 'STEAM'; Mode = 'probe'; Domains = @('steamcdn-a.akamaihd.net') }
        )
    }
    apex = @{
        # Match servers are assigned dynamically -> platform acceleration only.
        Id = 'apex'; Name = 'Apex Legends'; AppId = '1172470'; Executable = 'r5apex.exe'; RelativePath = 'r5apex.exe'; Vendor = 'Steam / EA app'
        OptGroups = @(
            @{ Label = 'EA Account / sign-in'; Tag = 'EA'; Mode = 'probe'; Domains = @('accounts.ea.com', 'signin.ea.com') }
            @{ Label = 'EA CDN'; Tag = 'EA'; Mode = 'probe'; Domains = @('origin-a.akamaihd.net') }
        )
    }
    hunt = @{
        # Match traffic is UDP to dynamically assigned Crytek servers; Crytek's own backend is a
        # single address, so only Steam's platform endpoints can be ranked.
        Id = 'hunt'; Name = 'Hunt: Showdown 1896'; AppId = '594650'; Executable = 'HuntGame.exe'; RelativePath = 'bin\win_x64\HuntGame.exe'; Vendor = 'Steam'
        OptGroups = @(
            @{ Label = 'Steam API (login / startup)'; Tag = 'STEAM'; Mode = 'probe'; Domains = @('api.steampowered.com', 'login.steampowered.com') }
            @{ Label = 'Steam CDN'; Tag = 'STEAM'; Mode = 'probe'; Domains = @('steamcdn-a.akamaihd.net') }
        )
    }
    dst = @{
        # Co-op survival. Klei's lobby/account services are multi-IP and CAN be ranked (finding and
        # joining sessions), but the session itself is P2P between players -> no domain involved.
        Id = 'dst'; Name = "Don't Starve Together"; AppId = '322330'; Executable = 'dontstarve_steam_x64.exe'; RelativePath = 'bin64\dontstarve_steam_x64.exe'; Vendor = 'Steam'
        OptGroups = @(
            @{ Label = 'Klei lobby / account'; Tag = 'KLEI'; Mode = 'probe'; Domains = @('lobby.klei.com', 'accounts.klei.com', 'login.klei.com') }
            @{ Label = 'Steam API (login / startup)'; Tag = 'STEAM'; Mode = 'probe'; Domains = @('api.steampowered.com', 'login.steampowered.com') }
        )
    }
    terraria = @{
        # Co-op sandbox. Multiplayer is P2P (the host's own connection), so only Steam's platform
        # services and the Cloudflare-fronted official site can be ranked.
        Id = 'terraria'; Name = 'Terraria'; AppId = '105600'; Executable = 'Terraria.exe'; RelativePath = 'Terraria.exe'; Vendor = 'Steam'
        OptGroups = @(
            @{ Label = 'Steam API (login / startup)'; Tag = 'STEAM'; Mode = 'probe'; Domains = @('api.steampowered.com', 'login.steampowered.com') }
            @{ Label = 'Official site / services'; Tag = 'CF'; Mode = 'probe'; Domains = @('terraria.org', 're-logic.com') }
        )
    }
    bd2 = @{
        # Standalone / mobile title (Neowiz, distributed via the STOVE platform and its own site).
        # Its whole content delivery is CloudFront-fronted across several multi-IP domains, so
        # ranking them genuinely speeds up the in-game asset downloads -- exactly what hosts CAN do.
        # Standalone = no Steam app id, no install detection: optimisation only needs the domains.
        Id = 'bd2'; Name = 'Brown Dust 2'; AppId = ''; Executable = 'BrownDust2.exe'; RelativePath = ''; Vendor = 'Neowiz / STOVE'; Standalone = $true; HelpsGameplay = $true
        OptGroups = @(
            @{ Label = 'Game CDN (assets / patches)'; Tag = 'AMAZON'; Mode = 'probe'; Domains = @('browndust2.com', 'browndust2.jp', 'cdn.neowiz.com') }
            @{ Label = 'STOVE platform'; Tag = 'AMAZON'; Mode = 'probe'; Domains = @('onstove.com') }
        )
    }
}

# Well-known ONLINE games (Steam AppID -> display name). Installed titles found here are listed in
# the picker even without a dedicated optimisation profile. This catalogue is the filter that keeps
# single-player games and tools out: Steam stores no online/single-player flag locally (appinfo.vdf
# only carries key names) and store.steampowered.com is unreachable on many networks, so there is no
# way to detect it automatically. Only add titles that genuinely have an online component.
$OnlineGameIds = @{
    # --- Shooters / competitive ---
    '730'     = 'Counter-Strike 2'
    '1172470' = 'Apex Legends'
    '578080'  = 'PUBG: BATTLEGROUNDS'
    '359550'  = 'Rainbow Six Siege'
    '1938090' = 'Call of Duty'
    '1237970' = 'Titanfall 2'
    '700330'  = 'SCP: Secret Laboratory'
    '553850'  = 'HELLDIVERS 2'
    '1966720' = 'Lethal Company'
    '581320'  = 'Insurgency: Sandstorm'
    '393380'  = 'Squad'
    '686810'  = 'Hell Let Loose'
    '1144200' = 'Ready or Not'
    '739630'  = 'Phasmophobia'
    '218620'  = 'PAYDAY 2'
    '632360'  = 'Risk of Rain 2'
    '1203220' = 'NARAKA: BLADEPOINT'
    '236390'  = 'War Thunder'
    '107410'  = 'Arma 3'
    '271590'  = 'Grand Theft Auto V'
    '1174180' = 'Red Dead Redemption 2'
    '1063730' = 'New World'
    # --- MOBA / strategy / fighting ---
    '570'     = 'Dota 2'
    '291550'  = 'Brawlhalla'
    '386360'  = 'SMITE'
    '813780'  = 'Age of Empires II: DE'
    '1466860' = 'Age of Empires IV'
    '289070'  = "Sid Meier's Civilization VI"
    '281990'  = 'Stellaris'
    '236850'  = 'Europa Universalis IV'
    '394360'  = 'Hearts of Iron IV'
    '1158310' = 'Crusader Kings III'
    # --- Co-op / survival / sandbox ---
    '550'     = 'Left 4 Dead 2'
    '4000'    = "Garry's Mod"
    '945360'  = 'Among Us'
    '1097150' = 'Fall Guys'
    '221100'  = 'DayZ'
    '252490'  = 'Rust'
    '346110'  = 'ARK: Survival Evolved'
    '892970'  = 'Valheim'
    '251570'  = '7 Days to Die'
    '322330'  = "Don't Starve Together"
    '105600'  = 'Terraria'
    '413150'  = 'Stardew Valley'
    '440900'  = 'Conan Exiles'
    '526870'  = 'Satisfactory'
    '427520'  = 'Factorio'
    '275850'  = "No Man's Sky"
    '359320'  = 'Elite Dangerous'
    '594650'  = 'Hunt: Showdown 1896'
    '244210'  = 'Assetto Corsa'
    '805550'  = 'Assetto Corsa Competizione'
    '1551360' = 'Forza Horizon 5'
    # --- Online RPG / MMO / card ---
    '230410'  = 'Warframe'
    '1085660' = 'Destiny 2'
    '1599340' = 'Lost Ark'
    '238960'  = 'Path of Exile'
    '1245620' = 'ELDEN RING'
    '582010'  = 'Monster Hunter: World'
    '1449850' = 'Yu-Gi-Oh! Master Duel'
    '306130'  = 'The Elder Scrolls Online'
    '1973530' = 'Limbus Company'
    # --- Additional online titles (EA / Ubisoft / Epic / Battle.net shooters & more) ---
    '1517290' = 'Battlefield 2042'
    '1238840' = 'Battlefield V'
    '1238820' = 'Battlefield 1'
    '1238860' = 'Battlefield 4'
    '1238880' = 'Battlefield Hardline'
    '440'     = 'Team Fortress 2'
    '2073850' = 'The Finals'
    '2507950' = 'Delta Force'
    '2767030' = 'Marvel Rivals'
    '2357570' = 'Overwatch 2'
    '782330'  = 'DOOM Eternal'
    '1190460' = 'Dead by Daylight'
    '304930'  = 'Unturned'
    '233860'  = 'Killing Floor 2'
    '961420'  = 'World War Z'
    '1874880' = 'Arma Reforger'
    '1698120' = 'Insurgency: Sandstorm'
    '1041320' = 'World War 3'
    '2429640' = 'Bodycam'
    '1162750' = 'Squad 44'
    '664180'  = 'Battlefield 1 (Revolution)'
}

function Write-Result([hashtable]$Result) {
    $json = $Result | ConvertTo-Json -Depth 7 -Compress
    if ($ResultFile) {
        [System.IO.File]::WriteAllText($ResultFile, $json, [System.Text.UTF8Encoding]::new($false))
    } else {
        Write-Output $json
    }
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-SteamLibraries {
    $roots = @(@(
        "${env:ProgramFiles(x86)}\Steam",
        "$env:ProgramFiles\Steam",
        "$env:USERPROFILE\Steam"
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) })

    @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') | ForEach-Object {
        try {
            $steamPath = (Get-ItemProperty -Path $_ -ErrorAction Stop).SteamPath
            if (-not $steamPath) { $steamPath = (Get-ItemProperty -Path $_ -ErrorAction Stop).InstallPath }
            if ($steamPath) {
                $steamPath = $steamPath -replace '/', '\\'
                if (Test-Path -LiteralPath $steamPath) { $roots += $steamPath }
            }
        } catch {}
    }

    foreach ($root in @($roots)) {
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            $text = Get-Content -LiteralPath $vdf -Raw -ErrorAction SilentlyContinue
            [regex]::Matches($text, '"path"\s*"(?<path>[^"]+)"') | ForEach-Object {
                $candidate = $_.Groups['path'].Value.Replace('\\', '\')
                if (Test-Path -LiteralPath $candidate) { $roots += $candidate }
            }
        }
    }
    # De-duplicate case-insensitively: Windows paths ignore case and Steam can report the same
    # library twice with different capitalisation (e.g. "d:\games\steam" and "D:\Games\Steam"),
    # which produced duplicate game entries.
    $unique = @{}
    foreach ($root in @($roots)) {
        if (-not $root) { continue }
        $trimmed = $root.TrimEnd('\')
        $key = $trimmed.ToLowerInvariant()
        if (-not $unique.ContainsKey($key)) { $unique[$key] = $trimmed }
    }
    return @($unique.Values)
}

function Find-Game([hashtable]$Game) {
    $running = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Game.Executable)) -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($running) {
        try {
            $path = $running.Path
            if ($path -and (Test-Path -LiteralPath $path)) {
                return @{ Installed = $true; Running = $true; ExecutablePath = $path; Source = 'Running process' }
            }
        } catch {}
    }
    foreach ($library in Get-SteamLibraries) {
        $manifest = Join-Path $library "steamapps\appmanifest_$($Game.AppId).acf"
        if (Test-Path -LiteralPath $manifest) {
            $content = Get-Content -LiteralPath $manifest -Raw -ErrorAction SilentlyContinue
            $match = [regex]::Match($content, '"installdir"\s*"(?<dir>[^"]+)"')
            if ($match.Success) {
                $base = Join-Path (Join-Path $library 'steamapps\common') $match.Groups['dir'].Value
                $exe = Join-Path $base $Game.RelativePath
                if (Test-Path -LiteralPath $exe) {
                    return @{ Installed = $true; Running = $false; ExecutablePath = $exe; Source = 'Steam library' }
                }
                # Fallback for games whose layout differs from the hardcoded relative path: look for
                # the executable by name a few levels deep instead of reporting them as not installed.
                try {
                    $found = Get-ChildItem -LiteralPath $base -Filter $Game.Executable -Recurse -File -Depth 3 -ErrorAction SilentlyContinue | Select-Object -First 1
                    if ($found) {
                        return @{ Installed = $true; Running = $false; ExecutablePath = $found.FullName; Source = 'Steam library (searched)' }
                    }
                } catch {}
            }
        }
    }
    if ($Game.Id -eq 'apex') {
        foreach ($candidate in @(
            (Join-Path $env:ProgramFiles 'EA Games\Apex Legends\r5apex.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Origin Games\Apex\r5apex.exe')
        )) {
            if (Test-Path -LiteralPath $candidate) {
                return @{ Installed = $true; Running = $false; ExecutablePath = $candidate; Source = 'EA app' }
            }
        }
    }
    # Standalone clients (own launcher, not Steam): locate them through the uninstall registry and a
    # few common install roots. Only INSTALLED games are ever shown, so this detection is what makes
    # e.g. Brown Dust 2 appear for someone who actually has it -- and stay hidden for everyone else.
    if ($Game.ContainsKey('Standalone') -and $Game.Standalone) {
        $exeName = $Game.Executable
        foreach ($root in @(
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
            'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
        )) {
            foreach ($key in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
                try {
                    $props = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction Stop
                    if (-not $props.DisplayName) { continue }
                    if ($props.DisplayName -notlike "*$($Game.Name)*") { continue }
                    $loc = $props.InstallLocation
                    if ($loc -and (Test-Path -LiteralPath $loc)) {
                        $exe = Get-ChildItem -LiteralPath $loc -Filter $exeName -Recurse -File -Depth 4 -ErrorAction SilentlyContinue | Select-Object -First 1
                        return @{
                            Installed = $true; Running = $false
                            ExecutablePath = $(if ($exe) { $exe.FullName } else { $null })
                            Source = 'Registry (standalone)'
                        }
                    }
                } catch {}
            }
        }
        foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA, $env:APPDATA)) {
            if (-not $base) { continue }
            foreach ($sub in @('STOVE', 'Neowiz', 'BrownDust2', 'Brown Dust 2')) {
                $dir = Join-Path $base $sub
                if (-not (Test-Path -LiteralPath $dir)) { continue }
                $exe = Get-ChildItem -LiteralPath $dir -Filter $exeName -Recurse -File -Depth 5 -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($exe) {
                    return @{ Installed = $true; Running = $false; ExecutablePath = $exe.FullName; Source = 'Common path (standalone)' }
                }
            }
        }
    }
    return @{ Installed = $false; Running = $false; ExecutablePath = $null; Source = $null }
}

function Get-SteamInstalledApps {
    $apps = @()
    $seen = @{}
    foreach ($library in Get-SteamLibraries) {
        $appsDir = Join-Path $library 'steamapps'
        if (-not (Test-Path -LiteralPath $appsDir)) { continue }
        foreach ($manifest in @(Get-ChildItem -LiteralPath $appsDir -Filter 'appmanifest_*.acf' -File -ErrorAction SilentlyContinue)) {
            try {
                $content = Get-Content -LiteralPath $manifest.FullName -Raw -ErrorAction SilentlyContinue
                if (-not $content) { continue }
                $appId = [regex]::Match($content, '"appid"\s*"(?<v>\d+)"').Groups['v'].Value
                $name = [regex]::Match($content, '"name"\s*"(?<v>[^"]+)"').Groups['v'].Value
                $installDir = [regex]::Match($content, '"installdir"\s*"(?<v>[^"]+)"').Groups['v'].Value
                if ($appId -and $name -and -not $seen.ContainsKey($appId)) {
                    $seen[$appId] = $true
                    $installPath = $null
                    if ($installDir) {
                        $candidate = Join-Path (Join-Path $library 'steamapps\common') $installDir
                        if (Test-Path -LiteralPath $candidate) { $installPath = $candidate }
                    }
                    $apps += [ordered]@{ appId = $appId; name = $name; installDir = $installDir; installPath = $installPath }
                }
            } catch {}
        }
    }
    return $apps
}

# --- Platform targets ---------------------------------------------------------------------------
# Most games only expose the SAME handful of platform endpoints (login, updates, friends, store).
# Ranking them per-game does not scale, so the endpoints are defined per PLATFORM and every
# catalogue title inherits its platform's targets. A game only needs its own $Catalog entry when it
# has game-specific endpoints worth ranking (see Limbus / Brown Dust 2).
$PlatformTargets = [ordered]@{
    'Steam' = @{
        Vendor = 'Steam'; Tag = 'STEAM'; Label = 'Steam platform (login / updates)'
        Domains = @('api.steampowered.com', 'login.steampowered.com', 'steamcdn-a.akamaihd.net')
    }
    'Ubisoft' = @{
        Vendor = 'Ubisoft Connect'; Tag = 'UBISOFT'; Label = 'Ubisoft Connect (login / services)'
        Domains = @('ubisoft.com', 'www.ubisoft.com', 'ubisoftconnect.com')
    }
    'Epic' = @{
        Vendor = 'Epic Games'; Tag = 'EPIC'; Label = 'Epic Online Services'
        Domains = @('epicgames.com', 'www.epicgames.com')
    }
    'Blizzard' = @{
        Vendor = 'Blizzard'; Tag = 'BLIZZARD'; Label = 'Battle.net'
        Domains = @('blizzard.com', 'us.battle.net')
    }
    'EA' = @{
        Vendor = 'EA'; Tag = 'EA'; Label = 'EA account / services'
        Domains = @('accounts.ea.com', 'signin.ea.com', 'origin-a.akamaihd.net')
    }
}

# Which platform a catalogue title belongs to. Steam is the default; only exceptions are listed.
# Rockstar is deliberately absent: every one of its endpoints resolves to a single IP, so there is
# nothing to rank (the game still shows up, just without routes).
$GamePlatforms = @{
    '359550' = 'Ubisoft'    # Rainbow Six Siege
    '1938090' = 'Blizzard'  # Call of Duty (Battle.net launcher for many users)
    '236390' = 'Steam'      # War Thunder (Steam + own launcher)
}

function Get-PlatformForApp([string]$AppId) {
    if ($GamePlatforms.ContainsKey($AppId)) { return $GamePlatforms[$AppId] }
    return 'Steam'
}

# A Steam game very often launches through the publisher's OWN client as well (Battlefield -> EA App,
# Rainbow Six -> Ubisoft Connect, many titles -> Epic Online Services). Those services have their own
# rankable endpoints, so the game should get their targets too -- and this is detected from the
# game's own folder, which needs no network and no hand-maintained list.
# Only the top level is inspected: the markers are launcher folders, so a deep walk would be slow
# for nothing.
function Get-PlatformHintsFromDisk([string]$InstallPath) {
    $hints = @()
    if (-not $InstallPath -or -not (Test-Path -LiteralPath $InstallPath)) { return $hints }
    try {
        $top = @(Get-ChildItem -LiteralPath $InstallPath -ErrorAction SilentlyContinue)
        $names = @($top | ForEach-Object { $_.Name })
        # EA: Origin-era games ship "__Installer"; newer ones ship an EA anticheat / desktop folder.
        foreach ($n in $names) {
            if ($n -eq '__Installer' -or $n -match '^(EAAntiCheat|EA Desktop|Origin|EA SPORTS)') { $hints += 'EA'; break }
        }
        foreach ($n in $names) {
            if ($n -match '^(Ubisoft|Uplay|UbisoftGameLauncher)') { $hints += 'Ubisoft'; break }
        }
        foreach ($n in $names) {
            if ($n -match '^Epic' -or $n -eq 'EOSSDK') { $hints += 'Epic'; break }
        }
        foreach ($n in $names) {
            if ($n -match '^Battle\.net' -or $n -match '^Blizzard') { $hints += 'Blizzard'; break }
        }
    } catch {}
    return @($hints | Select-Object -Unique)
}

# All endpoint groups for one game: its primary platform first, then any additional publisher
# client detected on disk.
function Get-TargetsForPlatform {
    param([string]$PlatformName, [string[]]$ExtraPlatforms = @())
    $groups = @(Get-PlatformTargets $PlatformName)
    $seenTags = @{}
    foreach ($g in $groups) { $seenTags[$g.Tag] = $true }
    foreach ($extra in @($ExtraPlatforms)) {
        if (-not $extra -or $extra -eq $PlatformName) { continue }
        foreach ($g in @(Get-PlatformTargets $extra)) {
            if ($seenTags.ContainsKey($g.Tag)) { continue }
            $seenTags[$g.Tag] = $true
            $groups += $g
        }
    }
    return $groups
}

function Get-PlatformTargets([string]$PlatformName) {
    if (-not $PlatformTargets.Contains($PlatformName)) { return @() }
    $p = $PlatformTargets[$PlatformName]
    return @(@{ Label = $p.Label; Tag = $p.Tag; Mode = 'probe'; Domains = @($p.Domains) })
}

# --- Offline online/single-player detection ------------------------------------------------------
# Steam stores each app's category flags locally in appcache/appinfo.vdf, and the key names are
# `category_<id>` using Steam's own ids:
#   1 = Multi-player, 9 = Co-op, 20 = MMO, 27 = Cross-Platform Multiplayer,
#   36 = Online PvP, 38 = Online Co-op
# So whether an installed game has an online component can be read OFFLINE, with no store API (which
# is unreachable on many networks) and no hardcoded whitelist.
#
# File layout (verified on a real install): 16-byte header (magic 0x07564428/0x07564429, universe,
# string-table offset), then repeating [appid u32][size u32][binary KeyValues], and finally the
# string table - a run of NUL-terminated key names that the KeyValues reference BY INDEX.
$SteamOnlineCategoryIds = @(1, 9, 20, 27, 36, 38)

function Get-SteamAppOnlineFlags([string[]]$AppIds) {
    $flags = @{}
    if (-not $AppIds -or @($AppIds).Count -eq 0) { return $flags }
    $vdf = $null
    foreach ($lib in Get-SteamLibraries) {
        $candidate = Join-Path $lib 'appcache\appinfo.vdf'
        if (Test-Path -LiteralPath $candidate) { $vdf = $candidate; break }
    }
    if (-not $vdf) { return $flags }
    try {
        $bytes = [System.IO.File]::ReadAllBytes($vdf)
        if ($bytes.Length -lt 64) { return $flags }
        $magic = [BitConverter]::ToUInt32($bytes, 0)
        if ($magic -ne 0x07564428 -and $magic -ne 0x07564429) { return $flags }
        $strOffset = [int][BitConverter]::ToUInt64($bytes, 8)
        if ($strOffset -le 16 -or $strOffset -ge $bytes.Length) { return $flags }

        $strings = [System.Collections.Generic.List[string]]::new()
        $i = $strOffset
        while ($i -lt $bytes.Length) {
            $start = $i
            while ($i -lt $bytes.Length -and $bytes[$i] -ne 0) { $i++ }
            if ($i -ge $bytes.Length) { break }
            $s = [System.Text.Encoding]::UTF8.GetString($bytes, $start, $i - $start)
            if ($s) { $strings.Add($s) }
            $i++
        }

        # byte patterns for the indices of the online-category keys
        $patterns = @()
        foreach ($cid in $SteamOnlineCategoryIds) {
            $idx = $strings.IndexOf("category_$cid")
            if ($idx -ge 0) {
                $patterns += ,@([byte]($idx -band 0xFF), [byte](($idx -shr 8) -band 0xFF), [byte](($idx -shr 16) -band 0xFF), [byte](($idx -shr 24) -band 0xFF))
            }
        }
        if ($patterns.Count -eq 0) { return $flags }

        $wanted = @{}
        foreach ($id in @($AppIds)) { $wanted["$id"] = $true }
        $pos = 16
        while ($pos -lt $strOffset - 8) {
            $appId = [BitConverter]::ToUInt32($bytes, $pos)
            $size = [BitConverter]::ToUInt32($bytes, $pos + 4)
            if ($appId -eq 0 -or $size -le 0 -or ($pos + 8 + $size) -gt $bytes.Length) { break }
            if ($wanted.ContainsKey("$appId")) {
                $online = $false
                $end = $pos + 8 + $size - 3
                foreach ($p in $patterns) {
                    for ($k = $pos + 8; $k -lt $end; $k++) {
                        if ($bytes[$k] -eq $p[0] -and $bytes[$k+1] -eq $p[1] -and $bytes[$k+2] -eq $p[2] -and $bytes[$k+3] -eq $p[3]) { $online = $true; break }
                    }
                    if ($online) { break }
                }
                $flags["$appId"] = $online
                if ($flags.Count -ge $wanted.Count) { break }
            }
            $pos += 8 + $size
        }
    } catch {}
    return $flags
}

# The frontend reads lower-case field names (label / tag / domains), while groups defined in
# $Catalog use PowerShell's natural upper-case spelling. Everything must therefore be normalised in
# ONE place before serialising: platform-derived groups used to skip this step, so games without a
# dedicated profile (Battlefield, SCP: SL, ...) rendered as empty cards while profiled games worked.
# Accepts either spelling so a caller cannot get it wrong again.
function ConvertTo-GroupList($Groups) {
    $out = @()
    foreach ($g in @($Groups)) {
        if (-not $g) { continue }
        $lbl = $g.label;   if (-not $lbl) { $lbl = $g.Label }
        $tg  = $g.tag;     if (-not $tg)  { $tg  = $g.Tag }
        $dm  = $g.domains; if (-not $dm)  { $dm  = $g.Domains }
        $out += [ordered]@{ label = $lbl; tag = $tg; domains = @($dm) }
    }
    return $out
}

function Get-GameTargets($Game) {
    return ConvertTo-GroupList $Game.OptGroups
}

# True when this game is recorded as boosted. Uses the local state file because reading the QoS
# policy store requires elevation, which the app does not have while merely scanning.
# Boot time is the cheapest way to tell whether an ActiveStore rule is still in force: that store
# lives in memory only, so a rule created before the last boot is gone. Windows Home editions have
# no Group Policy store (every Local call fails with System Error 53), so their rules always land in
# ActiveStore and always need re-applying after a restart.
# Resolved once per run: the WMI query is not free, and Test-GameBoosted calls this for every game.
$script:BootTimeResolved = $false
$script:BootTimeValue = $null

function Get-BootTimeStamp {
    if ($script:BootTimeResolved) { return $script:BootTimeValue }
    $script:BootTimeResolved = $true
    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        $script:BootTimeValue = ([datetime]$os.LastBootUpTime).ToString('o')
    } catch {
        $script:BootTimeValue = $null
    }
    return $script:BootTimeValue
}

# --- Keeping ActiveStore rules alive across reboots ----------------------------------------------
# ActiveStore lives in memory, so on Home editions (which have no Group Policy store at all) a boost
# would silently disappear on every restart. A logon task re-creates the rules, so the user never has
# to remember to click the button again.
$BoostTaskName = 'BatterBabel-BoostReapply'

function Register-BoostReapplyTask {
    try {
        $scriptPath = $PSCommandPath
        if (-not $scriptPath) { return $false }
        $arg = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$scriptPath`" -Action reapply-boost"
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arg
        $trigger = New-ScheduledTaskTrigger -AtLogOn
        $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
        Register-ScheduledTask -TaskName $BoostTaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force -ErrorAction Stop | Out-Null
        return $true
    } catch {
        return $false
    }
}

function Unregister-BoostReapplyTask {
    try {
        Unregister-ScheduledTask -TaskName $BoostTaskName -Confirm:$false -ErrorAction Stop
        return $true
    } catch {
        return $false
    }
}

function Test-BoostReapplyTask {
    try {
        $t = Get-ScheduledTask -TaskName $BoostTaskName -ErrorAction Stop
        return ($null -ne $t)
    } catch {
        return $false
    }
}

function Test-HasActiveStoreBoost {
    foreach ($item in @(Read-BoostState)) {
        if ($item.store -eq 'ActiveStore') { return $true }
    }
    return $false
}

# --- Scan cache -----------------------------------------------------------------------------------
# A full scan costs about a second: it walks every Steam library manifest and runs the appinfo.vdf
# category pass for each installed app. Repeating that on every launch is wasted work, so the result
# is cached and reused for 24 hours. The UI's "detect games" button calls scan-force to refresh it.
# Boost state is deliberately NOT taken from the cache -- it can change at any time -- and is
# recomputed on every read.
function Get-GameCachePath {
    $dir = Join-Path $env:LOCALAPPDATA 'BatterBabel'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    return Join-Path $dir 'games-cache.json'
}

function Read-GameCache([int]$MaxAgeHours = 24) {
    $path = Get-GameCachePath
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try {
        $text = [System.IO.File]::ReadAllText($path, [System.Text.UTF8Encoding]::new($false))
        if (-not $text.Trim()) { return $null }
        $data = $text | ConvertFrom-Json
        if (-not $data.savedAt -or -not $data.games) { return $null }
        $age = (Get-Date) - [datetime]$data.savedAt
        if ($age.TotalHours -gt $MaxAgeHours) { return $null }
        return $data
    } catch {
        return $null
    }
}

function Write-GameCache($Games, $Qos) {
    $path = Get-GameCachePath
    $payload = [ordered]@{
        savedAt = (Get-Date).ToString('o')
        games = @($Games)
        qos = $Qos
    }
    try {
        [System.IO.File]::WriteAllText($path, ($payload | ConvertTo-Json -Depth 8 -Compress), [System.Text.UTF8Encoding]::new($false))
    } catch {}
}

# Probes what the QoS boost can actually do on THIS machine, without needing admin, so the UI can
# warn before the user clicks instead of failing afterwards:
#   available  - the QoS Packet Scheduler must be bound to at least one adapter
#   store      - 'Local' when the Group Policy feature exists (rule survives reboots), otherwise
#                'ActiveStore' (Home editions; kept alive by the logon task)
function Get-QosCapability {
    $cap = [ordered]@{ available = $true; store = 'ActiveStore'; persistent = $false; reason = $null }
    try {
        $bound = @(Get-NetAdapterBinding -ComponentID 'ms_pacer' -ErrorAction SilentlyContinue | Where-Object { $_.Enabled })
        if ($bound.Count -eq 0) {
            $cap.available = $false
            $cap.reason = 'The "QoS Packet Scheduler" component is not enabled on any network adapter.'
            return $cap
        }
    } catch {}
    $hasGroupPolicy = $false
    try { $hasGroupPolicy = [bool](Get-Module -ListAvailable -Name GroupPolicy -ErrorAction SilentlyContinue) } catch {}
    if (-not $hasGroupPolicy) { $hasGroupPolicy = (Test-Path -LiteralPath "$env:SystemRoot\System32\gpedit.msc") }
    if ($hasGroupPolicy) {
        $cap.store = 'Local'
        $cap.persistent = $true
    }
    return $cap
}

function Test-GameBoosted([string]$GameId) {
    if (-not $GameId) { return $false }
    $boot = Get-BootTimeStamp
    foreach ($item in @(Read-BoostState)) {
        if ($item.id -ne $GameId) { continue }
        # A rule created in ActiveStore before the current boot no longer exists.
        if ($item.store -eq 'ActiveStore' -and $boot -and $item.bootTime -and $item.bootTime -ne $boot) {
            return $false
        }
        return $true
    }
    return $false
}

function Get-OnlineGameList {
    $result = @()
    $covered = @{}
    # 1) games that have a dedicated optimisation profile (listed whether installed or not)
    foreach ($key in @($Catalog.Keys)) {
        $game = $Catalog[$key]
        $location = Find-Game($game)
        $covered["$($game.AppId)"] = $true
        # HelpsGameplay = ranking these endpoints actually improves the game experience itself
        # (Limbus / Brown Dust 2: content CDN + game API). When it is false, the app tells the user
        # plainly that only platform services were accelerated -- no matter what genre the game is.
        $helpsGameplay = $false
        if ($game.ContainsKey('HelpsGameplay')) { $helpsGameplay = [bool]$game.HelpsGameplay }
        # Standalone = distributed outside Steam (own launcher / mobile). There is nothing to detect
        # locally, so the picker labels it as a standalone client instead of "not installed".
        $standalone = $false
        if ($game.ContainsKey('Standalone')) { $standalone = [bool]$game.Standalone }
        $result += [ordered]@{
            id = $game.Id; appId = $game.AppId; name = $game.Name; vendor = $game.Vendor
            installed = $location.Installed; running = $location.Running
            executablePath = $location.ExecutablePath; source = $location.Source
            support = $true; online = $true; accelerated = (Test-GameBoosted $game.Id)
            helpsGameplay = $helpsGameplay; standalone = $standalone
            optGroups = @(Get-GameTargets $game)
        }
    }
    # 2) installed titles that are known ONLINE games. Steam records no online/single-player flag
    #    locally (appinfo.vdf carries only key names, and store.steampowered.com is unreachable on
    #    many networks), so this catalogue is the only reliable filter -- single-player titles and
    #    tools are simply not in it and therefore never show up.
    $toolIds = @{ '228980' = $true }   # Steamworks Common Redistributables is not a game
    $installedApps = @(Get-SteamInstalledApps)

    # Ask Steam's own LOCAL metadata whether each installed title has an online component. This is
    # what makes the app universal: anyone who installs Battlefield / Rainbow Six / whatever gets it
    # detected automatically, with no whitelist entry. $OnlineGameIds is only a fallback for the
    # rare title the local metadata does not cover.
    $onlineFlags = Get-SteamAppOnlineFlags @($installedApps | ForEach-Object { $_.appId })

    foreach ($app in $installedApps) {
        if ($covered.ContainsKey($app.appId)) { continue }
        if ($toolIds.ContainsKey($app.appId)) { continue }

        $isOnline = $false
        if ($onlineFlags.ContainsKey($app.appId)) {
            $isOnline = [bool]$onlineFlags[$app.appId]
        } elseif ($OnlineGameIds.ContainsKey($app.appId)) {
            $isOnline = $true
        }
        if (-not $isOnline) { continue }   # single-player game or tool -> never listed

        # Endpoints: the platform it ships on, PLUS any publisher client detected on disk -- a Steam
        # copy of Battlefield still signs in through the EA App, so it needs both.
        $platformName = Get-PlatformForApp $app.appId
        $extraPlatforms = @()
        if ($platformName -eq 'Steam' -and $app.installPath) {
            $extraPlatforms = @(Get-PlatformHintsFromDisk $app.installPath)
        }
        $platformGroups = @(Get-TargetsForPlatform -PlatformName $platformName -ExtraPlatforms $extraPlatforms)
        # Normalise to the lower-case shape the frontend expects (platform groups used to keep
        # PowerShell's upper-case spelling and rendered as blank cards).
        $platformGroups = @(ConvertTo-GroupList $platformGroups)
        $vendorName = 'Steam'
        if ($PlatformTargets.Contains($platformName)) { $vendorName = $PlatformTargets[$platformName].Vendor }
        if ($extraPlatforms.Count -gt 0) { $vendorName = "$vendorName + $($extraPlatforms -join ' / ')" }
        $displayName = $app.name
        if ($OnlineGameIds.ContainsKey($app.appId)) { $displayName = $OnlineGameIds[$app.appId] }
        $result += [ordered]@{
            id = "steam-$($app.appId)"; appId = $app.appId
            name = $displayName
            vendor = $vendorName
            installed = $true; running = $false
            executablePath = $null; source = 'Steam library'
            support = ($platformGroups.Count -gt 0)
            online = $true; recognized = $true
            accelerated = $false; helpsGameplay = $false; standalone = $false
            platform = $platformName
            extraPlatforms = $extraPlatforms
            optGroups = $platformGroups
        }
    }
    return $result
}

function Get-PolicyInfo {
    $names = @()
    foreach ($store in @('Local', 'ActiveStore')) {
        try {
            $names += @(Get-NetQosPolicy -PolicyStore $store -ErrorAction Stop |
                Where-Object { $_.Name -like 'BatterBabel-*' } |
                Select-Object -ExpandProperty Name)
        } catch {}
    }
    return @($names | Select-Object -Unique)
}

# --- Boost state ------------------------------------------------------------------------------
# Reading QoS policies needs elevation and ActiveStore rules die on reboot, so boost rules are
# created in the persistent Local store and mirrored to a small JSON file. A non-elevated scan can
# then still show the correct boost / cancel-boost button state.
function Get-BoostStatePath {
    $dir = Join-Path $env:LOCALAPPDATA 'BatterBabel'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    return Join-Path $dir 'boosted.json'
}

function Read-BoostState {
    $path = Get-BoostStatePath
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    try {
        $text = [System.IO.File]::ReadAllText($path, [System.Text.UTF8Encoding]::new($false))
        if (-not $text.Trim()) { return @() }
        $data = $text | ConvertFrom-Json
        if (-not $data.games) { return @() }
        return @($data.games)
    } catch { return @() }
}

function Write-BoostState($States) {
    $path = Get-BoostStatePath
    $export = [ordered]@{ updatedAt = (Get-Date).ToString('o'); games = @($States) }
    try { [System.IO.File]::WriteAllText($path, ($export | ConvertTo-Json -Depth 6), [System.Text.UTF8Encoding]::new($false)) } catch {}
}

function Add-BoostState([string]$Id, [string]$ExePath, [string]$Store, [string]$BootTime) {
    $kept = @(Read-BoostState | Where-Object { $_.id -and $_.id -ne $Id })
    $kept += [ordered]@{
        id = $Id; executablePath = $ExePath
        store = $Store
        bootTime = $BootTime
        boostedAt = (Get-Date).ToString('o')
    }
    Write-BoostState $kept
}

function Remove-BoostState([string]$Id) {
    if (-not $Id) { Write-BoostState @(); return }
    $kept = @(Read-BoostState | Where-Object { $_.id -and $_.id -ne $Id })
    Write-BoostState $kept
}

# --- Candidate routes: official, verifiable public endpoints only; no third-party proxy nodes ---
$RouteCatalog = [ordered]@{
    'cf-dns'     = @{ Name = 'Cloudflare DNS';  Host = '1.1.1.1';               Port = $null; Group = 'Public DNS' }
    'google-dns' = @{ Name = 'Google DNS';      Host = '8.8.8.8';               Port = $null; Group = 'Public DNS' }
    'cf-cdn'     = @{ Name = 'Cloudflare Edge'; Host = 'speed.cloudflare.com';  Port = 443;   Group = 'CDN / Cloud' }
    'steam-api'  = @{ Name = 'Steam Service';   Host = 'api.steampowered.com';  Port = 443;   Group = 'Steam / Valve' }
    'steam-cm'   = @{ Name = 'Steam Community'; Host = 'steamcommunity.com';    Port = 443;   Group = 'Steam / Valve' }
    'ea'         = @{ Name = 'EA Service';      Host = 'ea.com';                Port = 443;   Group = 'EA / Respawn' }
    'ea-cdn'     = @{ Name = 'EA CDN';          Host = 'origin-a.akamaihd.net'; Port = 443;   Group = 'EA / Respawn' }
    'pm-api'     = @{ Name = 'Limbus Service';  Host = 'api.limbuscompany.com'; Port = 443;   Group = 'ProjectMoon' }
}

function Resolve-Ip([string]$Hostname) {
    if ($Hostname -match '^\d{1,3}(\.\d{1,3}){3}$') { return $Hostname }
    try {
        $addr = [System.Net.Dns]::GetHostAddresses($Hostname) |
            Where-Object { $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork } |
            Select-Object -First 1
        if ($addr) { return $addr.IPAddressToString }
    } catch {}
    return $null
}

function Test-IcmpLatency([string]$Ip, [int]$Count = 5, [int]$TimeoutMs = 1000) {
    $ping = [System.Net.NetworkInformation.Ping]::new()
    try {
        $rtts = @(); $lost = 0
        for ($i = 0; $i -lt $Count; $i++) {
            try {
                $reply = $ping.Send($Ip, $TimeoutMs)
                if ($reply -and $reply.Status -eq [System.Net.NetworkInformation.IPStatus]::Success) {
                    $rtts += [int]$reply.RoundtripTime
                } else { $lost++ }
            } catch { $lost++ }
        }
        if ($rtts.Count -eq 0) {
            return @{ avg = $null; min = $null; max = $null; loss = 100; jitter = $null; samples = 0 }
        }
        $avg = ($rtts | Measure-Object -Average).Average
        $min = ($rtts | Measure-Object -Minimum).Minimum
        $max = ($rtts | Measure-Object -Maximum).Maximum
        $jitter = 0
        if ($rtts.Count -ge 2) {
            $diffs = @()
            for ($i = 1; $i -lt $rtts.Count; $i++) { $diffs += [math]::Abs($rtts[$i] - $rtts[$i - 1]) }
            $jitter = ($diffs | Measure-Object -Average).Average
        }
        $loss = [math]::Round(($lost / $Count) * 100, 1)
        return @{ avg = [math]::Round($avg, 1); min = $min; max = $max; loss = $loss; jitter = [math]::Round($jitter, 1); samples = $rtts.Count }
    } finally { $ping.Dispose() }
}

function Test-TcpLatency([string]$Ip, [int]$Port, [int]$Count = 3, [int]$TimeoutMs = 1500) {
    $rtts = @()
    for ($i = 0; $i -lt $Count; $i++) {
        $client = $null
        try {
            $client = [System.Net.Sockets.TcpClient]::new()
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $task = $client.ConnectAsync($Ip, $Port)
            if ($task.Wait($TimeoutMs)) {
                $sw.Stop()
                $rtts += [int]$sw.Elapsed.TotalMilliseconds
            }
        } catch {} finally { if ($client) { $client.Close() } }
    }
    if ($rtts.Count -eq 0) { return @{ avg = $null; ok = $false; samples = 0 } }
    return @{ avg = [math]::Round(($rtts | Measure-Object -Average).Average, 1); ok = $true; samples = $rtts.Count }
}

function Test-DownloadSpeed([int]$Bytes = 2000000) {
    try {
        Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue
        $client = [System.Net.Http.HttpClient]::new()
        $client.Timeout = [TimeSpan]::FromSeconds(20)
        try {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $resp = $client.GetAsync("https://speed.cloudflare.com/__down?bytes=$Bytes").Result
            $data = $resp.Content.ReadAsByteArrayAsync().Result
            $sw.Stop()
            if ($resp.IsSuccessStatusCode -and $data.Length -gt 0) {
                $mbps = ($data.Length / 1MB) / $sw.Elapsed.TotalSeconds
                return @{ ok = $true; mbps = [math]::Round($mbps, 2); bytes = $data.Length; ms = [math]::Round($sw.Elapsed.TotalMilliseconds, 0) }
            }
        } finally { $client.Dispose() }
    } catch {}
    return @{ ok = $false; mbps = $null; bytes = 0; ms = $null }
}

function Get-RouteProbe {
    $results = @()
    foreach ($key in $RouteCatalog.Keys) {
        $c = $RouteCatalog[$key]
        $ip = Resolve-Ip $c.Host
        $item = [ordered]@{ id = $key; name = $c.Name; group = $c.Group; host = $c.Host; ip = $ip; icmp = $null; tcp = $null; score = 9999 }
        if ($ip) {
            $item.icmp = Test-IcmpLatency $ip
            if ($c.Port) { $item.tcp = Test-TcpLatency $ip ([int]$c.Port) }
        }
        $latency = $null
        if ($item.icmp -and $item.icmp.loss -lt 100) { $latency = $item.icmp.avg }
        elseif ($item.tcp -and $item.tcp.ok) { $latency = $item.tcp.avg }
        if ($null -ne $latency) { $item.score = $latency }
        $results += $item
    }
    return $results
}

# --- Cloudflare CDN optimisation (LLC_BABEL style) ---
# Official public Cloudflare IPv4 ranges (https://www.cloudflare.com/ips-v4)
$CloudflareRanges = @(
    '173.245.48.0/20', '103.21.244.0/22', '103.22.200.0/22', '103.31.4.0/22',
    '141.101.64.0/18', '108.162.192.0/18', '190.93.240.0/20', '188.114.96.0/20',
    '197.234.240.0/22', '198.41.128.0/17', '162.158.0.0/15', '104.16.0.0/12',
    '172.64.0.0/17', '172.64.128.0/18', '172.64.192.0/19', '172.64.224.0/22',
    '172.64.229.0/24', '172.64.230.0/23', '172.64.232.0/21', '172.64.240.0/21',
    '172.64.248.0/21', '172.65.0.0/16', '172.66.0.0/16', '172.67.0.0/16', '131.0.72.0/22'
)

# Limbus Company download CDN domains (Cloudflare-fronted).
$CfDomainCatalog = [ordered]@{
    'download.limbuscompanycdn.org'       = 'Limbus Download'
    'downloadcommon.limbuscompanycdn.org' = 'Limbus Download Common'
    'downloadfmod.limbuscompanycdn.org'   = 'Limbus Download FMOD'
}

# Limbus Company API endpoints (Amazon CloudFront).
$CloudFrontEndpoints = @(
    @{ Label = 'www';    Domain = 'www.limbuscompanyapi.com';    ProbeUrl = 'https://www.limbuscompanyapi.com/' }
    @{ Label = 'notice'; Domain = 'notice.limbuscompanyapi.com'; ProbeUrl = 'https://notice.limbuscompanyapi.com/' }
)

# Public DoH resolvers used to discover CloudFront candidate IPs (avoids local DNS pollution).
$CloudFrontDohSources = @(
    @{ Name = 'AliDNS';  Url = 'https://dns.alidns.com/resolve' }
    @{ Name = 'DNSPod';  Url = 'https://doh.pub/dns-query' }
)

function Test-IpInCidr([string]$Ip, [string]$Cidr) {
    $parts = $Cidr -split '/'
    $netBytes = [System.Net.IPAddress]::Parse($parts[0]).GetAddressBytes()
    $ipBytes = [System.Net.IPAddress]::Parse($Ip).GetAddressBytes()
    $prefix = [int]$parts[1]
    $fullBytes = [math]::Ceiling($prefix / 8)
    for ($i = 0; $i -lt $fullBytes; $i++) {
        $bits = [math]::Min(8, $prefix - ($i * 8))
        if ($bits -lt 8) {
            $mask = 0xFF -shl (8 - $bits)
            if (($ipBytes[$i] -band $mask) -ne ($netBytes[$i] -band $mask)) { return $false }
        } elseif ($ipBytes[$i] -ne $netBytes[$i]) { return $false }
    }
    return $true
}

function Test-IpInCloudflare([string]$Ip) {
    foreach ($cidr in $CloudflareRanges) { if (Test-IpInCidr $Ip $cidr) { return $true } }
    return $false
}

# Curated candidate Cloudflare IPs: addresses verified reachable/fast across multiple official
# Cloudflare ranges (104.16.0.0/12, 172.64.0.0/13, 108.162.192.0/18, 162.158.0.0/15).
$CfCandidateIps = @(
    '172.64.229.1', '108.162.192.1', '104.16.0.1', '104.24.0.1', '172.66.0.1',
    '162.159.140.220', '104.16.132.229', '104.17.105.123', '104.17.112.28', '172.67.0.1'
)

function ConvertTo-IpString([long]$Value) {
    $a = [int]([math]::Floor($Value / 16777216) % 256)
    $b = [int]([math]::Floor($Value / 65536) % 256)
    $c = [int]([math]::Floor($Value / 256) % 256)
    $d = [int]($Value % 256)
    return "$a.$b.$c.$d"
}

function ConvertFrom-IpString([string]$Ip) {
    $bytes = [System.Net.IPAddress]::Parse($Ip).GetAddressBytes()
    return ([long]$bytes[0] * 16777216) + ([long]$bytes[1] * 65536) + ([long]$bytes[2] * 256) + [long]$bytes[3]
}

# Builds a large candidate pool spread evenly across every official Cloudflare IPv4 range.
# Brute-forcing the whole space is pointless (millions of addresses), so each range is walked
# with a fixed stride: broad coverage of every anycast prefix within a bounded time.
function Get-CfCandidatePool([int]$Target = 2000) {
    $pool = [System.Collections.Generic.List[string]]::new()
    $seen = @{}
    $perRange = [math]::Max(1, [int][math]::Ceiling($Target / [double]$CloudflareRanges.Count))
    foreach ($cidr in $CloudflareRanges) {
        if ($pool.Count -ge $Target) { break }
        $parts = $cidr -split '/'
        $prefix = [int]$parts[1]
        $baseValue = ConvertFrom-IpString $parts[0]
        $size = [long][math]::Pow(2, 32 - $prefix)
        $usable = [math]::Max(1, $size - 2)
        $step = [math]::Max(1, [long][math]::Floor($usable / [double]$perRange))
        $added = 0
        $offset = [long]1
        while ($added -lt $perRange -and $offset -le $usable -and $pool.Count -lt $Target) {
            $ip = ConvertTo-IpString ($baseValue + $offset)
            if (-not $seen.ContainsKey($ip)) {
                $seen[$ip] = $true
                $pool.Add($ip)
                $added++
            }
            $offset += $step
        }
    }
    return @($pool)
}

# --- Learned good IPs -------------------------------------------------------------------------
# Every run records its fastest downloaders here; an address seen in the top few more than once
# earns a free pass into the download stage next time, so good IPs accumulate without hand editing.
function Get-GoodIpPath {
    $dir = Join-Path $env:LOCALAPPDATA 'BatterBabel'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    return Join-Path $dir 'cf-good-ips.json'
}

# Chooses the IPs that skip latency screening: curated addresses (unless they keep failing) plus
# learned addresses that have won at least twice and are not currently failing.
function Get-PriorityIps([int]$MaxLearned = 10) {
    $table = @{}
    $path = Get-GoodIpPath
    if (Test-Path -LiteralPath $path) {
        try {
            $text = [System.IO.File]::ReadAllText($path, [System.Text.UTF8Encoding]::new($false))
            if ($text.Trim()) {
                $data = $text | ConvertFrom-Json
                if ($data.ips) {
                    foreach ($item in @($data.ips.PSObject.Properties)) {
                        $table[$item.Name] = @{
                            hits = $(if ($item.Value.hits) { [int]$item.Value.hits } else { 0 })
                            misses = $(if ($item.Value.misses) { [int]$item.Value.misses } else { 0 })
                            bestMbps = $item.Value.bestMbps
                        }
                    }
                }
            }
        } catch {}
    }
    # A curated address that keeps failing is demoted for this run instead of silently eating a
    # free-pass slot. It is not deleted from the source list, and recovers as soon as it works again.
    $liveCurated = @()
    $demoted = @()
    foreach ($ip in @($CfCandidateIps)) {
        $rec = $table[$ip]
        if ($rec -and [int]$rec.misses -ge 2) { $demoted += $ip } else { $liveCurated += $ip }
    }
    $learnedAll = @()
    foreach ($ip in @($table.Keys)) {
        $rec = $table[$ip]
        if ([int]$rec.hits -lt 2) { continue }
        if ([int]$rec.misses -ge 2) { continue }
        $mbps = 0.0
        if ($rec.bestMbps) { $mbps = [double]$rec.bestMbps }
        $learnedAll += [pscustomobject]@{ Ip = $ip; Hits = [int]$rec.hits; Mbps = $mbps }
    }
    $learned = @($learnedAll | Sort-Object -Property Hits -Descending | Select-Object -First $MaxLearned | ForEach-Object { $_.Ip })
    return @{
        ips = @(@($liveCurated) + @($learned) | Select-Object -Unique)
        curated = $liveCurated.Count
        demoted = $demoted.Count
        demotedIps = @($demoted)
        learned = $learned.Count
        learnedIps = @($learned)
    }
}

# Updates the library after a run. Every address download-tested this run gets its health
# refreshed: a win clears the failure count, a failure (no speed measured) increments it, and two
# consecutive failures evict the entry -- so an address that goes dark quickly stops squatting on a
# free-pass slot instead of being trusted forever on past glory.
function Update-LearnedGoodIps($ResultRows) {
    $rows = @($ResultRows)
    if ($rows.Count -eq 0) { return @{ pruned = 0; kept = 0 } }
    $path = Get-GoodIpPath
    $ips = [ordered]@{}
    if (Test-Path -LiteralPath $path) {
        try {
            $text = [System.IO.File]::ReadAllText($path, [System.Text.UTF8Encoding]::new($false))
            if ($text.Trim()) {
                $data = $text | ConvertFrom-Json
                if ($data.ips) {
                    foreach ($item in @($data.ips.PSObject.Properties)) {
                        $ips[$item.Name] = [ordered]@{
                            hits = $(if ($item.Value.hits) { [int]$item.Value.hits } else { 0 })
                            misses = $(if ($item.Value.misses) { [int]$item.Value.misses } else { 0 })
                            bestMbps = $item.Value.bestMbps
                            lastSeen = $item.Value.lastSeen
                        }
                    }
                }
            }
        } catch {}
    }
    $now = (Get-Date).ToString('o')

    # Health pass over everything we actually download-tested. Addresses we do not already track
    # are only remembered when they WIN -- otherwise every exploration failure would flood the
    # library and push the genuinely good addresses out of the entry budget.
    foreach ($row in $rows) {
        $ip = $row.ip
        if (-not $ip) { continue }
        if (-not $ips.Contains($ip)) {
            if ($row.downloadMbps -eq $null) { continue }
            $ips[$ip] = [ordered]@{ hits = 0; misses = 0; bestMbps = $null; lastSeen = $null }
        }
        if ($row.downloadMbps -ne $null) {
            $ips[$ip].misses = 0
        } else {
            $ips[$ip].misses = [int]$ips[$ip].misses + 1
        }
    }

    # This run's fastest few earn a hit.
    foreach ($row in @($rows | Where-Object { $_.downloadMbps -ne $null } | Select-Object -First 3)) {
        $ip = $row.ip
        if (-not $ip) { continue }
        $rec = $ips[$ip]
        $rec.hits = [int]$rec.hits + 1
        $rec.misses = 0
        if ($rec.bestMbps -eq $null -or [double]$row.downloadMbps -gt [double]$rec.bestMbps) {
            $rec.bestMbps = $row.downloadMbps
        }
        $rec.lastSeen = $now
    }

    # Evict dead entries, then bound the library: strongest (most wins, then fastest) first.
    $pruned = 0
    $scored = @()
    $curatedList = @($CfCandidateIps)
    foreach ($ip in @($ips.Keys)) {
        $rec = $ips[$ip]
        $isCurated = ($curatedList -contains $ip)
        if ([int]$rec.misses -ge 2) {
            # A curated address KEEPS its failure record instead of being deleted. Deleting it
            # would wipe its slate, earn it a free pass again, fail again, and loop forever.
            if (-not $isCurated) { $pruned++; continue }
        }
        $h = [int]$rec.hits
        # Keep curated addresses (maintained in source) and anything that has won at least once.
        # Pure exploration noise is dropped so it cannot crowd out real winners.
        if ($h -le 0 -and -not $isCurated) { continue }
        $m = 0.0
        if ($rec.bestMbps) { $m = [double]$rec.bestMbps }
        $scored += [pscustomobject]@{ Ip = $ip; Rec = $rec; Score = ($h * 100) + $m }
    }
    $kept = @($scored | Sort-Object -Property Score -Descending | Select-Object -First 30)
    $export = [ordered]@{ updatedAt = $now; ips = [ordered]@{} }
    foreach ($item in $kept) { $export.ips[$item.Ip] = $item.Rec }
    try {
        [System.IO.File]::WriteAllText($path, ($export | ConvertTo-Json -Depth 6), [System.Text.UTF8Encoding]::new($false))
    } catch {}
    return @{ pruned = $pruned; kept = $kept.Count }
}

# Concurrent TCP:443 connect screening. A handshake costs a few RTTs instead of a megabyte,
# which is what makes a 2000-address pool finish in seconds instead of hours.
function Test-TcpLatencyBatch([string[]]$Ips, [int]$Port = 443, [int]$TimeoutMs = 900, [int]$Concurrency = 200, [int]$RangeStart = 0, [int]$RangeEnd = 0) {
    $results = @{}
    if ($Ips.Count -eq 0) { return $results }
    $pending = [System.Collections.Generic.Queue[string]]::new()
    foreach ($ip in $Ips) { $pending.Enqueue($ip) }
    $done = 0
    while ($pending.Count -gt 0) {
        $batch = @()
        while ($batch.Count -lt $Concurrency -and $pending.Count -gt 0) { $batch += $pending.Dequeue() }
        $entries = @{}
        $tasks = [System.Collections.Generic.List[System.Threading.Tasks.Task]]::new()
        foreach ($ip in $batch) {
            try {
                $sock = New-Object System.Net.Sockets.TcpClient
                $sock.NoDelay = $true
                $watch = [System.Diagnostics.Stopwatch]::StartNew()
                $task = $sock.ConnectAsync($ip, $Port)
                $entries[$ip] = @{ sock = $sock; task = $task; watch = $watch }
                $tasks.Add($task)
            } catch {
                $results[$ip] = $null
            }
        }
        # Wait for the WHOLE batch at once. Waiting per connection would stack every timeout
        # (200 x 900 ms per batch) and would inflate the measured latency of later entries.
        if ($tasks.Count -gt 0) {
            try { [System.Threading.Tasks.Task]::WaitAll($tasks.ToArray(), $TimeoutMs) | Out-Null } catch {}
        }
        foreach ($ip in $batch) {
            if (-not $entries.ContainsKey($ip)) { continue }
            $entry = $entries[$ip]
            $entry.watch.Stop()
            $connected = $false
            try { $connected = $entry.task.IsCompleted -and $entry.sock.Connected } catch { $connected = $false }
            if ($connected) {
                $results[$ip] = [int]$entry.watch.ElapsedMilliseconds
            } else {
                $results[$ip] = $null
            }
            try { $entry.sock.Close() } catch {}
        }
        $done += $batch.Count
        if ($RangeEnd -gt $RangeStart -and $Ips.Count -gt 0) {
            $pct = $RangeStart + [int](($RangeEnd - $RangeStart) * $done / $Ips.Count)
            Write-Tick $pct "Latency screening: $done/$($Ips.Count) IPs"
        }
    }
    return $results
}

function Test-CfDownloadSpeedBatch([string[]]$Ips, [int]$Bytes = 1000000, [int]$TimeoutSec = 8, [int]$Concurrency = 2, [int]$RangeStart = 0, [int]$RangeEnd = 0) {
    $curl = "$env:SystemRoot\System32\curl.exe"
    if (-not (Test-Path $curl)) { return @{} }
    $results = @{}
    $pending = [System.Collections.Generic.Queue[string]]::new()
    foreach ($ip in $Ips) { $pending.Enqueue($ip) }
    $done = 0
    while ($pending.Count -gt 0) {
        $batch = @()
        while ($batch.Count -lt $Concurrency -and $pending.Count -gt 0) { $batch += $pending.Dequeue() }
        $procs = @{}; $files = @{}; $errFiles = @{}
        foreach ($ip in $batch) {
            $f = Join-Path $env:TEMP ("bb-cf-" + [guid]::NewGuid().ToString('N') + ".txt")
            $ef = Join-Path $env:TEMP ("bb-cf-" + [guid]::NewGuid().ToString('N') + ".err")
            try {
                $p = Start-Process -FilePath $curl -ArgumentList @('--ssl-no-revoke', '--resolve', "speed.cloudflare.com:443:$ip", "https://speed.cloudflare.com/__down?bytes=$Bytes", '-o', 'NUL', '-s', '-m', "$TimeoutSec", '-w', '%{speed_download}|%{time_starttransfer}|%{http_code}') -RedirectStandardOutput $f -RedirectStandardError $ef -WindowStyle Hidden -PassThru
                $procs[$ip] = $p; $files[$ip] = $f; $errFiles[$ip] = $ef
            } catch { $procs[$ip] = $null; $files[$ip] = $f; $errFiles[$ip] = $ef }
        }
        foreach ($ip in $batch) {
            if ($procs[$ip]) { try { $procs[$ip].WaitForExit(($TimeoutSec * 1000) + 3000) | Out-Null } catch {} }
            $content = ''
            if (Test-Path -LiteralPath $files[$ip]) { $content = [System.IO.File]::ReadAllText($files[$ip]).Trim() }
            Remove-Item -LiteralPath $files[$ip] -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $errFiles[$ip] -Force -ErrorAction SilentlyContinue
            $parts = $content -split '\|'
            $mbps = $null; $latency = $null
            if ($parts.Count -ge 3 -and $parts[2] -eq '200') {
                if ([double]$parts[0] -gt 0) { $mbps = [math]::Round([double]$parts[0] / 1048576, 2) }
                if ([double]$parts[1] -gt 0) { $latency = [math]::Round([double]$parts[1] * 1000, 0) }
            }
            $results[$ip] = @{ downloadMbps = $mbps; latency = $latency }
        }
        $done += $batch.Count
        if ($RangeEnd -gt $RangeStart -and $Ips.Count -gt 0) {
            $pct = $RangeStart + [int](($RangeEnd - $RangeStart) * $done / $Ips.Count)
            Write-Tick $pct "Cloudflare download test: $done/$($Ips.Count) IPs"
        }
    }
    return $results
}

function Get-CfOptimize([int]$RangeStart = 0, [int]$RangeEnd = 0, [int]$PoolSize = 2000, [int]$Finalists = 20) {
    $span = [math]::Max(1, $RangeEnd - $RangeStart)
    $mid = $RangeStart + [int]($span * 0.7)

    # Phase 1: cheap TCP screening over the sampled pool (this is what makes 2000 addresses viable).
    $pool = @(Get-CfCandidatePool -Target $PoolSize)
    Write-Tick $RangeStart "Latency screening: 0/$($pool.Count) IPs"
    $latencies = Test-TcpLatencyBatch -Ips $pool -Concurrency 200 -TimeoutMs 900 -RangeStart $RangeStart -RangeEnd $mid

    $alive = @($latencies.GetEnumerator() | Where-Object { $_.Value -ne $null })
    $fastest = @($alive | Sort-Object { [int]$_.Value } | Select-Object -First $Finalists)
    $fastestIps = @($fastest | ForEach-Object { $_.Key })

    # Curated AND previously-learned addresses go straight to the download stage, minus anything
    # that has been failing recently. TCP latency does not predict throughput, so screening alone
    # would silently drop known-good IPs.
    $priority = Get-PriorityIps
    $priorityIps = @($priority.ips)
    $finalistIps = @($priorityIps + $fastestIps | Select-Object -Unique)

    # Phase 2: real download measurement on the priority IPs plus the lowest-latency discoveries.
    Write-Tick $mid "Download test on $($finalistIps.Count) IPs ($($priority.curated) curated + $($priority.learned) learned + $($fastestIps.Count) fastest)"
    $measured = @{}
    if ($finalistIps.Count -gt 0) {
        $measured = Test-CfDownloadSpeedBatch -Ips $finalistIps -RangeStart $mid -RangeEnd $RangeEnd
    }

    $rows = @()
    foreach ($ip in $finalistIps) {
        $mbps = $null
        $dlLatency = $null
        if ($measured.ContainsKey($ip)) {
            $mbps = $measured[$ip].downloadMbps
            $dlLatency = $measured[$ip].latency
        }
        $ping = $latencies[$ip]
        $useLatency = $ping
        if ($dlLatency -ne $null) { $useLatency = $dlLatency }
        $rows += [ordered]@{ ip = $ip; downloadMbps = $mbps; latency = $useLatency; pingMs = $ping }
    }

    $ranked = @($rows | Sort-Object { if ($_.downloadMbps -eq $null) { -1 } else { [double]$_.downloadMbps } } -Descending)
    $rank = 0
    $list = @()
    foreach ($r in $ranked) {
        $rank++
        $list += [ordered]@{ rank = $rank; ip = $r.ip; downloadMbps = $r.downloadMbps; latency = $r.latency }
    }
    $bestIp = $null; $bestMbps = $null; $bestLatency = $null
    $bestEntry = @($ranked | Where-Object { $_.downloadMbps -ne $null } | Select-Object -First 1)
    if (-not $bestEntry) { $bestEntry = @($ranked | Select-Object -First 1) }
    if ($bestEntry) {
        $bestIp = $bestEntry[0].ip
        $bestMbps = $bestEntry[0].downloadMbps
        $bestLatency = $bestEntry[0].latency
    }
    # Learn: winners get promoted, addresses that stop working get demoted (2 misses = evicted).
    $learnState = Update-LearnedGoodIps $ranked

    return @{
        candidates = $list
        bestIp = $bestIp
        bestDownloadMbps = $bestMbps
        bestLatency = $bestLatency
        testedCandidates = $finalistIps.Count
        poolSize = $pool.Count
        aliveCount = $alive.Count
        priorityCount = $priorityIps.Count
        learnedCount = $priority.learned
        learnedIps = @($priority.learnedIps)
        demotedCount = $priority.demoted
        demotedIps = @($priority.demotedIps)
        prunedCount = $learnState.pruned
        testedAt = (Get-Date).ToString('o')
    }
}

# --- CloudFront (Amazon) optimisation ---
function Get-DohRecords([string]$DohUrl, [string]$Domain) {
    $curl = "$env:SystemRoot\System32\curl.exe"
    try {
        $encoded = [uri]::EscapeDataString($Domain)
        $json = & $curl --ssl-no-revoke -s -m 8 -H 'Accept: application/dns-json' "$DohUrl`?name=$encoded&type=A" 2>$null
        $obj = ($json -join '') | ConvertFrom-Json
        if ($obj.Status -eq 0 -and $obj.Answer) {
            return @($obj.Answer | Where-Object { $_.type -eq 1 } | ForEach-Object { $_.data } | Where-Object { $_ -match '^\d{1,3}(\.\d{1,3}){3}$' })
        }
    } catch {}
    return @()
}

function Get-CloudFrontCandidates([string]$Domain, [int]$Max = 5) {
    $ips = @()
    try {
        $ips += @([System.Net.Dns]::GetHostAddresses($Domain) | Where-Object { $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork } | ForEach-Object { $_.IPAddressToString })
    } catch {}
    foreach ($src in $CloudFrontDohSources) {
        $ips += @(Get-DohRecords $src.Url $Domain)
    }
    # Cap the list: DNS already returns CDN-preferred addresses, and probing every one of them was
    # what made a Steam-API group (8 addresses x 3 curls x 8 s timeout) blow past the timeout.
    return @($ips | Select-Object -Unique | Select-Object -First $Max)
}

function Test-CloudFrontLatency([string]$Domain, [string]$Ip, [int]$Count = 2, [int]$TimeoutSec = 5) {
    $curl = "$env:SystemRoot\System32\curl.exe"
    $rtts = @()
    for ($i = 0; $i -lt $Count; $i++) {
        try {
            $out = & $curl --ssl-no-revoke --resolve "$Domain`:443:$Ip" "https://$Domain/" -o NUL -s -m $TimeoutSec -w '%{time_starttransfer}|%{http_code}' 2>$null
            $parts = ($out -join '') -split '\|'
            if ($parts.Count -ge 2 -and $parts[1] -ne '000' -and $parts[1] -match '^\d{3}$' -and [double]$parts[0] -gt 0) {
                $rtts += [double]$parts[0]
            }
        } catch {}
    }
    if ($rtts.Count -eq 0) { return $null }
    $sorted = @($rtts | Sort-Object)
    $median = $sorted[[math]::Floor($sorted.Count / 2)]
    return [math]::Round($median * 1000, 0)
}

function Get-HostsPath {
    return Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
}

# Tag names used for the per-provider hosts blocks (kept in sync with the game catalog).
$BabelHostsTags = @('CF', 'AMAZON', 'STEAM', 'EA', 'KLEI', 'UBISOFT', 'EPIC', 'BLIZZARD')

function Set-BabelHosts([hashtable]$ByTag, [string]$HostsPath) {
    if (-not $HostsPath) { $HostsPath = Get-HostsPath }
    if (-not (Test-Path -LiteralPath $HostsPath)) { throw "hosts file not found: $HostsPath" }
    $backup = "$HostsPath.batterbabel.bak"
    Copy-Item -LiteralPath $HostsPath -Destination $backup -Force
    $lines = @(Get-Content -LiteralPath $HostsPath -Encoding UTF8)
    $clean = [System.Collections.Generic.List[string]]::new()
    $inBlock = $false
    foreach ($line in $lines) {
        if ($line -match '^# ==== Batter Babel \S+ START') { $inBlock = $true; continue }
        if ($line -match '^# ==== Batter Babel \S+ END')   { $inBlock = $false; continue }
        if (-not $inBlock) { $clean.Add($line) }
    }
    while ($clean.Count -gt 0 -and $clean[$clean.Count - 1].Trim() -eq '') { $clean.RemoveAt($clean.Count - 1) }
    foreach ($tag in $BabelHostsTags) {
        if ($ByTag.Contains($tag) -and $ByTag[$tag].Count -gt 0) {
            $clean.Add('')
            $clean.Add("# ==== Batter Babel $tag START")
            foreach ($l in $ByTag[$tag]) { $clean.Add($l) }
            $clean.Add("# ==== Batter Babel $tag END")
        }
    }
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllLines($HostsPath, [string[]]$clean, $encoding)
    return $backup
}

function Remove-BabelHosts([string]$HostsPath) {
    if (-not $HostsPath) { $HostsPath = Get-HostsPath }
    if (-not (Test-Path -LiteralPath $HostsPath)) { return 0 }
    $lines = @(Get-Content -LiteralPath $HostsPath -Encoding UTF8)
    $clean = [System.Collections.Generic.List[string]]::new()
    $removed = 0
    $inBlock = $false
    foreach ($line in $lines) {
        if ($line -match '^# ==== Batter Babel \S+ START') { $inBlock = $true; $removed++; continue }
        if ($line -match '^# ==== Batter Babel \S+ END')   { $inBlock = $false; $removed++; continue }
        if ($inBlock) { $removed++; continue }
        $clean.Add($line)
    }
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllLines($HostsPath, [string[]]$clean, $encoding)
    return $removed
}

# --- System tune snapshot (originals captured before the first change, used by restore-tune) ---
function Get-TuneStatePath {
    $dir = Join-Path $env:LOCALAPPDATA 'BatterBabel'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    return Join-Path $dir 'tune-state.json'
}

function Save-TuneState($State) {
    $path = Get-TuneStatePath
    $json = $State | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($path, $json, [System.Text.UTF8Encoding]::new($false))
    return $path
}

function Load-TuneState {
    $path = Get-TuneStatePath
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try {
        $json = [System.IO.File]::ReadAllText($path, [System.Text.UTF8Encoding]::new($false))
        if (-not $json.Trim()) { return $null }
        return ($json | ConvertFrom-Json)
    } catch { return $null }
}

function Get-WirelessPowerIndex([string]$Output, [string]$Kind) {
    # Built from code points, NOT literal characters: this file must stay pure ASCII, because
    # Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI and would mangle any non-ASCII byte.
    # "dang qian jiao liu" / "dang qian zhi liu" are the localised labels powercfg prints on a
    # Chinese Windows.
    $acLabel = "$([char]0x5F53)$([char]0x524D)$([char]0x4EA4)$([char]0x6D41)"
    $dcLabel = "$([char]0x5F53)$([char]0x524D)$([char]0x76F4)$([char]0x6D41)"
    if ($Kind -eq 'ac') {
        if ($Output -match "(?:Current AC|$acLabel)[^\r\n]*?0x([0-9a-fA-F]+)") { return $Matches[1] }
    } else {
        if ($Output -match "(?:Current DC|$dcLabel)[^\r\n]*?0x([0-9a-fA-F]+)") { return $Matches[1] }
    }
    return $null
}

if ($Action -eq 'probe') {
    $ranked = @(Get-RouteProbe | Sort-Object { $_.score -as [double] })
    $list = @(); $rank = 0
    foreach ($c in $ranked) {
        $rank++
        $list += [ordered]@{
            id = $c.id; name = $c.name; group = $c.group; host = $c.host; ip = $c.ip
            rank = $rank; icmp = $c.icmp; tcp = $c.tcp; score = $c.score
        }
    }
    Write-Result @{ ok = $true; candidates = $list; download = (Test-DownloadSpeed); testedAt = (Get-Date).ToString('o') }
    exit 0
}

if ($Action -eq 'cf-optimize') {
    $gameKey = if ($GameId) { $GameId } else { 'limbus' }
    $game = $null
    if ($Catalog.Contains($gameKey)) {
        $game = $Catalog[$gameKey]
    } elseif ($gameKey -match '^steam-(\d+)$') {
        # A catalogue title without its own profile: build one from its platform's endpoints.
        $appId = $Matches[1]
        $platformName = Get-PlatformForApp $appId
        $displayName = $OnlineGameIds[$appId]
        if (-not $displayName) { $displayName = "App $appId" }
        $game = @{
            Id = $gameKey; Name = $displayName; AppId = $appId
            OptGroups = @(Get-PlatformTargets $platformName)
        }
    }
    if (-not $game) { throw 'Unknown game' }
    if (@($game.OptGroups).Count -eq 0) {
        Write-Tick 100 'Nothing to rank'
        Write-Result @{ ok = $false; game = $gameKey; gameName = $game.Name; message = 'No optimisable endpoints are configured for this game.' }
        exit 0
    }
    $log = [System.Collections.Generic.List[string]]::new()
    $groups = @()
    $groupCount = [math]::Max(1, @($game.OptGroups).Count)
    $gi = 0

    Write-Tick 3 "Preparing $($game.Name)"
    foreach ($g in $game.OptGroups) {
        $start = 4 + [int](88 * $gi / $groupCount)
        $end = 4 + [int](88 * ($gi + 1) / $groupCount)
        Write-Tick $start "Starting $($g.Label)"
        $log.Add("=== $($g.Label) ===")
        $entry = [ordered]@{
            label = $g.Label; tag = $g.Tag; mode = $g.Mode; domains = @($g.Domains)
            ok = $false; summary = ''; items = @(); bestIp = $null; downloadMbps = $null; latency = $null
        }
        if ($g.Mode -eq 'cloudflare') {
            $cf = Get-CfOptimize -RangeStart $start -RangeEnd $end
            $entry.poolSize = $cf.poolSize
            $entry.aliveCount = $cf.aliveCount
            $entry.finalistCount = $cf.testedCandidates
            $entry.priorityCount = $cf.priorityCount
            $entry.learnedCount = $cf.learnedCount
            $entry.learnedIps = @($cf.learnedIps)
            $entry.demotedCount = $cf.demotedCount
            $entry.prunedCount = $cf.prunedCount
            $log.Add("pool: $($cf.poolSize) sampled, $($cf.aliveCount) reachable, $($cf.testedCandidates) download-tested")
            $log.Add("free pass: $($cf.priorityCount) IPs ($($cf.learnedCount) learned)")
            if ($cf.learnedCount -gt 0) {
                $log.Add("learned IPs: $((@($cf.learnedIps)) -join ', ')")
            }
            if ($cf.demotedCount -gt 0) {
                $log.Add("demoted (failing, back to normal screening): $((@($cf.demotedIps)) -join ', ')")
            }
            if ($cf.prunedCount -gt 0) {
                $log.Add("pruned $($cf.prunedCount) dead IP(s) from the learning library")
            }
            if ($cf.bestIp) {
                $entry.ok = $true
                $entry.bestIp = $cf.bestIp
                $entry.downloadMbps = $cf.bestDownloadMbps
                $entry.latency = $cf.bestLatency
                $entry.summary = "$($cf.bestIp)  ($($cf.bestDownloadMbps) MB/s, $($cf.bestLatency) ms)"
                $log.Add("best IP: $($cf.bestIp) ($($cf.bestDownloadMbps) MB/s, $($cf.bestLatency) ms)")
            } else {
                $entry.summary = 'no usable IP'
                $log.Add('no usable IP')
            }
        } else {
            $items = @()
            # NOTE: do not name this $domains -- PowerShell variables are case-insensitive and the
            # script has a strongly typed [string]$Domains parameter, which would coerce the array
            # into one space-joined string.
            $domainList = @($g.Domains)
            $domainCount = [math]::Max(1, $domainList.Count)
            $di = 0
            foreach ($domain in $domainList) {
                Write-Tick ($start + [int](($end - $start) * $di / $domainCount)) "Resolving $domain"
                $candidates = @(Get-CloudFrontCandidates $domain)
                $bestIp = $null; $bestLatency = $null
                $candCount = [math]::Max(1, $candidates.Count)
                $ci = 0
                foreach ($ip in $candidates) {
                    $ci++
                    $frac = ($di + ($ci / [double]$candCount)) / [double]$domainCount
                    Write-Tick ($start + [int](($end - $start) * $frac)) "Probing $domain ($ci/$($candidates.Count))"
                    $latency = Test-CloudFrontLatency $domain $ip
                    if ($latency -ne $null -and ($bestLatency -eq $null -or $latency -lt $bestLatency)) {
                        $bestIp = $ip; $bestLatency = $latency
                    }
                }
                $di++
                $items += [ordered]@{ domain = $domain; ip = $bestIp; latency = $bestLatency; ok = ($bestIp -ne $null) }
                if ($bestIp) { $log.Add("$domain -> $bestIp ($bestLatency ms)") } else { $log.Add("$domain -> fallback DNS") }
            }
            $entry.items = $items
            $entry.ok = (@($items | Where-Object { $_.ok }).Count -gt 0)
            $entry.summary = (@($items | ForEach-Object {
                if ($_.ok) { "$($_.domain) -> $($_.ip) ($($_.latency) ms)" } else { "$($_.domain) -> fallback DNS" }
            }) -join ' | ')
        }
        $groups += $entry
        $gi++
    }

    Write-Tick 100 'Done'
    $log.Add('=== done ===')
    Write-Result @{ ok = $true; game = $gameKey; gameName = $game.Name; groups = $groups; log = @($log); hostsPath = (Get-HostsPath) }
    exit 0
}

# ===============================================================================================
# Action dispatch. The UI uses: scan, cf-optimize, cf-apply, cf-restore, optimize (QoS boost),
# restore (remove boost, with or without -GameId), tune-system, restore-tune.
#
# `status`, `probe`, `boost` and `apply-route` are LEGACY actions from the earlier UI: they are not
# reachable from the current frontend and are kept only so the code (and the $RouteCatalog /
# Resolve-Ip helpers they use) stays available for future work. They are safe to ignore.
# ===============================================================================================

if ($Action -eq 'scan' -or $Action -eq 'scan-force') {
    # Scanning costs about a second (Steam libraries + the appinfo.vdf category pass), which is
    # pointless to repeat on every launch. The result is cached and reused for 24 hours; the UI's
    # "detect games" button calls scan-force to bypass the cache. Anything that can change on its own --
    # the boost state -- is recomputed on every read instead of being taken from the cache.
    $useCache = ($Action -eq 'scan')
    if ($useCache) {
        $cached = Read-GameCache
        if ($cached) {
            $cachedGames = @($cached.games)
            foreach ($cg in $cachedGames) {
                if ($cg.id) { $cg.accelerated = Test-GameBoosted $cg.id }
            }
            Write-Result @{
                ok = $true
                games = $cachedGames
                qos = $cached.qos
                cached = $true
                scannedAt = $cached.savedAt
                protectedRoute = 'Local Windows QoS only. No proxy nodes, hosts changes, or DNS takeover.'
            }
            exit 0
        }
    }
    $freshGames = @(Get-OnlineGameList)
    $freshQos = Get-QosCapability
    Write-GameCache $freshGames $freshQos
    Write-Result @{
        ok = $true
        games = $freshGames
        qos = $freshQos
        cached = $false
        scannedAt = (Get-Date).ToString('o')
        protectedRoute = 'Local Windows QoS only. No proxy nodes, hosts changes, or DNS takeover.'
    }
    exit 0
}

if ($Action -eq 'status') {
    Write-Result @{ ok = $true; elevated = (Test-Administrator); activePolicies = @(Get-PolicyInfo); policyMode = 'DSCP 46 / Windows QoS' }
    exit 0
}

if ($Action -eq 'boost') {
    if (-not $GameId) { throw 'Select a game first' }
    $game = $Catalog[$GameId]
    $processes = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($game.Executable)) -ErrorAction SilentlyContinue)
    if ($processes.Count -eq 0) { Write-Result @{ ok = $false; message = "$($game.Name) is not running" }; exit 0 }
    $count = 0
    foreach ($process in $processes) { try { $process.PriorityClass = 'AboveNormal'; $count++ } catch {} }
    Write-Result @{ ok = ($count -gt 0); message = "Raised scheduling priority for $count $($game.Name) process(es)" }
    exit 0
}

if (($Action -in @('optimize', 'restore', 'apply-route', 'cf-apply', 'cf-restore', 'tune-system', 'restore-tune', 'reapply-boost')) -and -not (Test-Administrator)) {
    $argLine = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Action $Action"
    if ($GameId) { $argLine += " -GameId $GameId" }
    if ($Target) { $argLine += " -Target $Target" }
    if ($Domains) { $argLine += " -Domains `"$Domains`"" }
    if ($ProgressFile) { $argLine += " -ProgressFile `"$ProgressFile`"" }
    if ($ResultFile) { $argLine += " -ResultFile `"$ResultFile`"" }
    try {
        $elevated = Start-Process -FilePath 'powershell.exe' -ArgumentList $argLine -Verb RunAs -Wait -PassThru -WindowStyle Hidden
        if ($elevated.ExitCode -ne 0) {
            # Do not clobber a result the child already produced: only report a generic failure when
            # the result file is missing or empty. Otherwise the child's own (more useful) message
            # would be overwritten by a misleading "approval was cancelled".
            $existing = ''
            if ($ResultFile -and (Test-Path -LiteralPath $ResultFile)) {
                $existing = [System.IO.File]::ReadAllText($ResultFile, [System.Text.UTF8Encoding]::new($false))
            }
            if (-not $existing -or -not $existing.Trim()) {
                Write-Result @{ ok = $false; message = "The elevated task failed (exit code $($elevated.ExitCode))."; accelerated = $false }
            }
        }
    } catch {
        Write-Result @{ ok = $false; message = 'Administrator approval was cancelled'; accelerated = $false }
    }
    exit 0
}

if ($Action -eq 'restore') {
    if ($GameId) {
        # Cancel acceleration for one game only (the toggle button path).
        Write-Tick 40 'Removing QoS rule'
        $policyName = "BatterBabel-$GameId"
        Remove-NetQosPolicy -Name $policyName -PolicyStore Local -Confirm:$false -ErrorAction SilentlyContinue
        Remove-NetQosPolicy -Name $policyName -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue
        Remove-BoostState $GameId
        # Nothing left to re-apply? Then the logon task has no reason to exist.
        if (-not (Test-HasActiveStoreBoost)) { Unregister-BoostReapplyTask | Out-Null }
        $label = $GameId
        if ($Catalog.Contains($GameId)) { $label = $Catalog[$GameId].Name }
        Write-Tick 100 'Boost disabled'
        Write-Result @{ ok = $true; message = "Boost removed for $label."; policy = $policyName; accelerated = $false }
        exit 0
    }
    Write-Tick 40 'Removing QoS rules'
    $existing = @(Get-PolicyInfo)
    foreach ($name in $existing) {
        Remove-NetQosPolicy -Name $name -PolicyStore Local -Confirm:$false -ErrorAction SilentlyContinue
        Remove-NetQosPolicy -Name $name -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue
    }
    Remove-BoostState $null
    Unregister-BoostReapplyTask | Out-Null
    Write-Tick 100 'Boost disabled'
    Write-Result @{ ok = $true; message = "Removed $($existing.Count) Batter Babel QoS rule(s). Hosts and DNS were never changed."; accelerated = $false }
    exit 0
}

if ($Action -eq 'apply-route') {
    if (-not $Target -or -not $RouteCatalog.Contains($Target)) { throw 'Select a route first' }
    $c = $RouteCatalog[$Target]
    $ip = Resolve-Ip $c.Host
    if (-not $ip) {
        Write-Result @{ ok = $false; message = "Could not resolve $($c.Host)" }
        exit 0
    }
    $policyName = "BatterBabel-route-$Target"
    Remove-NetQosPolicy -Name $policyName -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue
    New-NetQosPolicy -Name $policyName -IPDstPrefixMatchCondition "$ip/32" -DSCPAction 46 -NetworkProfile All -PolicyStore ActiveStore | Out-Null
    Write-Result @{ ok = $true; message = "QoS applied to $($c.Name) ($ip/32). DSCP 46 is marked for traffic to this address only; hosts, DNS and proxies are never modified."; policy = $policyName; ip = $ip }
    exit 0
}

if ($Action -eq 'cf-apply') {
    # domains = "tag|ip|domain;tag|ip|domain;..." -- one triple per hosts entry, semicolon separated.
    Write-Tick 25 'Validating mappings'
    $byTag = [ordered]@{}
    $rejected = 0
    if ($Domains) {
        foreach ($entry in ($Domains -split ';')) {
            $parts = ($entry.Trim()) -split '\|'
            if ($parts.Count -ne 3) { $rejected++; continue }
            $tag = $parts[0].Trim(); $ip = $parts[1].Trim(); $domain = $parts[2].Trim()
            if ($tag -notmatch '^[A-Z0-9]{2,12}$') { $rejected++; continue }
            if ($ip -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { $rejected++; continue }
            if ($domain -notmatch '^[a-zA-Z0-9][a-zA-Z0-9.-]*\.[a-zA-Z]{2,}$') { $rejected++; continue }
            # The CF block may only ever hold official Cloudflare addresses: that domain set is
            # Cloudflare-fronted, so anything else would point it at an unrelated host. (This check
            # existed as Test-IpInCloudflare but was never actually wired in.)
            if ($tag -eq 'CF' -and -not (Test-IpInCloudflare $ip)) { $rejected++; continue }
            if (-not $byTag.Contains($tag)) { $byTag[$tag] = [System.Collections.Generic.List[string]]::new() }
            $byTag[$tag].Add("$ip $domain")
        }
    }
    if ($byTag.Count -eq 0) {
        Write-Tick 100 'Nothing to write'
        $msg = 'No mappings to write'
        if ($rejected -gt 0) { $msg = "No valid mappings to write ($rejected rejected)." }
        Write-Result @{ ok = $false; message = $msg; rejected = $rejected }
        exit 0
    }
    $backup = Set-BabelHosts $byTag
    $total = 0; foreach ($k in $byTag.Keys) { $total += $byTag[$k].Count }
    Write-Tick 100 'Hosts updated'
    Write-Result @{ ok = $true; message = "Wrote $total hosts mapping(s) (backup: $backup)."; backup = $backup; rejected = $rejected }
    exit 0
}

if ($Action -eq 'cf-restore') {
    Write-Tick 30 'Restoring hosts'
    $removed = Remove-BabelHosts
    Write-Tick 100 'Hosts restored'
    Write-Result @{ ok = $true; message = "Restored, removed $removed host line(s)." }
    exit 0
}

if ($Action -eq 'tune-system') {
    $log = [System.Collections.Generic.List[string]]::new()
    $items = @()
    Write-Tick 2 'Snapshotting current settings'

    # --- 0) Snapshot the originals once (needed by restore-tune; never overwritten) ---
    $statePath = Get-TuneStatePath
    if (Test-Path -LiteralPath $statePath) {
        $log.Add("Originals snapshot already exists: $statePath")
    } else {
        try {
            $snap = [ordered]@{
                savedAt = (Get-Date).ToString('o')
                nagle = [ordered]@{}
                tcpGlobal = [ordered]@{}
                adapterProps = [ordered]@{}
                adapterPower = [ordered]@{}
                throttle = [ordered]@{}
                wifiPower = [ordered]@{}
            }
            foreach ($adapter in @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue)) {
                $guid = $adapter.InterfaceGuid
                if (-not $guid) { continue }
                $ifPath = "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$guid"
                if (-not (Test-Path -LiteralPath $ifPath)) { continue }
                $entry = [ordered]@{}
                foreach ($n in @('TcpAckFrequency', 'TCPNoDelay', 'TcpDelAckTicks')) {
                    try { $entry[$n] = (Get-ItemProperty -Path $ifPath -Name $n -ErrorAction Stop).$n } catch { $entry[$n] = $null }
                }
                $snap.nagle[$guid] = $entry
            }
            $showGlobal = (& netsh int tcp show global 2>&1 | Out-String)
            $snap.tcpGlobal['autotuninglevel'] = if ($showGlobal -match 'Auto-Tuning Level\s*:\s*(\S+)') { $Matches[1] } else { 'normal' }
            $snap.tcpGlobal['rss'] = if ($showGlobal -match 'Receive-Side Scaling State\s*:\s*(\S+)') { $Matches[1] } else { 'enabled' }
            foreach ($a in @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue)) {
                $props = [ordered]@{}
                foreach ($kw in @('*InterruptModeration', '*EEE', '*FlowControl')) {
                    try {
                        $p = Get-NetAdapterAdvancedProperty -Name $a.Name -RegistryKeyword $kw -ErrorAction Stop
                        $props[$kw] = "$(@($p.RegistryValue)[0])"
                    } catch { $props[$kw] = $null }
                }
                $snap.adapterProps[$a.Name] = $props
                try {
                    $pm = Get-NetAdapterPowerManagement -Name $a.Name -ErrorAction Stop
                    $snap.adapterPower[$a.Name] = "$($pm.AllowComputerToTurnOffDevice)"
                } catch { $snap.adapterPower[$a.Name] = $null }
            }
            $mmPath = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'
            try { $snap.throttle['NetworkThrottlingIndex'] = (Get-ItemProperty -Path $mmPath -Name 'NetworkThrottlingIndex' -ErrorAction Stop).NetworkThrottlingIndex } catch { $snap.throttle['NetworkThrottlingIndex'] = $null }
            try { $snap.throttle['SystemResponsiveness'] = (Get-ItemProperty -Path $mmPath -Name 'SystemResponsiveness' -ErrorAction Stop).SystemResponsiveness } catch { $snap.throttle['SystemResponsiveness'] = $null }
            $powerQuery = (& powercfg /query SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 2>&1 | Out-String)
            $snap.wifiPower['ac'] = Get-WirelessPowerIndex $powerQuery 'ac'
            $snap.wifiPower['dc'] = Get-WirelessPowerIndex $powerQuery 'dc'
            Save-TuneState $snap | Out-Null
            $log.Add("Originals snapshot saved: $statePath")
        } catch {
            $log.Add("Snapshot failed: $($_.Exception.Message)")
        }
    }

    # --- 1) TCP low latency: disable Nagle/delayed-ACK per interface ---
    # Benefits every TCP-based online game (small packets are sent immediately).
    Write-Tick 10 'TCP low latency (Nagle)'
    $nagleItem = [ordered]@{ id = 'tcp-nagle'; title = 'TCP low latency (Nagle / delayed ACK off)'; ok = $false; detail = '' }
    try {
        $applied = 0
        foreach ($adapter in @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue)) {
            $guid = $adapter.InterfaceGuid
            if (-not $guid) { continue }
            $ifPath = "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$guid"
            if (-not (Test-Path -LiteralPath $ifPath)) { continue }
            try {
                Set-ItemProperty -Path $ifPath -Name 'TcpAckFrequency' -Value 1 -Type DWord -ErrorAction Stop
                Set-ItemProperty -Path $ifPath -Name 'TCPNoDelay' -Value 1 -Type DWord -ErrorAction Stop
                Set-ItemProperty -Path $ifPath -Name 'TcpDelAckTicks' -Value 0 -Type DWord -ErrorAction Stop
                $applied++
            } catch {}
        }
        if ($applied -gt 0) {
            $nagleItem.ok = $true
            $nagleItem.detail = "applied to $applied interface(s)"
            $log.Add("Nagle/delayed-ACK disabled on $applied interface(s).")
        } else {
            $nagleItem.detail = 'no writable interface found (needs admin)'
            $log.Add('Nagle: no writable interface found.')
        }
    } catch {
        $nagleItem.detail = "failed: $($_.Exception.Message)"
        $log.Add("Nagle failed: $($_.Exception.Message)")
    }
    $items += $nagleItem

    # --- 2) TCP global tuning ---
    Write-Tick 24 'TCP global tuning'
    $tcpItem = [ordered]@{ id = 'tcp-global'; title = 'TCP global tuning'; ok = $false; detail = '' }
    try {
        $notes = @()
        & netsh int tcp set global autotuninglevel=normal 2>&1 | Out-Null
        $notes += 'autotuning=normal'
        & netsh int tcp set heuristics disabled 2>&1 | Out-Null
        $notes += 'heuristics=disabled'
        & netsh int tcp set global rss=enabled 2>&1 | Out-Null
        $notes += 'rss=enabled'
        $tcpItem.ok = $true
        $tcpItem.detail = ($notes -join ', ')
        $log.Add("TCP globals: $($notes -join ', ')")
    } catch {
        $tcpItem.detail = "failed: $($_.Exception.Message)"
        $log.Add("TCP globals failed: $($_.Exception.Message)")
    }
    $items += $tcpItem

    # --- 3) Adapter low-latency properties ---
    Write-Tick 36 'Adapter low-latency properties'
    $adapterItem = [ordered]@{ id = 'adapter'; title = 'Adapter low-latency properties'; ok = $false; detail = ''; applied = @() }
    try {
        $applied = @()
        # Registry values are numeric and display names are localised, so "off" is taken as the
        # smallest valid registry value (0 for all of these on real drivers).
        $targets = @(
            @{ kw = '*InterruptModeration'; label = 'interrupt moderation off' },
            @{ kw = '*EEE';                 label = 'energy-efficient ethernet off' },
            @{ kw = '*FlowControl';         label = 'flow control off' }
        )
        foreach ($a in @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue)) {
            foreach ($t in $targets) {
                try {
                    $prop = Get-NetAdapterAdvancedProperty -Name $a.Name -RegistryKeyword $t.kw -ErrorAction SilentlyContinue
                    if (-not $prop) { continue }
                    $valid = @($prop.ValidRegistryValues)
                    if ($valid.Count -eq 0) { continue }
                    $off = @($valid | Sort-Object { [int]$_ })[0]
                    $current = @($prop.RegistryValue)[0]
                    if ("$current" -ne "$off") {
                        Set-NetAdapterAdvancedProperty -Name $a.Name -RegistryKeyword $t.kw -RegistryValue "$off" -ErrorAction Stop
                        $applied += "$($a.Name): $($t.label)"
                    }
                } catch {}
            }
            try {
                Set-NetAdapterPowerManagement -Name $a.Name -AllowComputerToTurnOffDevice Disabled -ErrorAction Stop
                $applied += "$($a.Name): device power saving off"
            } catch {}
        }
        $adapterItem.ok = $true
        if ($applied.Count -gt 0) {
            $adapterItem.applied = $applied
            $adapterItem.detail = "$($applied.Count) setting(s) changed"
        } else {
            $adapterItem.detail = 'already optimal or driver does not expose these properties'
        }
        $log.Add("Adapter properties: $($adapterItem.detail)")
    } catch {
        $adapterItem.detail = "failed: $($_.Exception.Message)"
        $log.Add("Adapter failed: $($_.Exception.Message)")
    }
    $items += $adapterItem

    # --- 4) Windows multimedia network throttling ---
    Write-Tick 58 'Windows network throttling'
    $throttleItem = [ordered]@{ id = 'throttle'; title = 'Windows network throttling'; ok = $false; detail = '' }
    try {
        $regPath = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'
        $old = $null
        try { $old = (Get-ItemProperty -Path $regPath -Name 'NetworkThrottlingIndex' -ErrorAction Stop).NetworkThrottlingIndex } catch {}
        Set-ItemProperty -Path $regPath -Name 'NetworkThrottlingIndex' -Value 0xFFFFFFFF -Type DWord -ErrorAction Stop
        Set-ItemProperty -Path $regPath -Name 'SystemResponsiveness' -Value 10 -Type DWord -ErrorAction Stop
        $throttleItem.ok = $true
        $throttleItem.detail = "NetworkThrottlingIndex disabled (was $old), SystemResponsiveness=10"
        $log.Add("Network throttling: $($throttleItem.detail)")
    } catch {
        $throttleItem.detail = "failed: $($_.Exception.Message)"
        $log.Add("Network throttling failed: $($_.Exception.Message)")
    }
    $items += $throttleItem

    # --- 2) Wi-Fi adapter power management -> maximum performance ---
    Write-Tick 70 'Wi-Fi adapter power mode'
    $wifiItem = [ordered]@{ id = 'wifi-power'; title = 'Wi-Fi adapter power mode'; ok = $false; detail = '' }
    try {
        $adapters = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.MediaType -eq 'Native 802.11' })
        if ($adapters.Count -eq 0) {
            $wifiItem.detail = 'no Wi-Fi adapter found'
            $log.Add('No Wi-Fi adapter found, skipped.')
        } else {
            $names = @()
            foreach ($a in $adapters) {
                try { Set-NetAdapterPowerManagement -Name $a.Name -AllowComputerToTurnOffDevice Disabled -ErrorAction Stop; $names += $a.Name } catch {}
            }
            # power scheme: wireless adapter power saving mode -> maximum performance
            & powercfg /setacvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0 2>$null | Out-Null
            & powercfg /setdcvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0 2>$null | Out-Null
            & powercfg /setactive SCHEME_CURRENT 2>$null | Out-Null
            if ($names.Count -gt 0) {
                $wifiItem.ok = $true
                $wifiItem.detail = "set to maximum performance: $($names -join ', ')"
                $log.Add("Wi-Fi power set to maximum performance: $($names -join ', ')")
            } else {
                $wifiItem.detail = 'power scheme updated, adapter property needs admin'
                $log.Add('Wi-Fi power scheme updated (adapter property needs admin).')
            }
        }
    } catch {
        $wifiItem.detail = "failed: $($_.Exception.Message)"
        $log.Add("Wi-Fi power failed: $($_.Exception.Message)")
    }
    $items += $wifiItem

    # --- 3) Background bandwidth hogs ---
    Write-Tick 80 'Background bandwidth check'
    $hogItem = [ordered]@{ id = 'bg-programs'; title = 'Background bandwidth check'; ok = $true; detail = ''; found = @() }
    $hogNames = @{
        'BaiduNetdisk' = 'Baidu Netdisk'; 'BaiduNetdiskHost' = 'Baidu Netdisk'
        'Thunder' = 'Thunder'; 'ThunderPlatform' = 'Thunder'; 'XLServicePlatform' = 'Thunder'; 'xunlei' = 'Thunder'
        'QQDownload' = 'QQ Download'; 'IDMan' = 'Internet Download Manager'
        'aria2c' = 'aria2'; 'qbittorrent' = 'qBittorrent'; 'utorrent' = 'uTorrent'; 'BitTorrent' = 'BitTorrent'
        'iQIYI' = 'iQIYI'; 'QQLive' = 'Tencent Video'; 'YoukuDesktop' = 'Youku'; 'PPTV' = 'PPTV'
        'bilibili' = 'Bilibili'; 'BilibiliLive' = 'Bilibili Live'
        'KuGou' = 'KuGou'; 'QQMusic' = 'QQ Music'; 'CloudMusic' = 'NetEase Music'
    }
    $found = @()
    foreach ($p in @(Get-Process -ErrorAction SilentlyContinue)) {
        if ($hogNames.ContainsKey($p.ProcessName)) {
            $label = $hogNames[$p.ProcessName]
            if ($found -notcontains $label) { $found += $label }
        }
    }
    if ($found.Count -gt 0) {
        $hogItem.ok = $false
        $hogItem.found = $found
        $hogItem.detail = "found: $($found -join ', ')"
        $log.Add("Bandwidth hogs found: $($found -join ', ')")
    } else {
        $hogItem.detail = 'none found'
        $log.Add('No background bandwidth hog found.')
    }
    $items += $hogItem

    # --- 7) Game-specific extra: CS2 autoexec.cfg (only when CS2 is installed) ---
    $cs2 = $Catalog['cs2']
    $cs2Loc = Find-Game $cs2
    if ($cs2Loc.Installed -and $cs2Loc.ExecutablePath) {
        Write-Tick 90 'CS2 autoexec.cfg'
        $cs2Item = [ordered]@{ id = 'cs2-cfg'; title = 'CS2 autoexec.cfg (game-specific)'; ok = $false; detail = '' }
        try {
            $root = Split-Path (Split-Path (Split-Path (Split-Path $cs2Loc.ExecutablePath -Parent) -Parent) -Parent) -Parent
            $cfgDir = Join-Path $root 'game\csgo\cfg'
            if (Test-Path -LiteralPath $cfgDir) {
                $cfgPath = Join-Path $cfgDir 'autoexec.cfg'
                if (Test-Path -LiteralPath $cfgPath) { Copy-Item -LiteralPath $cfgPath -Destination "$cfgPath.batterbabel.bak" -Force }
                $content = @(
                    '// Batter Babel network tuning (generated)',
                    'rate 196608',
                    'cl_interp 0.031',
                    'cl_interp_ratio 2',
                    'cl_net_buffer_ticks 64',
                    'net_graph 1',
                    'cl_allow_animated_avatars false'
                )
                [System.IO.File]::WriteAllLines($cfgPath, [string[]]$content, [System.Text.UTF8Encoding]::new($false))
                $cs2Item.ok = $true
                $cs2Item.detail = "written: $cfgPath"
                $log.Add("CS2 autoexec.cfg written: $cfgPath")
            } else {
                $cs2Item.detail = "cfg dir not found: $cfgDir"
                $log.Add("CS2 cfg dir not found: $cfgDir")
            }
        } catch {
            $cs2Item.detail = "failed: $($_.Exception.Message)"
            $log.Add("CS2 cfg failed: $($_.Exception.Message)")
        }
        $items += $cs2Item
    }

    $log.Add('=== system tune done ===')
    Write-Tick 100 'System tune complete'
    Write-Result @{ ok = $true; items = $items; log = @($log) }
    exit 0
}

if ($Action -eq 'restore-tune') {
    $log = [System.Collections.Generic.List[string]]::new()
    $items = @()
    Write-Tick 2 'Loading snapshot'
    $state = Load-TuneState
    if ($null -eq $state) {
        Write-Result @{ ok = $false; message = 'No snapshot found. Run the system tuning first.'; items = @(); log = @('No snapshot file found.') }
        exit 0
    }

    # 1) Nagle / delayed-ACK
    Write-Tick 10 'Restoring TCP low latency'
    $nagleItem = [ordered]@{ id = 'tcp-nagle'; title = 'TCP low latency (Nagle) restored'; ok = $false; detail = '' }
    try {
        $restored = 0
        foreach ($prop in @($state.nagle.PSObject.Properties)) {
            $ifPath = "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$($prop.Name)"
            if (-not (Test-Path -LiteralPath $ifPath)) { continue }
            foreach ($n in @('TcpAckFrequency', 'TCPNoDelay', 'TcpDelAckTicks')) {
                $old = $prop.Value.$n
                try {
                    if ($null -eq $old -or "$old" -eq '') {
                        Remove-ItemProperty -Path $ifPath -Name $n -ErrorAction SilentlyContinue
                    } else {
                        Set-ItemProperty -Path $ifPath -Name $n -Value ([int]$old) -Type DWord -ErrorAction Stop
                    }
                    $restored++
                } catch {}
            }
        }
        $nagleItem.ok = $true
        $nagleItem.detail = "restored $restored value(s)"
        $log.Add("Nagle settings restored ($restored).")
    } catch {
        $nagleItem.detail = "failed: $($_.Exception.Message)"
        $log.Add("Nagle restore failed: $($_.Exception.Message)")
    }
    $items += $nagleItem

    # 2) TCP globals
    Write-Tick 24 'Restoring TCP globals'
    $tcpItem = [ordered]@{ id = 'tcp-global'; title = 'TCP global tuning restored'; ok = $false; detail = '' }
    try {
        $notes = @()
        $at = $state.tcpGlobal.autotuninglevel
        if ($null -ne $at -and "$at" -ne '') { & netsh int tcp set global "autotuninglevel=$at" 2>&1 | Out-Null; $notes += "autotuning=$at" }
        $rss = $state.tcpGlobal.rss
        if ($null -ne $rss -and "$rss" -ne '') { & netsh int tcp set global "rss=$rss" 2>&1 | Out-Null; $notes += "rss=$rss" }
        $tcpItem.ok = $true
        $tcpItem.detail = if ($notes.Count -gt 0) { ($notes -join ', ') } else { 'nothing to restore' }
        $log.Add("TCP globals restored: $($tcpItem.detail)")
    } catch {
        $tcpItem.detail = "failed: $($_.Exception.Message)"
        $log.Add("TCP globals restore failed: $($_.Exception.Message)")
    }
    $items += $tcpItem

    # 3) Adapter advanced properties
    Write-Tick 36 'Restoring adapter properties'
    $adapterItem = [ordered]@{ id = 'adapter'; title = 'Adapter properties restored'; ok = $false; detail = '' }
    try {
        $restored = @()
        foreach ($prop in @($state.adapterProps.PSObject.Properties)) {
            foreach ($kw in @('*InterruptModeration', '*EEE', '*FlowControl')) {
                $old = $prop.Value.$kw
                if ($null -eq $old -or "$old" -eq '') { continue }
                try {
                    Set-NetAdapterAdvancedProperty -Name $prop.Name -RegistryKeyword $kw -RegistryValue "$old" -ErrorAction Stop
                    $restored += "$($prop.Name)/$kw"
                } catch {}
            }
        }
        $adapterItem.ok = $true
        $adapterItem.detail = if ($restored.Count -gt 0) { "restored $($restored.Count) setting(s)" } else { 'nothing to restore' }
        $log.Add("Adapter properties restored ($($restored.Count)).")
    } catch {
        $adapterItem.detail = "failed: $($_.Exception.Message)"
        $log.Add("Adapter restore failed: $($_.Exception.Message)")
    }
    $items += $adapterItem

    # 4) Adapter power management
    Write-Tick 50 'Restoring adapter power management'
    $powerItem = [ordered]@{ id = 'adapter-power'; title = 'Adapter power management restored'; ok = $false; detail = '' }
    try {
        $restored = @()
        foreach ($prop in @($state.adapterPower.PSObject.Properties)) {
            $old = $prop.Value
            if ($null -eq $old -or "$old" -eq '') { continue }
            try {
                Set-NetAdapterPowerManagement -Name $prop.Name -AllowComputerToTurnOffDevice "$old" -ErrorAction Stop
                $restored += $prop.Name
            } catch {}
        }
        $powerItem.ok = $true
        $powerItem.detail = if ($restored.Count -gt 0) { "restored: $($restored -join ', ')" } else { 'nothing to restore' }
        $log.Add("Adapter power restored ($($restored.Count)).")
    } catch {
        $powerItem.detail = "failed: $($_.Exception.Message)"
        $log.Add("Adapter power restore failed: $($_.Exception.Message)")
    }
    $items += $powerItem

    # 5) Multimedia network throttling
    Write-Tick 62 'Restoring network throttling'
    $throttleItem = [ordered]@{ id = 'throttle'; title = 'Windows network throttling restored'; ok = $false; detail = '' }
    try {
        $mmPath = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'
        $notes = @()
        $nti = $state.throttle.NetworkThrottlingIndex
        if ($null -ne $nti -and "$nti" -ne '') {
            Set-ItemProperty -Path $mmPath -Name 'NetworkThrottlingIndex' -Value ([int]$nti) -Type DWord -ErrorAction Stop
            $notes += "NetworkThrottlingIndex=$nti"
        }
        $sr = $state.throttle.SystemResponsiveness
        if ($null -ne $sr -and "$sr" -ne '') {
            Set-ItemProperty -Path $mmPath -Name 'SystemResponsiveness' -Value ([int]$sr) -Type DWord -ErrorAction Stop
            $notes += "SystemResponsiveness=$sr"
        }
        $throttleItem.ok = $true
        $throttleItem.detail = if ($notes.Count -gt 0) { ($notes -join ', ') } else { 'nothing to restore' }
        $log.Add("Throttling restored: $($throttleItem.detail)")
    } catch {
        $throttleItem.detail = "failed: $($_.Exception.Message)"
        $log.Add("Throttling restore failed: $($_.Exception.Message)")
    }
    $items += $throttleItem

    # 6) Wireless power saving mode
    Write-Tick 74 'Restoring Wi-Fi power mode'
    $wifiItem = [ordered]@{ id = 'wifi-power'; title = 'Wi-Fi power mode restored'; ok = $false; detail = '' }
    try {
        $ac = $state.wifiPower.ac
        $dc = $state.wifiPower.dc
        $adapters = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.MediaType -eq 'Native 802.11' })
        if ($adapters.Count -eq 0) {
            $wifiItem.ok = $true
            $wifiItem.detail = 'no Wi-Fi adapter found'
            $log.Add('Wi-Fi restore: no adapter found.')
        } else {
            if ($null -ne $ac -and "$ac" -ne '') { & powercfg /setacvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a "0x$ac" 2>&1 | Out-Null }
            if ($null -ne $dc -and "$dc" -ne '') { & powercfg /setdcvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a "0x$dc" 2>&1 | Out-Null }
            & powercfg /setactive SCHEME_CURRENT 2>&1 | Out-Null
            $wifiItem.ok = $true
            $wifiItem.detail = "AC=0x$ac, DC=0x$dc"
            $log.Add("Wi-Fi power restored (AC=0x$ac, DC=0x$dc).")
        }
    } catch {
        $wifiItem.detail = "failed: $($_.Exception.Message)"
        $log.Add("Wi-Fi restore failed: $($_.Exception.Message)")
    }
    $items += $wifiItem

    # 7) CS2 autoexec.cfg (only when CS2 is installed)
    $cs2Loc = Find-Game $Catalog['cs2']
    if ($cs2Loc.Installed -and $cs2Loc.ExecutablePath) {
        Write-Tick 86 'Restoring CS2 autoexec.cfg'
        $cs2Item = [ordered]@{ id = 'cs2-cfg'; title = 'CS2 autoexec.cfg restored'; ok = $false; detail = '' }
        try {
            $root = Split-Path (Split-Path (Split-Path (Split-Path $cs2Loc.ExecutablePath -Parent) -Parent) -Parent) -Parent
            $cfgPath = Join-Path $root 'game\csgo\cfg\autoexec.cfg'
            $bakPath = "$cfgPath.batterbabel.bak"
            if (Test-Path -LiteralPath $bakPath) {
                Copy-Item -LiteralPath $bakPath -Destination $cfgPath -Force
                Remove-Item -LiteralPath $bakPath -Force -ErrorAction SilentlyContinue
                $cs2Item.ok = $true
                $cs2Item.detail = 'restored from backup'
                $log.Add('CS2 autoexec.cfg restored from backup.')
            } elseif (Test-Path -LiteralPath $cfgPath) {
                $content = [System.IO.File]::ReadAllText($cfgPath)
                if ($content -match 'Batter Babel') {
                    Remove-Item -LiteralPath $cfgPath -Force
                    $cs2Item.ok = $true
                    $cs2Item.detail = 'generated file removed'
                    $log.Add('CS2 autoexec.cfg (generated) removed.')
                } else {
                    $cs2Item.ok = $true
                    $cs2Item.detail = 'no Batter Babel backup, file left untouched'
                    $log.Add('CS2 autoexec.cfg: nothing to restore.')
                }
            } else {
                $cs2Item.ok = $true
                $cs2Item.detail = 'nothing to restore'
                $log.Add('CS2 autoexec.cfg: nothing to restore.')
            }
        } catch {
            $cs2Item.detail = "failed: $($_.Exception.Message)"
            $log.Add("CS2 restore failed: $($_.Exception.Message)")
        }
        $items += $cs2Item
    }

    # The snapshot is consumed on restore so the next tune captures fresh originals.
    try { Remove-Item -LiteralPath (Get-TuneStatePath) -Force -ErrorAction SilentlyContinue } catch {}
    $log.Add('Snapshot cleared.')
    $log.Add('=== system restore done ===')
    Write-Tick 100 'System restore complete'
    Write-Result @{ ok = $true; items = $items; log = @($log) }
    exit 0
}

if ($Action -eq 'optimize') {
    if (-not $GameId) { throw 'Select a game first' }
    if (-not $Catalog.Contains($GameId)) { throw "Unknown game: $GameId" }
    Write-Tick 15 'Locating the game'
    $game = $Catalog[$GameId]
    $location = Find-Game $game
    if (-not $location.Installed -or -not $location.ExecutablePath) {
        Write-Result @{ ok = $false; message = "$($game.Name) was not found. Install it in Steam or EA app, then scan again."; accelerated = $false }
        exit 0
    }
    Write-Tick 55 'Creating the DSCP 46 rule'
    $policyName = "BatterBabel-$($game.Id)"
    Remove-NetQosPolicy -Name $policyName -PolicyStore Local -Confirm:$false -ErrorAction SilentlyContinue
    Remove-NetQosPolicy -Name $policyName -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue

    # Two policy stores, tried in order:
    #   Local       -- backed by Group Policy. Persists across reboots, but does NOT EXIST on
    #                  Windows Home editions (no Group Policy feature at all), where every call
    #                  fails with "The network path was not found" (System Error 53).
    #   ActiveStore -- the live in-memory store. Works on every edition, but is cleared on reboot.
    # So Home users get a working rule that must be re-applied after a restart; that is handled by
    # the reapply-on-launch path below.
    # New-NetQosPolicy reports failures as NON-terminating errors, so $ErrorActionPreference alone
    # does not stop the script: catch explicitly, and verify before claiming the boost is active.
    $qosError = $null
    $usedStore = $null
    foreach ($store in @('Local', 'ActiveStore')) {
        try {
            New-NetQosPolicy -Name $policyName -AppPathNameMatchCondition $location.ExecutablePath -DSCPAction 46 -NetworkProfile All -PolicyStore $store -ErrorAction Stop | Out-Null
            $usedStore = $store
            $qosError = $null
            break
        } catch {
            $qosError = $_.Exception.Message
        }
    }
    if (-not $usedStore) {
        Write-Tick 100 'Boost failed'
        Write-Result @{
            ok        = $false
            accelerated = $false
            message   = "Could not create the QoS rule: $qosError"
            hint      = 'Check that the "QoS Packet Scheduler" component is enabled on your network adapter and that the Windows Management Instrumentation service is running.'
            policy    = $policyName
            executablePath = $location.ExecutablePath
        }
        exit 0
    }

    # Confirm the rule is really there before reporting success.
    $verified = $false
    try {
        $found = @(Get-NetQosPolicy -PolicyStore $usedStore -ErrorAction Stop | Where-Object { $_.Name -eq $policyName })
        $verified = ($found.Count -gt 0)
    } catch {}
    if (-not $verified) {
        Write-Tick 100 'Boost failed'
        Write-Result @{
            ok = $false; accelerated = $false
            message = "The QoS rule was created but could not be verified in $usedStore."
            policy = $policyName; executablePath = $location.ExecutablePath
        }
        exit 0
    }

    Add-BoostState $game.Id $location.ExecutablePath $usedStore (Get-BootTimeStamp)

    # On Home editions the rule only lives in ActiveStore; schedule a logon task so it comes back by
    # itself after a restart instead of asking the user to click again.
    $taskInfo = $null
    if ($usedStore -eq 'ActiveStore') {
        $registered = Register-BoostReapplyTask
        $taskInfo = if ($registered) { 'scheduled' } else { 'not-scheduled' }
    }

    Write-Tick 100 'Boost enabled'
    $msg = "Windows QoS is enabled for $($game.Name). The rule only matches the scanned game executable."
    if ($usedStore -eq 'ActiveStore') {
        if ($taskInfo -eq 'scheduled') {
            $msg += " (This Windows edition has no Group Policy store, so the rule was applied to the active store and a logon task will re-apply it automatically after a restart.)"
        } else {
            $msg += " (This Windows edition has no Group Policy store, so the rule was applied to the active store only and must be re-applied after a restart.)"
        }
    }
    Write-Result @{
        ok = $true
        message = $msg
        policy = $policyName
        store = $usedStore
        persistent = ($usedStore -eq 'Local')
        autoReapply = ($taskInfo -eq 'scheduled')
        accelerated = $true
        executablePath = $location.ExecutablePath
    }
    exit 0
}

if ($Action -eq 'reapply-boost') {
    # Invoked by the logon scheduled task, already elevated. Re-creates every ActiveStore rule that
    # did not survive the reboot, so the user never has to remember to click the button again.
    $boot = Get-BootTimeStamp
    $entries = @(Read-BoostState)
    $applied = 0
    $failed = 0
    $skipped = 0
    foreach ($item in $entries) {
        if (-not $item.id -or -not $item.executablePath) { continue }
        if ($item.store -ne 'ActiveStore') { continue }
        if ($boot -and $item.bootTime -eq $boot) { $skipped++; continue }
        $policyName = "BatterBabel-$($item.id)"
        try {
            Remove-NetQosPolicy -Name $policyName -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue
            New-NetQosPolicy -Name $policyName -AppPathNameMatchCondition $item.executablePath -DSCPAction 46 -NetworkProfile All -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
            Add-BoostState $item.id $item.executablePath 'ActiveStore' $boot
            $applied++
        } catch {
            $failed++
        }
    }
    Write-Result @{ ok = $true; reapplied = $applied; failed = $failed; skipped = $skipped; total = @($entries).Count }
    exit 0
}

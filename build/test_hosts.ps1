# Standalone test for the tag-based hosts block logic (mirrors optimization.ps1).
$BabelHostsTags = @('CF', 'AMAZON', 'STEAM', 'EA')

function Set-BabelHosts([hashtable]$ByTag, [string]$HostsPath) {
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
    [System.IO.File]::WriteAllLines($HostsPath, [string[]]$clean, [System.Text.UTF8Encoding]::new($false))
    return $backup
}

function Remove-BabelHosts([string]$HostsPath) {
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
    [System.IO.File]::WriteAllLines($HostsPath, [string[]]$clean, [System.Text.UTF8Encoding]::new($false))
    return $removed
}

$test = Join-Path $env:TEMP 'bb-hosts-test2.txt'
Set-Content -LiteralPath $test -Value @('# (c) 2026', '127.0.0.1 localhost', '', '::1 localhost') -Encoding UTF8

$byTag = [ordered]@{}
$byTag['CF'] = [System.Collections.Generic.List[string]]::new()
$byTag['CF'].Add('108.162.192.1 download.limbuscompanycdn.org')
$byTag['CF'].Add('108.162.192.1 downloadcommon.limbuscompanycdn.org')
$byTag['AMAZON'] = [System.Collections.Generic.List[string]]::new()
$byTag['AMAZON'].Add('18.65.25.128 www.limbuscompanyapi.com')

Set-BabelHosts $byTag $test | Out-Null
Write-Host '--- after write ---'
Get-Content $test

# re-write with only STEAM to prove block replacement (old CF/AMAZON removed)
$byTag2 = [ordered]@{}
$byTag2['STEAM'] = [System.Collections.Generic.List[string]]::new()
$byTag2['STEAM'].Add('23.1.2.3 steamcontent.com')
Set-BabelHosts $byTag2 $test | Out-Null
Write-Host '--- after re-write (STEAM only) ---'
Get-Content $test

$removed = Remove-BabelHosts $test
Write-Host "--- after restore (removed=$removed) ---"
Get-Content $test
Remove-Item $test, "$test.batterbabel.bak" -Force -ErrorAction SilentlyContinue

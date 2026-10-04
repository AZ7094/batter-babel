# Per-game network profiles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make 系统调优 (`tune-system`) apply a per-game system-parameter tier plus a managed config block for games that genuinely have a client-side config surface, and report honestly which settings are network parameters.

**Architecture:** A new pure module `scripts/net-profiles.ps1` owns the tier table, the per-game recipe table, and every decision — tier delta, the composed tune plan, managed-block text transforms, and install-root derivation. `optimization.ps1` keeps only the imperative half: it snapshots originals, asks the module for a plan, applies that plan through the existing registry/netsh/adapter calls, writes managed blocks, and records what it wrote so `restore-tune` can undo it.

**Tech Stack:** Windows PowerShell 5.1 (the shell the app actually spawns — no `??`, no `ForEach-Object -Parallel`), Tauri 2, the repo's hand-rolled test runner, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-04-per-game-network-profiles-design.md`

## Global Constraints

- Every `scripts/**/*.ps1` is **pure ASCII with no BOM**. PowerShell 5.1 reads a BOM-less `.ps1` as ANSI; non-ASCII makes it fail to parse.
- `.gitattributes` enforces `*.ps1 eol=crlf` and `*.js eol=lf`. Do not fight it.
- Target **Windows PowerShell 5.1** only.
- Any new top-level `scripts/*.ps1` **must** be added to `bundle.resources` in `src-tauri/tauri.conf.json` in the same commit that creates it. CI fails the push otherwise, and an installed build dies at the dot-source with "The term ... is not recognized".
- Tests use the hand-rolled runner only: `scripts/tests/TestAssert.ps1` provides `Assert-Equal($Expected,$Actual,$Name)`, `Assert-True([bool]$Condition,$Name)`, `Assert-Throws([scriptblock]$Script,$Name)`. No Pester.
- `Assert-Equal` does `$Expected -eq $Actual`, which on two arrays returns a filtered array, not a boolean. **Compare arrays as `-join ','` strings.**
- Never edit `src/public/*.js`, `*.html`, or `docs/**.md` through `pwsh Get-Content`/`Set-Content`: those files are UTF-8 and the shell corrupts them. Use the editor tools.
- Commit messages: imperative English subject, no trailing period, blank line, then a body explaining why.
- Version for this feature: `0.10.2`, in `src-tauri/tauri.conf.json` and `src-tauri/Cargo.toml`.

## Review Focus

These are the conditions the spec implies but no test in it names. Each gets a test or an explicit guard in the task that owns the code.

1. **A user's own `autoexec.cfg` with no Batter Babel markers.** A reasonable person expects their content to survive. Taking the file over wholesale because it has no markers would delete their binds. Merge must append a block, and only a file matching the legacy Batter Babel output exactly may be replaced. (Task 3, Task 8)
2. **A half-written `autoexec.cfg` carrying only the start marker** (machine died mid-write). Deleting "up to the end marker" would eat the rest of the file. Both transforms must return the input unchanged when the marker pair is incomplete. (Task 3)
3. **Switching tiers when the snapshot holds real numbers, not nulls** (a machine that already had `TcpAckFrequency` set before Batter Babel ever ran). Restoring must write those numbers back, not blindly delete the values. (Task 2 pins the delta; Task 5 pins the restore.)
4. **A missing or truncated `tune-state.json` while a tier is applied.** `appliedTier` will be absent from snapshots written by 0.10.1 and earlier. The run must treat that as "no previous tier" and still snapshot rather than crash. (Task 5)
5. **A game whose install path vanished** between the catalog scan and the apply (a `steam-<appid>` entry that was since uninstalled). The run must report "not installed" and skip the config write, never write to a derived path that no longer exists. (Task 5)

---

### Task 1: Tier and recipe data, lookups, and module registration

**Files:**
- Create: `scripts/net-profiles.ps1`
- Create: `scripts/tests/net-profiles.tests.ps1`
- Modify: `scripts/tests/run-tests.ps1:7` (add a dot-source after `cf-probe.ps1`)
- Modify: `src-tauri/tauri.conf.json:36` (add the resource)

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `$NetProfileTiers`: hashtable keyed `latency`, `throughput`. Each value is an `[ordered]` hashtable with keys `Id`, `Label`, `Nagle`, `InterruptModeration`, `Eee`, `FlowControl`, `AutoTuning`, `Rss`, `Throttling`, `AdapterPower`, `WifiPower`. The string sentinel `'Restore'` means "put this key back to its snapshot original".
  - `$NetGameRecipes`: hashtable keyed by game id. Each value has `GameId`, `Tier`, `ExecutableParentDepth` (int), `ConfigFiles` (array of hashtables with `RelativePath`, `Lines`), `NonNetworkNotes` (string array), `HasSurface` (bool).
  - `Get-NetTier([string]$TierId)` → hashtable; throws on unknown id.
  - `Get-NetTierIds()` → `[string[]]`.
  - `Get-NetRecipe([string]$GameId)` → hashtable; unknown or empty id returns the default recipe.

- [ ] **Step 1: Write the failing test**

Create `scripts/tests/net-profiles.tests.ps1`:

```powershell
Assert-Equal 'latency' (Get-NetTier 'latency').Id 'tier latency id'
Assert-Equal 'throughput' (Get-NetTier 'throughput').Id 'tier throughput id'
Assert-Throws { Get-NetTier 'nope' } 'unknown tier throws'
Assert-Equal 'latency,throughput' ((Get-NetTierIds) -join ',') 'exactly two tiers'
Assert-Equal 'latency' (Get-NetTier 'latency').AutoTuning 'latency uses normal autotuning'
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
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tests\run-tests.ps1`
Expected: FAIL — `Get-NetTier` is not recognized, and the run exits non-zero.

- [ ] **Step 3: Implement the module**

Create `scripts/net-profiles.ps1`, ASCII only, with a header comment naming this plan's spec. Contents: the two data tables and the three lookup functions.

Tier values, exactly:

| Key | latency | throughput |
| --- | --- | --- |
| `Nagle` | `$true` | `'Restore'` |
| `InterruptModeration` | `0` | `1` |
| `Eee` | `0` | `0` |
| `FlowControl` | `0` | `0` |
| `AutoTuning` | `'normal'` | `'normal'` |
| `Rss` | `'enabled'` | `'enabled'` |
| `Throttling` | `'off'` | `'off'` |
| `AdapterPower` | `'disabled'` | `'disabled'` |
| `WifiPower` | `'max'` | `'max'` |

Recipes: `cs2` = latency, depth 4, one config file at `game\csgo\cfg\autoexec.cfg` whose `Lines` are exactly

```
// Batter Babel (generated) - CS2 has no client-side network cvar worth setting;
// see docs/superpowers/specs/2026-10-04-per-game-network-profiles-design.md
// The line below is a rendering tweak, not a network parameter.
cl_allow_animated_avatars 0
```

and `NonNetworkNotes` = one entry naming `cl_allow_animated_avatars 0` as a rendering tweak, not a network parameter. Then `limbus` and `bd2` at throughput, `apex`/`hunt`/`dst`/`terraria` at latency — all with `ConfigFiles = @()`, `HasSurface = $false`, `ExecutableParentDepth = 0`. The default recipe (unknown id) is latency, no config files, `HasSurface = $false`.

`Get-NetTier` throws `"Unknown tier: $TierId"` for an id that is not a key. `Get-NetRecipe` returns the default recipe for `$null` or an unregistered id, with `GameId` set to the requested id.

- [ ] **Step 4: Register the module so CI and the installer both see it**

Add the dot-source to `scripts/tests/run-tests.ps1` immediately after the `cf-probe.ps1` line:

```powershell
. (Join-Path $scriptsRoot 'net-profiles.ps1')
```

`run-tests.ps1` already globs `*.tests.ps1`, so the new test file is picked up with no further change.

Add `"../scripts/net-profiles.ps1"` to the `bundle.resources` array in `src-tauri/tauri.conf.json:36`. This must be in this commit: CI's resource-whitelist check fails any push where a top-level `scripts/*.ps1` is missing from that array.

- [ ] **Step 5: Run the test to verify it passes**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tests\run-tests.ps1`
Expected: PASS — every new line prints `ok -`, `failed: 0`, exit code 0.

- [ ] **Step 6: Commit**

```bash
git add scripts/net-profiles.ps1 scripts/tests/net-profiles.tests.ps1 scripts/tests/run-tests.ps1 src-tauri/tauri.conf.json
git commit -m "Add per-game network profile data and lookups"
```

---

### Task 2: Tier delta and the Nagle value names

**Files:**
- Modify: `scripts/net-profiles.ps1`
- Modify: `scripts/tests/net-profiles.tests.ps1`

**Interfaces:**
- Consumes: `$NetProfileTiers`, `Get-NetTier` from Task 1.
- Produces:
  - `Get-NetTierDelta([string]$FromTierId, [string]$ToTierId)` → `@{ Set = [string[]]; Restore = [string[]] }`, holding logical key names only.
  - `Get-NagleValueNames()` → `[string[]]` = `TcpAckFrequency`, `TCPNoDelay`, `TcpDelAckTicks`.

Rules that must hold: the managed keys are every tier key except `Id` and `Label`. If `$FromTierId` equals `$ToTierId` and is non-empty, both lists are empty. Otherwise, for each managed key: when the target value is `'Restore'` the key goes to `Restore`; when the source tier exists and holds the same value the key is skipped; otherwise the key goes to `Set`.

- [ ] **Step 1: Write the failing tests**

Append to `scripts/tests/net-profiles.tests.ps1`:

```powershell
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tests\run-tests.ps1`
Expected: FAIL — `Get-NetTierDelta` is not recognized.

- [ ] **Step 3: Implement `Get-NetTierDelta` and `Get-NagleValueNames`**

Both in `scripts/net-profiles.ps1`. `Get-NetTierDelta` returns two arrays and must never return `$null` for either, so callers can use `.Count` and `-contains` without guarding.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tests\run-tests.ps1`
Expected: PASS, `failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add scripts/net-profiles.ps1 scripts/tests/net-profiles.tests.ps1
git commit -m "Compute the tier delta so switching tiers cannot leave stale values"
```

---

### Task 3: Managed block transforms, legacy detection, and install-root derivation

**Files:**
- Modify: `scripts/net-profiles.ps1`
- Modify: `scripts/tests/net-profiles.tests.ps1`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `Merge-ManagedBlock([string]$Text, [string[]]$Lines)` → string.
  - `Remove-ManagedBlock([string]$Text)` → string.
  - `Test-HasManagedBlock([string]$Text)` → bool.
  - `Test-NetLegacyBlock([string]$Text)` → bool.
  - `Get-NetGameRoot([string]$ExecutablePath, [int]$ParentDepth)` → string.

Markers, fixed: start `// >>> Batter Babel >>>`, end `// <<< Batter Babel <<<`. `Text` may be `$null` or empty and must be treated as empty. `Merge-ManagedBlock` replaces the block when both markers are present, appends one when neither is, and returns `$Text` **unchanged** when exactly one is present. Line endings in the output are `\r\n`. Merging an already-merged text with the same lines returns it byte-identical. `Remove-ManagedBlock` deletes both markers and everything between them plus the trailing newline, and returns `$Text` unchanged when the pair is incomplete. `Test-NetLegacyBlock` compares the non-empty, trimmed lines as an unordered case-insensitive set against exactly these seven:

```
// Batter Babel network tuning (generated)
rate 196608
cl_interp 0.031
cl_interp_ratio 2
cl_net_buffer_ticks 64
net_graph 1
cl_allow_animated_avatars false
```

`Get-NetGameRoot` applies `Split-Path -Parent` `$ParentDepth` times; an empty path returns `''` and a negative depth behaves as 0.

- [ ] **Step 1: Write the failing tests**

Append to `scripts/tests/net-profiles.tests.ps1`:

```powershell
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

Assert-Equal 'C:\game' (Get-NetGameRoot 'C:\game\bin\win64\cs2.exe' 4) 'root derived by parent depth'
Assert-Equal 'C:\game\bin\win64' (Get-NetGameRoot 'C:\game\bin\win64\cs2.exe' 0) 'depth 0 is the exe folder'
Assert-Equal '' (Get-NetGameRoot '' 4) 'empty exe path yields empty root'
Assert-Equal 'C:\game' (Get-NetGameRoot 'C:\game\bin\win64\cs2.exe' -1) 'negative depth behaves as zero'
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tests\run-tests.ps1`
Expected: FAIL — `Merge-ManagedBlock` is not recognized.

- [ ] **Step 3: Implement the five functions**

All in `scripts/net-profiles.ps1`. Implement the transforms on `\r\n`-normalised text so that marker detection and the idempotence assertions hold regardless of the input's line endings. Do not use a regex greedy across the whole file for the removal: find the start marker index, then the first end marker after it, and splice.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tests\run-tests.ps1`
Expected: PASS, `failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add scripts/net-profiles.ps1 scripts/tests/net-profiles.tests.ps1
git commit -m "Add managed block transforms that never touch text outside the block"
```

---

### Task 4: Compose the tune plan

**Files:**
- Modify: `scripts/net-profiles.ps1`
- Modify: `scripts/tests/net-profiles.tests.ps1`

**Interfaces:**
- Consumes: `Get-NetRecipe`, `Get-NetTier`, `Get-NetTierDelta`, `Merge-ManagedBlock`, `Get-NetGameRoot` from Tasks 1-3.
- Produces: `Get-NetTunePlan([string]$GameId, [string]$FromTierId, [string]$InstallRoot)` → `[ordered]` hashtable:
  - `Tier` — the tier id to apply.
  - `Set` — logical key names to write.
  - `Restore` — logical key names to put back from the snapshot.
  - `ConfigWrites` — array of `@{ Path = <absolute path>; Lines = [string[]] }`, one per recipe config file; empty when the game has no surface.
  - `Notes` — string array, the recipe's `NonNetworkNotes` followed by a "no client-side network parameters" line when `HasSurface` is false.

`$InstallRoot` is passed in, never discovered here, so the function stays pure and testable. A `ConfigWrites` path is `[IO.Path]::Combine($InstallRoot, $RelativePath)`. An empty `$InstallRoot` yields an empty `ConfigWrites` array and adds a note.

- [ ] **Step 1: Write the failing tests**

Append to `scripts/tests/net-profiles.tests.ps1`:

```powershell
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tests\run-tests.ps1`
Expected: FAIL — `Get-NetTunePlan` is not recognized.

- [ ] **Step 3: Implement `Get-NetTunePlan`**

In `scripts/net-profiles.ps1`. It composes the earlier functions and owns no I/O. The "install path" note text must contain the substring `install path` so the test above pins it.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tests\run-tests.ps1`
Expected: PASS, `failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add scripts/net-profiles.ps1 scripts/tests/net-profiles.tests.ps1
git commit -m "Compose one pure plan per game instead of branching inside the tuner"
```

---

### Task 5: Execute the plan from tune-system

**Files:**
- Modify: `scripts/optimization.ps1` (dot-source after line 1228; new helpers near line 1650; rewrite the `tune-system` body at 2017-2296)

**Interfaces:**
- Consumes: everything from `net-profiles.ps1`; the existing `Get-TuneStatePath`, `Load-TuneState`, `Save-TuneState`, `Find-Game`, `$Catalog`, `Write-Tick`, `Write-Result`, `Test-Administrator`, `Get-WirelessPowerIndex`.
- Produces: `tune-system` accepts an optional `-GameId`; its result gains an item whose `id` is `net-tier` and one item per config write; `tune-state.json` gains top-level `appliedTier` (string) and `managedConfigFiles` (string array).

- [ ] **Step 1: Dot-source the module**

Add after `optimization.ps1:1228`:

```powershell
. "$PSScriptRoot\net-profiles.ps1"
```

- [ ] **Step 2: Add the two imperative helpers**

Near the other tune helpers (around line 1650), add:

`Restore-TuneKey([string]$Key, $State)` → `[string]` note. Puts the named logical key back to its snapshot value and describes what it did:

- `Nagle` — for every interface guid in `$State.nagle`, write each name from `Get-NagleValueNames()` back when the snapshot holds a value, and remove the property when it is `$null` or empty. This is the branch Review Focus 3 pins: a snapshot value of `0` is a real value, so `$null`/empty is the *only* case that deletes.
- `InterruptModeration`, `Eee`, `FlowControl` — restore each adapter's `*`-prefixed property from `$State.adapterProps`, skipping `$null`/empty entries.
- `AutoTuning`, `Rss` — `netsh int tcp set global` from `$State.tcpGlobal`.
- `Throttling` — write `$State.throttle.NetworkThrottlingIndex` and `SystemResponsiveness` back under `HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile`.
- `AdapterPower` — `Set-NetAdapterPowerManagement -AllowComputerToTurnOffDevice` from `$State.adapterPower` for each adapter.
- `WifiPower` — `powercfg` the stored `ac`/`dc` indices.
- An unrecognised key returns a note saying it was skipped. It must not throw.

`Invoke-TuneTier([hashtable]$Tier, [hashtable]$Delta, $State, [System.Collections.Generic.List[string]]$Log)` → `@()` of result items with `id`, `title`, `ok`, `detail`. For each key in `$Delta.Set` it performs that tier's value (Nagle triad via `Get-NagleValueNames()`; adapter properties via `Set-NetAdapterAdvancedProperty`; `netsh` for autotuning/RSS; the multimedia-throttling registry values; adapter power; Wi-Fi power), and for each key in `$Delta.Restore` it calls `Restore-TuneKey`. Every item's `ok` must reflect whether the underlying call actually succeeded — copy the existing per-step `try`/`catch` discipline, and do not report success for something that was skipped.

- [ ] **Step 3: Rewrite the `tune-system` body**

Keep the snapshot block (lines 2022-2076) unchanged in behaviour, then:

1. Resolve the game: when `$GameId` is set and `$Catalog` has it, `Find-Game` gives the executable path; pass `Get-NetGameRoot` with the recipe's `ExecutableParentDepth` as the install root. When the game is not installed or the path is missing, pass an empty install root and add a note — this is Review Focus 5, and it must not throw.
2. Load the state with `Load-TuneState` and read `appliedTier`. Absent means `$null` — Review Focus 4.
3. `Get-NetTunePlan $GameId $appliedTier $installRoot`.
4. `Invoke-TuneTier` for the tier items.
5. Write the config files: for each `ConfigWrites` entry, read the file when it exists; if it has a managed block, `Merge-ManagedBlock` and write; if it has no block **and** `Test-NetLegacyBlock` is true, copy it to `<path>.batterbabel.bak` once and write `Merge-ManagedBlock '' $Lines`; otherwise `Merge-ManagedBlock` onto the existing text, preserving the user's content. Never delete the file. When the directory does not exist, skip with an item whose `ok` is `$false` and a detail saying the game config directory is missing.
6. Record `appliedTier` and `managedConfigFiles` on the state object and `Save-TuneState`.
7. Keep the existing background-program check as a tier-independent item, and drop the old inline CS2 block — the plan now owns it.

All new `Write-Tick` messages must stay ASCII and match one of: `Applying network tier $1`, `Restoring $1 to its original value`, `Writing game config $1`, `System tune complete`.

- [ ] **Step 4: Verify it parses and nothing else broke**

Run: `powershell -NoProfile -Command "$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile('scripts\optimization.ps1',[ref]$null,[ref]$e); if($e.Count){$e; exit 1}"`
Expected: no output, exit code 0.

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tests\run-tests.ps1`
Expected: PASS, `failed: 0` — the module tests still pass and the parse is clean.

Applying the tier cannot be unit-tested: it needs an elevated process and a real registry. Task 8 runs it for real.

- [ ] **Step 5: Commit**

```bash
git add scripts/optimization.ps1
git commit -m "Apply the selected game's network tier and record which one is active"
```

---

### Task 6: Restore the managed blocks

**Files:**
- Modify: `scripts/optimization.ps1` (`restore-tune`, from line 2298)

**Interfaces:**
- Consumes: `Remove-ManagedBlock` from Task 3; `managedConfigFiles` written by Task 5.
- Produces: an extra `restore-tune` item with `id` `game-configs`; the five existing items keep their ids.

- [ ] **Step 1: Add the config-restore step to `restore-tune`**

Before the final `Write-Tick 100` and `Write-Result`, add a step that iterates `$state.managedConfigFiles`, calls `Remove-ManagedBlock` on each file's contents, writes the result back when it differs, and reports one item. Missing files are reported as skipped with `ok = $true` and a detail saying the file is gone — a file the user deleted is not a failure. Also clear `appliedTier` and `managedConfigFiles` from the state after a successful restore, and keep the existing behaviour of removing the state file at the end.

Keep `Write-Tick` messages ASCII: `Removing Batter Babel blocks`, `Restoring game config $1`.

- [ ] **Step 2: Verify it parses and the suite still passes**

Run: `powershell -NoProfile -Command "$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile('scripts\optimization.ps1',[ref]$null,[ref]$e); if($e.Count){$e; exit 1}"`
Expected: no output, exit code 0.

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tests\run-tests.ps1`
Expected: PASS, `failed: 0`.

- [ ] **Step 3: Commit**

```bash
git add scripts/optimization.ps1
git commit -m "Remove the Batter Babel blocks when restoring system tuning"
```

---

### Task 7: Send the selected game and localise the new messages

**Files:**
- Modify: `src/public/app.js` (the `PROGRESS_ZH` table ending at line 168; `tuneSystem()` at 646-680)

**Interfaces:**
- Consumes: the `tune-system` backend from Task 5, which now accepts `gameId`.
- Produces: no new exported names.

- [ ] **Step 1: Pass the game id**

In `tuneSystem()` change line 657 to send the current game, matching how `cf-optimize` does it at line 530:

```js
const data = await invokeAction({ action: 'tune-system', gameId: currentGame.id });
```

Guard the case where no game is selected the same way the surrounding code does; `currentGame.id` is already used unguarded at line 530, so follow that precedent rather than inventing a new one.

- [ ] **Step 2: Add the progress rules**

Append to `PROGRESS_ZH` before its closing `];`:

```js
[/^Applying network tier (\S+)$/, '套用网络参数档位 $1'],
[/^Restoring (.+) to its original value$/, '还原 $1 为原始值'],
[/^Writing game config (.+)$/, '写入游戏配置 $1'],
[/^Removing Batter Babel blocks$/, '移除 Batter Babel 配置块'],
[/^Restoring game config (.+)$/, '还原游戏配置 $1'],
```

- [ ] **Step 3: Verify the file still parses**

Run: `node --check src/public/app.js`
Expected: no output, exit code 0.

- [ ] **Step 4: Commit**

```bash
git add src/public/app.js
git commit -m "Send the selected game to system tuning and localise its progress"
```

---

### Task 8: Version bump, build, and end-to-end verification

**Files:**
- Modify: `src-tauri/tauri.conf.json:4` (version)
- Modify: `src-tauri/Cargo.toml` (version)

**Interfaces:**
- Consumes: all previous tasks.
- Produces: `0.10.2` artifacts; `src-tauri/target/release/bundle/nsis/Batter Babel_0.10.2_x64-setup.exe`.

- [ ] **Step 1: Bump both versions to 0.10.2**

`src-tauri/tauri.conf.json` `"version"` and the `version` field of `src-tauri/Cargo.toml`. `Cargo.lock` updates itself on the next build. The repo's convention is the bump travels with the final code commit, and its message body ends with `Bump to 0.10.2.`

- [ ] **Step 2: Run the full CI-equivalent check locally**

Run each of these and confirm the stated result:

```powershell
# every scripts/**/*.ps1 is ASCII with no BOM
Get-ChildItem scripts -Recurse -Filter *.ps1 | ForEach-Object {
  $b = [IO.File]::ReadAllBytes($_.FullName)
  if ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { "BOM: $($_.Name)" }
  if ($b | Where-Object { $_ -gt 0x7F }) { "NON-ASCII: $($_.Name)" }
}

# resource whitelist matches the tree
node --check src/public/app.js
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tests\run-tests.ps1
```

Expected: no `BOM:` and no `NON-ASCII:` lines; `node --check` silent; runner reports `failed: 0`.

- [ ] **Step 3: Build**

Run: `cmd /c "call \"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat\" && cd /d \"D:\Batter Babel\src-tauri\" && cargo tauri build"`
Expected: succeeds in roughly two minutes and writes `src-tauri/target/release/bundle/nsis/Batter Babel_0.10.2_x64-setup.exe`.

- [ ] **Step 4: Run the real apply, which needs the user**

Ask the user to press 系统调优 with CS2 selected and approve the UAC prompt. Then confirm all of:

- `E:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg\autoexec.cfg` contains the managed block with exactly `cl_allow_animated_avatars 0`, and its `.batterbabel.bak` still holds the seven legacy lines.
- `%LOCALAPPDATA%\BatterBabel\tune-state.json` has `appliedTier` = `latency` and one entry in `managedConfigFiles`.
- Selecting Limbus Company and pressing 系统调优 again reports the throughput tier, and the log shows a Nagle restore rather than a Nagle set.
- The log contains no item claiming success for a setting CS2 ignores.

- [ ] **Step 5: Restore and confirm**

Press 还原系统调优 and confirm the managed block is gone from `autoexec.cfg` while anything the user wrote outside it survives, and that the registry values match `tune-state.json`'s originals.

- [ ] **Step 6: Commit**

```bash
git add src-tauri/tauri.conf.json src-tauri/Cargo.toml src-tauri/Cargo.lock
git commit -m "Ship per-game network profiles

<what changed and why, in the repo's detailed-body style>

Bump to 0.10.2."
```

## Self-Review

**Spec coverage.** Every spec section maps to a task: the module and its data to Tasks 1-4; the tier table and its two real differences to Task 1; the idempotent apply plus snapshot-based restore to Tasks 2 and 5; the managed block, the abandoned wholesale-overwrite strategy, and the legacy takeover to Tasks 3 and 5; the CS2 recipe and the kept `cl_allow_animated_avatars 0` to Tasks 1, 4, and 5; the per-game tier mapping to Task 1; `-GameId` and the frontend to Tasks 5 and 7; the resource whitelist and version to Tasks 1 and 8; the test list to Tasks 1-4; the acceptance criteria to Task 8. The spec's "no background watcher" and "never write Steam launch options" non-goals are honoured by omission — nothing in the plan touches a scheduled task or `localconfig.vdf`.

**Step scan.** No step says TBD, "handle edge cases", or "add validation". Each test step carries its assertions; each code step carries signatures, file paths, and the spec's exact values, and leaves bodies to the implementer except where the exact text is the deliverable (the four managed-block lines, the seven legacy lines, the tier table, the `PROGRESS_ZH` entries).

**Type consistency.** `Get-NetTierIds` returns `[string[]]`. `Get-NetTierDelta` returns `@{Set; Restore}` and both are always arrays. `Get-NetTunePlan` returns a plan whose `ConfigWrites[].Path` is absolute and whose `Lines` are `[string[]]`. `Restore-TuneKey` returns a `[string]` note; `Invoke-TuneTier` returns result items shaped like the existing ones (`id`, `title`, `ok`, `detail`). `managedConfigFiles` is a string array in both the writer (Task 5) and the reader (Task 6). Marker strings are defined once in Task 3 and referenced, never re-typed, afterwards.

**Review Focus.** Items 1 and 2 have tests in Task 3 and are consumed by Task 5 Step 3. Item 3 has delta tests in Task 2 and the null-versus-zero branch named in Task 5 Step 2. Item 4 is named in Task 5 Step 3. Item 5 is named in Task 5 Steps 3 and 4 and checked by hand in Task 8 Step 4.

**Proportion.** The plan is longer than the spec because the spec is a design narrative and this is eight independently reviewable commits; the code blocks are signatures and assertions, not bodies, except the three tables and lists whose exact text is the deliverable.

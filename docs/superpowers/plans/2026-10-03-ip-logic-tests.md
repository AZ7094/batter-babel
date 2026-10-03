# IP Logic Test Baseline

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add regression coverage to the fragile IP/CIDR logic on both sides of the boundary — Rust entry validation and the PowerShell candidate pool — and fix one real bug (`is_ipv4` accepting leading-zero octets that PowerShell reinterprets as octal).

**Architecture:** The five pure PowerShell functions and `$CloudflareRanges` move into a dot-sourceable `scripts/ip-core.ps1`; `optimization.ps1` dot-sources it in place. A hand-rolled assertion runner (`scripts/tests/`) exercises them with no network. `commands.rs` gains a `#[cfg(test)]` module.

**Tech Stack:** PowerShell 5.1 (ASCII-only scripts) + `System.Net.IPAddress`; Rust + `cargo test`.

**Source spec:** `docs/superpowers/specs/2026-10-03-ip-logic-tests-design.md`

---

## File Structure

### Create

- `scripts/ip-core.ps1` — `$CloudflareRanges` + `ConvertTo-IpString`, `ConvertFrom-IpString`, `Test-IpInCidr`, `Test-IpInCloudflare`, `Get-CfCandidatePool`.
- `scripts/tests/TestAssert.ps1` — `Reset-TestTally`, `Assert-Equal`, `Assert-True`, `Assert-Throws` with a script-scoped pass/fail tally.
- `scripts/tests/run-tests.ps1` — bootstraps `ip-core.ps1` + `TestAssert.ps1`, dot-sources every `*.tests.ps1`, prints a summary, exits 1 on any failure.
- `scripts/tests/ip-core.tests.ps1` — inline regression cases (round-trip, `[long]` overflow, CIDR membership, pool invariants).

### Modify

- `scripts/optimization.ps1` — replace the inline `$CloudflareRanges` + the five functions with `. "$PSScriptRoot\ip-core.ps1"`.
- `src-tauri/tauri.conf.json` — add `../scripts/ip-core.ps1` to `bundle.resources` (the installer currently ships only `optimization.ps1`, so a dot-source would break on installed builds).
- `src-tauri/src/commands.rs` — harden `is_ipv4` and add `#[cfg(test)] mod tests`.

---

### Task 1: Rust — reject non-canonical IPv4 (TDD red → green)

**Files:**
- Modify: `src-tauri/src/commands.rs`

- [x] **Step 1: Add the failing test first**

Add a `#[cfg(test)] mod tests` at the bottom of `commands.rs` with:

```rust
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_leading_zero_octets() {
        // PowerShell's [IPAddress]::Parse reads leading zeros as OCTAL,
        // so "010.1.1.1" silently becomes 8.1.1.1. Must reject up front.
        assert!(!is_ipv4("010.1.1.1"));
        assert!(!is_ipv4("01.02.03.04"));
        assert!(!is_ipv4("192.168.01.1"));
    }

    #[test]
    fn accepts_canonical_ipv4() {
        assert!(is_ipv4("1.2.3.4"));
        assert!(is_ipv4("0.0.0.0"));
        assert!(is_ipv4("255.255.255.255"));
        assert!(is_ipv4("108.162.192.1"));
    }

    #[test]
    fn rejects_malformed_ipv4() {
        assert!(!is_ipv4(""));
        assert!(!is_ipv4("1.2.3"));
        assert!(!is_ipv4("1.2.3.4.5"));
        assert!(!is_ipv4("256.1.1.1"));
        assert!(!is_ipv4("1.2.3.4x"));
        assert!(!is_ipv4("1..3.4"));
        assert!(!is_ipv4(" 1.2.3.4"));
    }

    #[test]
    fn validates_game_ids_by_format() {
        assert!(is_valid_game_id("limbus"));
        assert!(is_valid_game_id("cs2"));
        assert!(is_valid_game_id("hunt-dst_2026"));
        assert!(!is_valid_game_id(""));
        assert!(!is_valid_game_id("Game"));
        assert!(!is_valid_game_id("game with space"));
        assert!(!is_valid_game_id(&"a".repeat(33)));
    }
}
```

Run `cargo test` — `rejects_leading_zero_octets` must FAIL (red).

- [x] **Step 2: Make it pass**

Harden `is_ipv4` to reject non-canonical octets:

```rust
fn is_ipv4(s: &str) -> bool {
    let parts: Vec<&str> = s.split('.').collect();
    if parts.len() != 4 {
        return false;
    }
    parts.iter().all(|p| {
        !p.is_empty()
            && p.len() <= 3
            && p.chars().all(|c| c.is_ascii_digit())
            && !(p.len() > 1 && p.starts_with('0'))
            && p.parse::<u8>().is_ok()
    })
}
```

Run `cargo test` again — all four tests pass (green).

---

### Task 2: PowerShell — extract `ip-core.ps1`

**Files:**
- Create: `scripts/ip-core.ps1`
- Modify: `scripts/optimization.ps1`

- [x] **Step 1: Create the module** (ASCII-only; move `$CloudflareRanges` plus the five functions verbatim)

- [x] **Step 2: Dot-source it** — in `optimization.ps1`, replace the block from the `# --- Cloudflare CDN optimisation` comment through the end of `Get-CfCandidatePool` (keeping `$CfDomainCatalog`, `$CloudFrontEndpoints`, `$CloudFrontDohSources`, `$CfCandidateIps` in place) with:

```powershell
# --- Cloudflare CDN optimisation (LLC_BABEL style) ---
# Pure IP/CIDR helpers live in ip-core.ps1 so tests can dot-source them without running the dispatcher.
. "$PSScriptRoot\ip-core.ps1"
```

- [x] **Step 3: Bundle the new file** — add `"../scripts/ip-core.ps1"` to `bundle.resources` in `src-tauri/tauri.conf.json`. Without it the installed app cannot resolve the dot-source and every action fails.

- [x] **Step 4: Smoke check** — verify `optimization.ps1` still parses and `Get-CfCandidatePool` / `Test-IpInCloudflare` still resolve from the extracted file.

---

### Task 3: PowerShell — assertion runner + regression tests

**Files:**
- Create: `scripts/tests/TestAssert.ps1`
- Create: `scripts/tests/run-tests.ps1`
- Create: `scripts/tests/ip-core.tests.ps1`

- [x] **Step 1: Assertion helpers** — `Assert-Equal`, `Assert-True`, `Assert-Throws`, plus `$script:TestPassed` / `$script:TestFailed` tally (ASCII-only).

- [x] **Step 2: Runner** — dot-source `ip-core.ps1`, `TestAssert.ps1`, then every `*.tests.ps1`; print `passed:` / `failed:`; `exit 1` when any failure.

- [x] **Step 3: Test cases** — cover: `ConvertTo-IpString` 0 and max-u32; round-trip `173.245.48.1`; `ConvertFrom-IpString` equals `2918526977` (proves no int32 overflow on the 173 octet); CIDR member/non-member; `Test-IpInCloudflare` true/false; `Get-CfCandidatePool -Target 2000` returns 2000 unique addresses all inside Cloudflare ranges; small pool (100) sizing.

- [x] **Step 4: Run and green** — `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tests\run-tests.ps1` exits 0.

---

### Task 4: Verify no behavioral change

- [x] `cargo test` green, `run-tests.ps1` green.
- [x] `optimization.ps1` still dot-sources `ip-core.ps1`; a dry `Get-CfCandidatePool -Target 2000 | Measure-Object` returns 2000, matching the pre-extraction behavior.
- [x] `git diff` shows only the intended moves (no accidental edit to the kept catalogs).

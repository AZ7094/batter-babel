# Batter Babel

**English** | [简体中文](README.zh-CN.md)

A Windows game network optimisation tool. It automatically detects the **online games installed** on your PC, benchmarks the CDN / API routes of the game you select, and — once you confirm — writes the fastest endpoints into your `hosts` file. It also ships a one-click, **game-agnostic** system network tune-up.

Everything runs locally. There is no cloud service, no account, no third-party proxy node, and no traffic relaying.

## Features

**What it can do**: accelerate **domain-based services** — login, updates, lobby, friends, achievements, store, official sites, asset CDNs.

**What it cannot do**:
- **In-game latency** — match traffic is UDP straight to the game server (or P2P straight to another player) and **never touches a domain**, so `hosts` has nothing to work with. For those games the app shows an amber notice explaining this.
- **In-game downloads** (maps / mods / save syncing) — Steam asks its Connection Manager for a **list of content-server IPs** and then connects to those IPs directly, and P2P transfers involve no domain at all. To speed up Steam downloads use the official setting instead: **Steam → Settings → Downloads → Download Region**, which beats any `hosts` optimisation.

### 加速游戏 (local QoS)

This is the only mechanism that improves **match latency** (especially useful for titles like Hunt: Showdown, whose servers are assigned dynamically).

- Creates a Windows QoS rule (`DSCP 46`, all network profiles) for the selected game's executable, matching **only that one exe** — nothing else is affected.
- **It works on every Windows edition.** Home editions have no Group Policy, so `-PolicyStore Local` always fails with "The network path was not found" (System Error 53). The app tries `Local` first (survives reboots) and **automatically falls back to `ActiveStore`**, then registers a **logon scheduled task** that re-creates the rule after every restart — Home users never have to click again. Removing the last boost removes the task.
- Button state lives in `%LOCALAPPDATA%\BatterBabel\boosted.json` (reading the QoS store directly needs administrator rights, which a plain scan does not have). The record also stores the **policy store** and the **boot time**, so a rule that no longer survives after a restart is correctly reported as inactive.

### Game detection

- Steam stores each app's **category flags** locally in `appcache/appinfo.vdf`, and the keys are named `category_<id>` using Steam's own ids (`1` multi-player, `9` co-op, `20` MMO, `27` cross-platform multi-player, `36` online PvP, `38` online co-op). The app parses that binary KeyValues file directly — 16-byte header (magic `0x07564428`/`0x07564429`, universe, string-table offset) → repeating `[appid][size][binary KV]` entries → trailing string table that the keys reference **by index** — so online-ness is decided **entirely offline**, with no store API (which many networks cannot reach at all).
- Every Steam library is scanned (`libraryfolders.vdf` + registry, parsing each `appmanifest_*.acf`); standalone clients are found through the uninstall registry and common install roots (`STOVE`, `Neowiz`, …). Library paths are de-duplicated case-insensitively (`d:\steam` and `D:\steam` are the same library), and Steam's redistributables (appid 228980) are skipped.

### Platform targets: every detected game is optimisable

Most games only talk to the same handful of platform services, so endpoints are defined **per platform** (`$PlatformTargets`: Ubisoft, Epic, Blizzard, EA) and every detected game inherits its platform's endpoints automatically. Rainbow Six Siege uses Ubisoft Connect's endpoints.

**Steam's own endpoints are not ranked** (`api/login.steampowered.com`, the Steam CDN). Almost everyone already runs a Steam accelerator, and the real control for Steam download speed is **Settings → Downloads → Download Region**, so ranking them adds nothing. Titles whose only endpoints would have been Steam's (CS2, Hunt: Showdown, …) therefore show **no routes** — use the **加速游戏** QoS boost for those instead.

A separate `$Catalog` entry is only needed when a game has **its own** domains worth ranking. `$OnlineGameIds` (~83 well-known online games) is only a fallback for what the local metadata does not cover.

Game ids are validated by format only, and **the single place a game is defined is `$Catalog`** in `optimization.ps1` — adding one never requires touching the Rust layer, and there is no `ValidateSet` to keep in sync.

### Route selection

- **Cloudflare** — samples **2000 addresses** evenly across every official IPv4 range, screens them with concurrent TCP:443 handshakes (~10 s for the whole pool), then runs **real download tests** (`curl --resolve`, TLS chain still verified) on the 10 curated addresses, any previously-learned fast addresses, and the 20 lowest-latency discoveries (30 in total), ranking them by measured throughput.
  - **Self-learning** — every run records its fastest downloaders to `%LOCALAPPDATA%\BatterBabel\cf-good-ips.json`. An address that reaches the top few **twice** earns a permanent free pass into the download stage, skipping the latency screen (which does not predict throughput). The library is capped at 30 entries.
  - **Stale-address handling** — every tested address has its health refreshed: a win clears the failure count, a failure increments it, and **two consecutive failures evict the entry**. Curated addresses are *demoted* rather than deleted, avoiding the "delete → free pass next round → fail again" loop. Addresses that never win are not kept at all, so exploration noise cannot crowd out the real winners.
- **CloudFront / generic CDN** — collects candidate IPs from the system resolver **plus** AliDNS and DNSPod DoH, probes each candidate per domain (real TLS SNI, certificate and Host preserved), and keeps the lowest median latency.
- **Writing hosts** — results go into per-provider blocks (`CF`, `AMAZON`, `STEAM`, `EA`, `KLEI`, `UBISOFT`, `EPIC`, `BLIZZARD`), are backed up to `hosts.batterbabel.bak`, and can be removed with one click. Anything outside the marked blocks is never touched. The `CF` block additionally **rejects any address that is not an official Cloudflare range**.

### System tuning (game-agnostic, one click)

Generic latency fixes that benefit any online game:

- **TCP low latency** — Nagle / delayed-ACK disabled per interface (`TcpAckFrequency`=1, `TCPNoDelay`=1, `TcpDelAckTicks`=0), so small packets leave immediately
- **TCP globals** — `autotuninglevel=normal`, heuristics disabled, RSS enabled
- **Adapter low-latency properties** — interrupt moderation, energy-efficient Ethernet, flow control and device power saving turned off
- **Windows network throttling** — `NetworkThrottlingIndex` disabled, `SystemResponsiveness`=10
- **Wi-Fi power** — adapter set to maximum performance
- **Background bandwidth check** — lists bandwidth hogs (Baidu Netdisk, Thunder, video and torrent clients…)

**Restore system tuning** — before the first change the originals are snapshotted to `%LOCALAPPDATA%\BatterBabel\tune-state.json`; one click restores everything.

## Download

A pre-built installer is published on the [Releases](../../releases) page:

- `Batter Babel_<version>_x64-setup.exe` — NSIS installer (desktop + start-menu shortcuts), ~1.1 MB.

> Requires the **WebView2 Runtime** (preinstalled on Windows 11 and on up-to-date Windows 10).

## Usage
Only **写入 hosts**, **还原 hosts**, **加速游戏 / 取消游戏加速**, **系统调优** and **还原系统调优** request administrator rights. Refusing never changes any of your network settings.

## Build from source

Requires [Rust](https://rustup.rs) (stable, MSVC toolchain) and the WebView2 Runtime.

```powershell
# install the Tauri CLI
cargo install tauri-cli --version "^2"

# build the installer into src-tauri/target/release/bundle/
cargo tauri build
```

Icons are generated by `build/make_tauri_icons.py` (Python + Pillow) if you need to regenerate them.

## How it works

- A small [Tauri](https://tauri.app) shell (`src-tauri/`, Rust) hosts the static UI (`src/public/`) in the system WebView2 and exposes two commands to the frontend.
- `run_action` is **async + `spawn_blocking`**: PowerShell (and a UAC prompt) can block for a minute, so it must never run on the UI thread.
- The Rust backend shells out to a PowerShell backend (`scripts/optimization.ps1`) for game scanning, route probing, Cloudflare/CloudFront optimisation, hosts writing and the system tune-up.
- **Live progress runs on two independent paths**: PowerShell appends `percent|message` to the file passed as `-ProgressFile`; a Rust poller mirrors each change into a `bb-progress` event **and** into a global that `get_progress` returns. The UI reads the polled value (every 250 ms via `invoke('get_progress')` while an action runs) and treats the event as a low-latency bonus — if `window.__TAURI__.event` were ever unavailable, `listen` would fail silently and the bar would be left showing only its own timer.
- Cloudflare download tests use the system `curl.exe` with `--ssl-no-revoke --resolve`: the TLS chain and hostname are still verified, only revocation checking is skipped.
- Adding a game touches **one place**: `$Catalog` in `optimization.ps1`. Everything else is detected automatically from Steam's local metadata.

## Safety

- Only **official Cloudflare public IP ranges** (from `https://www.cloudflare.com/ips-v4`) are ever tested or written. `cf-apply` **hard-rejects** any non-Cloudflare address before it reaches the `CF` block, and reports how many entries were refused.
- Hosts writes are wrapped in `# ==== Batter Babel <TAG> START/END` blocks (TAG = `CF`, `AMAZON`, `STEAM`, `EA`, `KLEI`, `UBISOFT`, `EPIC`, `BLIZZARD`), backed up to `hosts.batterbabel.bak`, and removable with **还原 hosts**.
- **No DNS takeover, no proxy is created, and the routing table is never modified.**
- Writing hosts only helps domains that actually use that CDN. Pointing a domain at the wrong provider's IP will make it unreachable — restore with **还原 hosts**.
- The system tune-up only touches TCP / Nagle timers, adapter power & low-latency properties, the multimedia network throttling index, and the Wi-Fi power mode. Original values are written to the log before they are changed.
- The QoS boost matches **only the selected game's executable path**; it never throttles or deprioritises anything else.

## Disclaimer

This tool is for personal, legitimate use on networks you are authorised to manage. Using `hosts` entries, QoS rules or system network settings may violate the terms of some networks or services. How you use it is your own responsibility.

## License

[MIT](LICENSE)

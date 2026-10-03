# Batter Babel

**English** | [简体中文](README.zh-CN.md)

A Windows desktop app for game network optimisation. It detects the games installed on your PC, lets you pick which one to optimise, benchmarks that game's CDN / API routes, and — with your confirmation — writes the fastest endpoints into your `hosts` file. It also ships a one-click, **game-agnostic** system network tune-up.

Everything runs locally. There is no cloud service, no account, no third-party proxy node, and no traffic relaying.

> UI language: Chinese (zh-CN). 界面为中文。

## Features

- **Scope is stated per game, by applicability — not by genre** — whenever ranking routes cannot improve the game itself, a full-width amber notice appears in the result area explaining exactly what is and is not improved. That covers CS2 / Apex / Hunt (in-match traffic is UDP to dynamically assigned servers) and equally Don't Starve Together / Terraria (multiplayer is P2P between players) — none of them talk to a rankable domain in-game.
  - The notice also covers **in-game downloads** (maps / mods / save syncing). Steam asks its Connection Manager for a **content-server IP list** and then connects to those IPs directly, and P2P transfers involve no domain at all — so hosts entries cannot speed those up. The notice points at the controls that *do* work: **Steam → Settings → Downloads → Download Region**, and 加速游戏 for latency.
  - **`HelpsGameplay`** in `$Catalog` marks the titles where it genuinely helps the game (currently only Limbus Company, whose content download and game API really are served from rankable endpoints). Leave it off and the notice appears automatically — so adding a title never requires judging its genre, only asking "does ranking these endpoints actually improve gameplay?".
- **Game detection — automatic online-game recognition, no whitelist**
  - Steam records each app's **category flags locally** in `appcache/appinfo.vdf`, and the keys are named `category_<id>` using Steam's own ids (`1` multi-player, `9` co-op, `20` MMO, `27` cross-platform multi-player, `36` online PvP, `38` online co-op). The app parses that binary KeyValues blob directly — 16-byte header (magic `0x07564428`/`0x07564429`, universe, string-table offset) → repeating `[appid u32][size u32][binary KV]` entries → trailing string table that the keys reference **by index** — and reads each installed title's online-ness **offline**. No store API, which is unreachable on many networks.
  - So **anyone who installs Battlefield, Rainbow Six Siege or anything else gets it detected automatically**. Verified on a real library: `Lossless Scaling` (a tool — only categories `10`/`62`) is filtered out, while `How to Fish` (multi-player + co-op), `CS2`, `SCP: SL` and `Crimson Desert` (Steam flags it online PvP) are listed.
  - **Only installed games are listed.** Games detected through every Steam library (`libraryfolders.vdf` + registry, parsing each `appmanifest_*.acf`) and, for standalone clients, through the uninstall registry and common install roots (`STOVE`, `Neowiz`, …). A game you cannot launch has no business appearing in the picker. Duplicate library paths are de-duplicated case-insensitively (`d:\steam` and `D:\steam` are the same library) and Steam's redistributables (appid 228980) are skipped.
- **Platform targets — every detected game is optimisable**
  - Most titles only talk to the same handful of platform services, so endpoints are defined **per platform** (`$PlatformTargets`: Steam, Ubisoft, Epic, Blizzard, EA) and every detected game inherits its platform's endpoints automatically. Rainbow Six Siege gets Ubisoft Connect endpoints; SCP: SL gets Steam's — no per-game entry needed.
  - A game only needs its own `$Catalog` entry when it has **game-specific** endpoints worth ranking. `$OnlineGameIds` (~83 well-known online titles) is only a fallback for a title the local metadata does not cover.
  - Titles with a dedicated profile:
    - **Limbus Company** — Cloudflare download CDN (`download` / `downloadcommon` / `downloadfmod .limbuscompanycdn.org`) + CloudFront API (`www` / `notice .limbuscompanyapi.com`)
    - **Counter-Strike 2** / **Hunt: Showdown 1896** — Steam API (`api.steampowered.com`, `login.steampowered.com`) + Steam CDN (`steamcdn-a.akamaihd.net`)
    - **Apex Legends** — EA account/sign-in (`accounts.ea.com`, `signin.ea.com`) + EA CDN (`origin-a.akamaihd.net`)
    - **Don't Starve Together** — Klei lobby/account (`lobby.klei.com`, `accounts.klei.com`, `login.klei.com`) + Steam API. Klei hosts matchmaking on multi-IP endpoints, so ranking them helps finding and joining sessions; play itself is P2P.
    - **Terraria** — Steam API + the Cloudflare-fronted official site (`terraria.org`, `re-logic.com`). Multiplayer is P2P (the host's own connection), which hosts entries cannot influence.
    - **Brown Dust 2** — standalone client; game CDN (`browndust2.com`, `browndust2.jp`, `cdn.neowiz.com`) + STOVE platform.
  - Game ids are validated by format, and the **only place a game is defined is `$Catalog`** in `optimization.ps1` — adding a title never requires touching the Rust layer or a `ValidateSet`.
- **加速游戏 / 取消游戏加速** (one toggle button, next to 还原 hosts) — creates a Windows QoS rule (`DSCP 46`, all network profiles) matching only the selected game's executable, and removes it again on the second click. This is the one network optimisation that helps **UDP-based online games** such as Hunt: Showdown, whose match servers are assigned dynamically and therefore cannot be helped by `hosts`.
  - **It works on every Windows edition.** Home editions have no Group Policy, so `-PolicyStore Local` always fails with "The network path was not found" (System Error 53). The backend tries `Local` first (survives reboots) and **automatically falls back to `ActiveStore`**, then registers a **logon scheduled task** that re-creates the rule after each restart — so Home users never have to remember to click again. Removing the last boost removes the task.
  - Button state survives app restarts via `%LOCALAPPDATA%\BatterBabel\boosted.json` (reading the QoS store directly requires elevation, which a plain scan does not have). The record also stores the **store** and the **boot time**, so a stale ActiveStore rule is correctly reported as inactive after a restart.
- **Cloudflare fast-IP selection** — samples a **2000-address pool** spread evenly across every official Cloudflare IPv4 range, screens it with concurrent TCP:443 handshakes (≈10 s for the whole pool), then runs real download tests (`curl --resolve`, TLS chain still verified) on the 10 curated addresses, any previously-learned fast addresses, and the 20 lowest-latency discoveries (30 in total), ranking them by measured speed.
  - **Self-learning** — every run records its fastest downloaders to `%LOCALAPPDATA%\BatterBabel\cf-good-ips.json`. An address that reaches the top few **twice** earns a permanent free pass straight into the download stage, bypassing the latency screen (which does not predict throughput). The library is capped at the 30 strongest entries.
  - **Stale-address handling** — every address that gets download-tested has its health refreshed: a win clears the failure count, a failure increments it, and **two consecutive failures evict the entry**. Curated addresses are *demoted* rather than deleted, so a dark one stays out of the free-pass list (and still carries its failure record) until it recovers. Addresses that never win are not kept at all, so exploration noise cannot crowd out the real winners.
  - **If an IP goes dark permanently** — demotions are printed in the log; delete it from `$CfCandidateIps` (or the whole `cf-good-ips.json`) to reset. Nothing else needs touching.
- **CloudFront / CDN per-domain optimisation** — collects candidate IPs from the system resolver **plus** AliDNS and DNSPod DoH, probes each candidate per domain (real TLS SNI, certificate and Host preserved), and keeps the lowest median latency.
- **Hosts writing** — writes results into per-provider blocks (`CF`, `AMAZON`, `STEAM`, `EA`, `KLEI`, `UBISOFT`, `EPIC`, `BLIZZARD`), with automatic backup (`hosts.batterbabel.bak`) and one-click restore. Anything outside the marked blocks is preserved. The `CF` block additionally refuses any address that is not an official Cloudflare range.
- **加速游戏 (in-game latency boost) — QoS DSCP 46, works on every Windows edition**
  - **Windows Home editions have no Group Policy, so `-PolicyStore Local` always fails** with "The network path was not found" (System Error 53). Verified on Windows 11 Home: the `GroupPolicy` module and `gpedit.msc` are both absent, while WMI and the QoS Packet Scheduler are fine and `ActiveStore` works perfectly.
  - The backend therefore tries **`Local` first** (survives reboots) and **falls back to `ActiveStore`** automatically. When it has to fall back it also **registers a logon scheduled task** (`BatterBabel-BoostReapply`) that re-creates the rule after every restart, so the user never has to click again. Removing the last boost removes the task.
  - Boost state lives in `%LOCALAPPDATA%\BatterBabel\boosted.json` alongside the store and the **boot time**, so a stale ActiveStore rule is correctly reported as inactive after a restart (reading `Get-NetQosPolicy` needs admin, which `scan` does not have).
  - `scan` also reports a **QoS capability probe** (is the QoS Packet Scheduler bound? will the rule be persistent?), so the UI can explain the situation before anything is clicked instead of failing afterwards.
- **System network tuning** (separate button, one click) — generic, **game-agnostic** latency fixes that benefit any online game:
  - **TCP low latency** — Nagle / delayed-ACK disabled per interface (`TcpAckFrequency`=1, `TCPNoDelay`=1, `TcpDelAckTicks`=0), so small game packets leave immediately
  - **TCP globals** — `autotuninglevel=normal`, heuristics disabled, RSS enabled
  - **Adapter low-latency properties** — interrupt moderation / energy-efficient Ethernet / flow control turned off, device power saving off
  - **Windows network throttling** — `NetworkThrottlingIndex` disabled, `SystemResponsiveness`=10
  - **Wi-Fi power** — adapter set to maximum performance
  - **Background bandwidth check** — lists bandwidth hogs (Baidu Netdisk, Thunder, video and torrent clients…)
  - Optional game-specific extra: CS2 `autoexec.cfg` network parameters, only when CS2 is installed (original backed up)
- **Restore system tuning** — before the first change, the originals are snapshotted to `%LOCALAPPDATA%\BatterBabel\tune-state.json`; the **还原系统调优** button restores every value (Nagle timers, TCP globals, adapter properties, throttling index, Wi-Fi power mode, CS2 `autoexec.cfg`) and clears the snapshot.

## Download

A pre-built Windows installer is published on the [Releases](../../releases) page:

- `Batter Babel_<version>_x64-setup.exe` — NSIS installer (desktop + start-menu shortcuts), ~1 MB.

> Requires the **WebView2 Runtime** (preinstalled on Windows 11 and on up-to-date Windows 10).

## Usage

1. Install and launch the app. It scans your games automatically and lists every **installed** game that Steam reports as having an online component.
2. Pick a game in the **游戏** dropdown — the result cards below switch to that game's CDN/API targets.
3. Click **开始优选** to rank the routes; results appear in the cards and the log, and the progress bar follows the real work (per-IP speed tests, per-domain probes).
4. Click **写入 hosts** to apply (Windows UAC prompt). Use **还原 hosts** to revert.
5. **加速游戏** toggles a Windows QoS rule (`DSCP 46`) for that game's executable — the one optimisation that also helps UDP-based match traffic. Click again (it becomes **取消游戏加速**) to remove it.
6. **系统调优** applies the generic network tuning (TCP / adapter / throttling / Wi-Fi power / background check) in one click — it is **not** tied to the selected game.
7. **还原系统调优** puts every value changed by 系统调优 back to the state captured before the first run.

Administrator elevation is requested only for **写入 hosts**, **还原 hosts**, **加速游戏 / 取消游戏加速**, **系统调优** and **还原系统调优**. Refusing it never changes your network settings.

## Build from source

Requirements: [Rust](https://rustup.rs) (stable, MSVC toolchain) and the WebView2 Runtime.

```powershell
# install the Tauri CLI
cargo install tauri-cli --version "^2"

# build the installer into src-tauri/target/release/bundle/
cargo tauri build
```

The icons are generated by `build/make_tauri_icons.py` (Python + Pillow) if you need to regenerate them.

## How it works

- A small [Tauri](https://tauri.app) shell (`src-tauri/`, Rust) hosts the static UI (`src/public/`) in the system WebView2 and exposes two commands to the frontend.
- `run_action` is **async + `spawn_blocking`**: PowerShell (and a UAC prompt) can block for a minute, so it must never run on the UI thread.
- The Rust backend shells out to a PowerShell backend (`scripts/optimization.ps1`) for game scanning, route probing, Cloudflare/CloudFront optimisation, hosts writing and the system tune-up.
- **Live progress runs on two independent paths**: PowerShell appends `percent|message` to a file passed as `-ProgressFile`; a Rust poller mirrors each change into a `bb-progress` event **and** into a global that `get_progress` returns. The UI reads the polled value (`invoke('get_progress')`, every 250 ms while an action runs) and treats the event as a low-latency bonus — if `window.__TAURI__.event` were ever unavailable, `listen` would fail silently and the bar would be left showing only its own timer.
- Cloudflare download tests use the system `curl.exe` with `--ssl-no-revoke --resolve`, which still verifies the TLS chain and hostname and only skips revocation checking.
- Adding a game touches **one place**: `$Catalog` in `optimization.ps1`. Everything else is detected automatically from Steam's local metadata.

## Safety

- Only **official Cloudflare public IP ranges** (from `https://www.cloudflare.com/ips-v4`) are ever tested or written. `cf-apply` hard-rejects any non-Cloudflare address before it reaches the `CF` block, and reports how many entries were refused.
- Hosts writes are wrapped in `# ==== Batter Babel <TAG> START/END` blocks (TAG = `CF`, `AMAZON`, `STEAM`, `EA`, `KLEI`, `UBISOFT`, `EPIC`, `BLIZZARD`), backed up to `hosts.batterbabel.bak`, and removable with **还原 hosts**.
- DNS is never taken over, no proxy is created, and the routing table is never modified.
- Writing hosts only helps domains that actually use that CDN. Pointing a domain at the wrong provider's IP will make it unreachable — revert with **还原 hosts**.
- The system tune-up only touches TCP/Nagle timers, adapter power & low-latency properties, the multimedia network throttling index, and the Wi-Fi power mode. Original values are reported in the log before they are changed.
- The QoS boost matches **only the selected game's executable path**; it does not throttle or deprioritise anything else.

## Disclaimer

This tool is for personal, legitimate use on networks you are authorised to manage. Use of `hosts` entries, QoS rules or system network settings may be against the terms of some networks or services. You are responsible for how you use it.

## License

[MIT](LICENSE)

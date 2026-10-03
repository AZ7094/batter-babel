use serde_json::Value;
use std::path::PathBuf;
use std::process::Command;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use tauri::{Emitter, Manager};

#[cfg(windows)]
use std::os::windows::process::CommandExt;

/// Do not flash a console window when spawning PowerShell.
#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

/// Latest progress reported by the running action.
///
/// This exists because the event channel is a single point of failure: if `window.__TAURI__.event`
/// is unavailable in the webview, `listen('bb-progress')` silently does nothing and the bar would
/// only ever show the frontend's own timer -- a fake progress bar. The frontend therefore POLLS
/// this state through a normal `invoke` call, which uses the same channel as every other command
/// that demonstrably works.
#[derive(Default)]
pub struct ProgressState {
    inner: Mutex<(f64, String)>,
}

static FALLBACK_PROGRESS: OnceLock<ProgressState> = OnceLock::new();

fn progress_cell() -> &'static ProgressState {
    FALLBACK_PROGRESS.get_or_init(ProgressState::default)
}

fn set_progress(percent: f64, message: &str) {
    if let Ok(mut guard) = progress_cell().inner.lock() {
        guard.0 = percent;
        guard.1 = message.to_string();
    }
}

fn current_progress() -> (f64, String) {
    match progress_cell().inner.lock() {
        Ok(guard) => (guard.0, guard.1.clone()),
        Err(_) => (0.0, String::new()),
    }
}

/// Polled by the UI while an action runs. Returns the same numbers the event would have carried.
#[tauri::command]
pub fn get_progress() -> Value {
    let (percent, message) = current_progress();
    serde_json::json!({ "percent": percent, "message": message })
}

const VALID_ACTIONS: &[&str] = &[
    "scan",
    "scan-force",
    "status",
    "probe",
    "cf-optimize",
    "optimize",
    "restore",
    "boost",
    "apply-route",
    "cf-apply",
    "cf-restore",
    "tune-system",
    "restore-tune",
];

const ELEVATED_ACTIONS: &[&str] = &[
    "optimize",
    "restore",
    "apply-route",
    "cf-apply",
    "cf-restore",
    "tune-system",
    "restore-tune",
];

/// Game ids are checked by FORMAT, not against a hardcoded list. The authoritative catalogue lives
/// in optimization.ps1 ($Catalog); keeping a second copy here silently rejected every newly added
/// game (hunt / dst / terraria all failed the old ["limbus","apex","cs2"] list).
fn is_valid_game_id(s: &str) -> bool {
    !s.is_empty()
        && s.len() <= 32
        && s.chars()
            .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '-' || c == '_')
}
const VALID_ROUTES: &[&str] = &[
    "cf-dns",
    "google-dns",
    "cf-cdn",
    "steam-api",
    "steam-cm",
    "ea",
    "ea-cdn",
    "pm-api",
];

fn resolve_script(app: &tauri::AppHandle) -> Result<PathBuf, String> {
    let resource_dir = app.path().resource_dir().map_err(|e| e.to_string())?;
    for candidate in [
        resource_dir.join("scripts").join("optimization.ps1"),
        resource_dir.join("optimization.ps1"),
    ] {
        if candidate.exists() {
            return Ok(candidate);
        }
    }
    let dev = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("scripts")
        .join("optimization.ps1");
    if dev.exists() {
        return Ok(dev);
    }
    Err("optimization.ps1 未找到".to_string())
}

fn is_ipv4(s: &str) -> bool {
    let parts: Vec<&str> = s.split('.').collect();
    if parts.len() != 4 {
        return false;
    }
    parts.iter().all(|p| {
        // Reject non-canonical octets such as "010": PowerShell's [IPAddress]::Parse reads a
        // leading zero as OCTAL, so "010.1.1.1" would silently become 8.1.1.1 by the time the
        // address reaches the elevated hosts write.
        !p.is_empty()
            && p.len() <= 3
            && p.chars().all(|c| c.is_ascii_digit())
            && !(p.len() > 1 && p.starts_with('0'))
            && p.parse::<u8>().is_ok()
    })
}

#[tauri::command]
pub async fn run_action(
    app: tauri::AppHandle,
    action: String,
    game_id: Option<String>,
    target: Option<String>,
    domains: Option<String>,
) -> Result<Value, String> {
    // Spawning PowerShell (and waiting on a UAC prompt) can block for tens of seconds.
    // Running it through spawn_blocking keeps it off the main thread, so the window
    // stays responsive instead of showing "not responding".
    tauri::async_runtime::spawn_blocking(move || {
        run_action_blocking(&app, &action, game_id, target, domains)
    })
    .await
    .map_err(|e| format!("后台任务执行失败: {e}"))?
}

fn run_action_blocking(
    app: &tauri::AppHandle,
    action: &str,
    game_id: Option<String>,
    target: Option<String>,
    domains: Option<String>,
) -> Result<Value, String> {
    if !VALID_ACTIONS.contains(&action) {
        return Err("无效的操作请求".to_string());
    }
    if let Some(g) = &game_id {
        if !is_valid_game_id(g) {
            return Err("无效的操作请求".to_string());
        }
    }
    if action == "apply-route" {
        match &target {
            Some(t) if VALID_ROUTES.contains(&t.as_str()) => {}
            _ => return Err("请选择要应用的线路".to_string()),
        }
    }
    if action == "cf-apply" {
        if let Some(t) = &target {
            if !is_ipv4(t) {
                return Err("无效的 Cloudflare IP".to_string());
            }
        }
        if let Some(d) = &domains {
            if d.len() > 4000 {
                return Err("映射列表过长".to_string());
            }
        }
    }

    let script = resolve_script(app)?;
    let needs_elevation = ELEVATED_ACTIONS.contains(&action);

    let ts = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    // A per-call counter as well as the timestamp: two invocations in the same millisecond would
    // otherwise share a result file and one could read the other's output.
    static CALL_SEQ: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    let seq = CALL_SEQ.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let tag = format!("{}-{}-{}", std::process::id(), ts, seq);

    let result_file = if needs_elevation {
        Some(std::env::temp_dir().join(format!("batter-babel-{}.json", tag)))
    } else {
        None
    };

    // The PowerShell side appends "percent|message" to this file; a poller converts every
    // change into a Tauri event so the UI progress bar reflects the real work being done.
    let progress_file = std::env::temp_dir().join(format!("batter-babel-progress-{}.txt", tag));
    let _ = std::fs::remove_file(&progress_file);

    let stop = Arc::new(AtomicBool::new(false));
    // Reset the polled state for this run so a stale value from the previous action is never read.
    set_progress(0.0, "");
    let poller = {
        let app = app.clone();
        let file = progress_file.clone();
        let stop = stop.clone();
        std::thread::spawn(move || {
            let mut last = String::new();
            while !stop.load(Ordering::Relaxed) {
                if let Ok(text) = std::fs::read_to_string(&file) {
                    let text = text.trim().to_string();
                    if !text.is_empty() && text != last {
                        last = text.clone();
                        if let Some((pct, msg)) = text.split_once('|') {
                            if let Ok(percent) = pct.trim().parse::<f64>() {
                                let message = msg.trim().to_string();
                                // Mirror into the polled state as well: the UI reads this through a
                                // normal invoke, so progress keeps working even if the webview's
                                // event API is missing.
                                set_progress(percent, &message);
                                let _ = app.emit(
                                    "bb-progress",
                                    serde_json::json!({ "percent": percent, "message": message }),
                                );
                            }
                        }
                    }
                }
                std::thread::sleep(Duration::from_millis(120));
            }
        })
    };

    let mut cmd = Command::new("powershell.exe");
    #[cfg(windows)]
    {
        cmd.creation_flags(CREATE_NO_WINDOW);
    }
    cmd.args(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"]);
    cmd.arg(&script);
    cmd.arg("-Action").arg(action);
    if let Some(g) = &game_id {
        cmd.arg("-GameId").arg(g);
    }
    if let Some(t) = &target {
        cmd.arg("-Target").arg(t);
    }
    if let Some(d) = &domains {
        cmd.arg("-Domains").arg(d);
    }
    cmd.arg("-ProgressFile").arg(&progress_file);
    if let Some(rf) = &result_file {
        cmd.arg("-ResultFile").arg(rf);
    }

    let output = cmd.output().map_err(|e| e.to_string())?;

    stop.store(true, Ordering::Relaxed);
    let _ = poller.join();
    let _ = std::fs::remove_file(&progress_file);

    let mut raw = if let Some(rf) = &result_file {
        std::fs::read_to_string(rf).unwrap_or_default()
    } else {
        String::from_utf8_lossy(&output.stdout).trim().to_string()
    };
    if let Some(rf) = &result_file {
        let _ = std::fs::remove_file(rf);
    }

    if raw.is_empty() {
        raw = String::from_utf8_lossy(&output.stderr).trim().to_string();
    }
    if raw.is_empty() {
        return Err("优化服务没有返回结果".to_string());
    }

    serde_json::from_str(&raw).map_err(|e| format!("优化服务返回格式错误: {e}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_leading_zero_octets() {
        // PowerShell's [IPAddress]::Parse reads leading zeros as OCTAL, so "010.1.1.1"
        // silently becomes 8.1.1.1 and the wrong IP would be written into hosts.
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

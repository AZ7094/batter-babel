#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod commands;

fn main() {
    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![
            commands::run_action,
            commands::get_progress
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

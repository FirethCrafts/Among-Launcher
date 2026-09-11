use serde::{Deserialize, Serialize};
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Arc;
use tauri::{AppHandle, Emitter, State};

mod config;
mod error;

use config::{LauncherConfig, SharedConfig};
use error::LauncherError;

static ID_COUNTER: AtomicU64 = AtomicU64::new(0);

fn generate_id() -> String {
    let ts = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64;
    let counter = ID_COUNTER.fetch_add(1, Ordering::Relaxed);
    format!("{:08x}{:08x}", ts.wrapping_shr(4), counter)
}

fn now_millis() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as i64
}

// ============================================================
// Game Detection
// ============================================================

mod game_detection {
    use super::*;
    use std::env;

    const AMONG_US_EXE: &str = "Among Us.exe";
    const AMONG_US_FOLDER: &str = "Among Us";

    #[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
    #[serde(rename_all = "snake_case")]
    pub enum Storefront {
        Steam,
        Epic,
        MicrosoftStore,
    }

    #[derive(Debug, Clone, Serialize, Deserialize, Default)]
    pub struct GameSearchResult {
        #[serde(skip_serializing_if = "Option::is_none")]
        pub path: Option<String>,
        #[serde(skip_serializing_if = "Option::is_none")]
        pub storefront: Option<Storefront>,
        #[serde(default)]
        pub detected_but_unavailable: bool,
    }

    pub fn find_for_storefront(storefront: Option<&Storefront>) -> GameSearchResult {
        match storefront {
            Some(Storefront::Steam) => find_steam()
                .map(|path| GameSearchResult {
                    path: Some(path),
                    storefront: Some(Storefront::Steam),
                    ..Default::default()
                })
                .unwrap_or_default(),
            Some(Storefront::Epic) => find_epic(),
            Some(Storefront::MicrosoftStore) => find_xbox(),
            None => find_with_storefront(),
        }
    }

    pub fn find_with_storefront() -> GameSearchResult {
        if let Some(path) = find_steam() {
            return GameSearchResult {
                path: Some(path),
                storefront: Some(Storefront::Steam),
                ..Default::default()
            };
        }

        let epic = find_epic();
        if epic.path.is_some() {
            return epic;
        }

        let xbox = find_xbox();
        if xbox.path.is_some() {
            return xbox;
        }

        if epic.detected_but_unavailable {
            return epic;
        }
        if xbox.detected_but_unavailable {
            return xbox;
        }

        GameSearchResult::default()
    }

    // -- Steam --

    fn find_steam() -> Option<String> {
        let libraries = find_steam_libraries();
        for library in &libraries {
            let game_path = PathBuf::from(library)
                .join("steamapps")
                .join("common")
                .join(AMONG_US_FOLDER)
                .join(AMONG_US_EXE);
            if game_path.exists() {
                return game_path.parent().map(|p| p.to_string_lossy().to_string());
            }
        }

        let fallbacks = [
            r"C:\Program Files (x86)\Steam\steamapps\common\Among Us",
            r"C:\Program Files\Steam\steamapps\common\Among Us",
            r"D:\SteamLibrary\steamapps\common\Among Us",
            r"D:\Steam\steamapps\common\Among Us",
        ];

        for path in &fallbacks {
            if Path::new(path).join(AMONG_US_EXE).exists() {
                return Some(path.to_string());
            }
        }

        None
    }

    fn find_steam_libraries() -> Vec<String> {
        let mut libraries = Vec::new();

        if let Some(steam_path) = get_steam_path_from_registry() {
            libraries.push(steam_path);
        }

        let common_paths = [
            r"C:\Program Files (x86)\Steam",
            r"C:\Program Files\Steam",
            r"D:\Steam",
            r"D:\SteamLibrary",
            r"E:\Steam",
            r"E:\SteamLibrary",
            r"F:\Steam",
            r"F:\SteamLibrary",
        ];

        for base in &common_paths {
            let config_path = Path::new(base).join("config").join("libraryfolders.vdf");
            if config_path.exists() {
                libraries.extend(parse_library_folders_vdf(&config_path));
            }
        }

        libraries.sort();
        libraries.dedup();
        libraries
    }

    #[cfg(target_os = "windows")]
    fn get_steam_path_from_registry() -> Option<String> {
        let fallback_paths = [
            r"C:\Program Files (x86)\Steam\steamapps\common\Among Us",
            r"C:\Program Files\Steam\steamapps\common\Among Us",
            r"D:\SteamLibrary\steamapps\common\Among Us",
            r"D:\Steam\steamapps\common\Among Us",
            r"E:\SteamLibrary\steamapps\common\Among Us",
        ];

        for path in &fallback_paths {
            let exe = std::path::Path::new(path).join("Among Us.exe");
            if exe.exists() {
                return Some(path.to_string());
            }
        }

        None
    }

    #[cfg(not(target_os = "windows"))]
    fn get_steam_path_from_registry() -> Option<String> {
        None
    }

    fn parse_library_folders_vdf(vdf_path: &Path) -> Vec<String> {
        let mut paths = Vec::new();
        if let Ok(content) = fs::read_to_string(vdf_path) {
            for line in content.lines() {
                let trimmed = line.trim();
                if trimmed.starts_with("\"path\"") {
                    let bytes = trimmed.as_bytes();
                    let first_quote = bytes.iter().position(|&b| b == b'"');
                    if let Some(start_pos) = first_quote {
                        let after_first = start_pos + 1;
                        if let Some(second_quote) = bytes[after_first..].iter().position(|&b| b == b'"') {
                            let inner = after_first + second_quote;
                            let third_quote = bytes[inner + 1..].iter().position(|&b| b == b'"');
                            if let Some(end_offset) = third_quote {
                                let value_start = inner + 1;
                                let value_end = value_start + end_offset;
                                let raw = &trimmed[value_start..value_end];
                                let path = raw.replace("\\\\", "\\");
                                if Path::new(&path).exists() {
                                    paths.push(path);
                                }
                            }
                        }
                    }
                }
            }
        }
        paths
    }

    // -- Epic --

    fn find_epic() -> GameSearchResult {
        if let Some(program_data) = env::var_os("PROGRAMDATA") {
            let manifests_dir = PathBuf::from(program_data)
                .join("Epic")
                .join("EpicGamesLauncher")
                .join("Data")
                .join("Manifests");

            if manifests_dir.exists() {
                let mut manifest_found = false;
                if let Ok(entries) = fs::read_dir(&manifests_dir) {
                    for entry in entries.flatten() {
                        if entry.path().extension().map(|e| e == "item").unwrap_or(false) {
                            if let Ok(content) = fs::read_to_string(entry.path()) {
                                if let Ok(item) =
                                    serde_json::from_str::<serde_json::Value>(&content)
                                {
                                    let display_name =
                                        item.get("DisplayName").and_then(|v| v.as_str()).unwrap_or("");
                                    let install_location = item
                                        .get("InstallLocation")
                                        .and_then(|v| v.as_str())
                                        .unwrap_or("");

                                    let matches_name =
                                        display_name.eq_ignore_ascii_case("Among Us");
                                    let matches_path =
                                        install_location.to_lowercase().ends_with("among us");

                                    if matches_name || matches_path {
                                        manifest_found = true;
                                        if !install_location.is_empty() {
                                            let direct =
                                                PathBuf::from(install_location).join(AMONG_US_EXE);
                                            if direct.exists() {
                                                return GameSearchResult {
                                                    path: Some(install_location.to_string()),
                                                    storefront: Some(Storefront::Epic),
                                                    ..Default::default()
                                                };
                                            }
                                            let nested = PathBuf::from(install_location)
                                                .join(AMONG_US_FOLDER)
                                                .join(AMONG_US_EXE);
                                            if nested.exists() {
                                                return GameSearchResult {
                                                    path: Some(
                                                        PathBuf::from(install_location)
                                                            .join(AMONG_US_FOLDER)
                                                            .to_string_lossy()
                                                            .to_string(),
                                                    ),
                                                    storefront: Some(Storefront::Epic),
                                                    ..Default::default()
                                                };
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                if manifest_found {
                    return GameSearchResult {
                        storefront: Some(Storefront::Epic),
                        detected_but_unavailable: true,
                        ..Default::default()
                    };
                }
            }
        }

        if let Some(local_app_data) = dirs::data_local_dir() {
            let config_path = local_app_data
                .join("Epic")
                .join("EpicGamesLauncher")
                .join("Saved")
                .join("Config")
                .join("Windows")
                .join("GameUserSettings.ini");

            if let Ok(content) = fs::read_to_string(&config_path) {
                for line in content.lines() {
                    if let Some(rest) = line.strip_prefix("DefaultInstallLocation=") {
                        let install_dir = rest.trim().trim_matches('"');
                        if !install_dir.is_empty() {
                            let game_path = Path::new(install_dir)
                                .join(AMONG_US_FOLDER)
                                .join(AMONG_US_EXE);
                            if game_path.exists() {
                                return GameSearchResult {
                                    path: Some(
                                        Path::new(install_dir)
                                            .join(AMONG_US_FOLDER)
                                            .to_string_lossy()
                                            .to_string(),
                                    ),
                                    storefront: Some(Storefront::Epic),
                                    ..Default::default()
                                };
                            }
                        }
                    }
                }
            }
        }

        let epic_fallbacks = [
            r"C:\Program Files\Epic Games\Among Us",
            r"D:\Epic Games\Among Us",
            r"E:\Epic Games\Among Us",
        ];

        for path in &epic_fallbacks {
            if Path::new(path).join(AMONG_US_EXE).exists() {
                return GameSearchResult {
                    path: Some(path.to_string()),
                    storefront: Some(Storefront::Epic),
                    ..Default::default()
                };
            }
        }

        GameSearchResult::default()
    }

    // -- Xbox / MS Store --

    fn find_xbox() -> GameSearchResult {
        for drive in ['C', 'D', 'E', 'F', 'G'] {
            let root = format!("{}:\\", drive);
            if !Path::new(&root).exists() {
                continue;
            }

            for folder_name in ["Among Us", "AmongUs"] {
                let candidates = [
                    PathBuf::from(&root).join(folder_name).join(AMONG_US_EXE),
                    PathBuf::from(&root)
                        .join(folder_name)
                        .join("Content")
                        .join(AMONG_US_EXE),
                    PathBuf::from(&root)
                        .join("XboxGames")
                        .join(folder_name)
                        .join("Content")
                        .join(AMONG_US_EXE),
                ];

                for candidate in &candidates {
                    if candidate.exists() {
                        return GameSearchResult {
                            path: candidate.parent().map(|p| p.to_string_lossy().to_string()),
                            storefront: Some(Storefront::MicrosoftStore),
                            ..Default::default()
                        };
                    }
                }
            }
        }

        if let Ok(program_files) = env::var("PROGRAMFILES") {
            let windows_apps = Path::new(&program_files).join("WindowsApps");
            if windows_apps.exists() {
                if let Ok(entries) = fs::read_dir(&windows_apps) {
                    let mut innersloth_dirs: Vec<_> = entries
                        .flatten()
                        .filter(|e| {
                            e.file_name()
                                .to_string_lossy()
                                .to_lowercase()
                                .starts_with("innersloth")
                        })
                        .collect();
                    innersloth_dirs.sort_by(|a, b| {
                        b.metadata()
                            .and_then(|m| m.modified())
                            .unwrap_or(std::time::SystemTime::UNIX_EPOCH)
                            .cmp(
                                &a.metadata()
                                    .and_then(|m| m.modified())
                                    .unwrap_or(std::time::SystemTime::UNIX_EPOCH),
                            )
                    });
                    for entry in &innersloth_dirs {
                        let game_path = entry.path().join(AMONG_US_EXE);
                        if game_path.exists() {
                            return GameSearchResult {
                                path: Some(entry.path().to_string_lossy().to_string()),
                                storefront: Some(Storefront::MicrosoftStore),
                                ..Default::default()
                            };
                        }
                    }
                }
            }
        }

        if let Some(local_app_data) = dirs::data_local_dir() {
            let packages_dir = local_app_data.join("Packages");
            if packages_dir.exists() {
                if let Ok(entries) = fs::read_dir(&packages_dir) {
                    let mut inner_sloth_pkgs: Vec<_> = entries
                        .flatten()
                        .filter(|e| {
                            e.file_name()
                                .to_string_lossy()
                                .to_lowercase()
                                .starts_with("innersloth.llc-")
                        })
                        .collect();
                    inner_sloth_pkgs.sort_by(|a, b| {
                        b.metadata()
                            .and_then(|m| m.modified())
                            .unwrap_or(std::time::SystemTime::UNIX_EPOCH)
                            .cmp(
                                &a.metadata()
                                    .and_then(|m| m.modified())
                                    .unwrap_or(std::time::SystemTime::UNIX_EPOCH),
                            )
                    });
                    for pkg in &inner_sloth_pkgs {
                        let game_path = pkg
                            .path()
                            .join("LocalCache")
                            .join("Local")
                            .join("Microsoft")
                            .join("WindowsApps")
                            .join(AMONG_US_EXE);
                        if game_path.exists() {
                            return GameSearchResult {
                                path: game_path.parent().map(|p| p.to_string_lossy().to_string()),
                                storefront: Some(Storefront::MicrosoftStore),
                                ..Default::default()
                            };
                        }
                    }
                }
            }
        }

        GameSearchResult::default()
    }

    fn _is_installed_from_ms_store() -> bool {
        if let Some(local_app_data) = dirs::data_local_dir() {
            let packages_dir = local_app_data.join("Packages");
            if packages_dir.exists() {
                if let Ok(entries) = fs::read_dir(&packages_dir) {
                    return entries.flatten().any(|e| {
                        e.file_name()
                            .to_string_lossy()
                            .to_lowercase()
                            .starts_with("innersloth.llc-")
                    });
                }
            }
        }
        false
    }
}

// ============================================================
// IPC Pipe Server
// ============================================================

mod ipc {
    use super::*;
    use serde_json::Value;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};
    use tokio::sync::mpsc;

    const PIPE_NAME: &str = r"\\.\pipe\AmongLauncher.IPC";
    const HEADER_SIZE: usize = 4;
    const MAX_MESSAGE_SIZE: usize = 1024 * 1024;

    #[derive(Debug, Clone, Serialize, Deserialize)]
    pub struct IpcEnvelope {
        #[serde(rename = "type")]
        pub msg_type: String,
        #[serde(default)]
        pub id: String,
        #[serde(default = "default_timestamp")]
        pub timestamp: i64,
        #[serde(skip_serializing_if = "Option::is_none")]
        pub payload: Option<Value>,
    }

    fn default_timestamp() -> i64 {
        now_millis()
    }

    #[derive(Clone)]
    pub struct PipeServerHandle {
        write_tx: Arc<tokio::sync::Mutex<Option<mpsc::Sender<String>>>>,
        connected: Arc<AtomicBool>,
    }

    impl PipeServerHandle {
        pub fn new() -> Self {
            Self {
                write_tx: Arc::new(tokio::sync::Mutex::new(None)),
                connected: Arc::new(AtomicBool::new(false)),
            }
        }

        pub async fn send(&self, json: &str) -> Result<(), String> {
            let tx = {
                let guard = self.write_tx.lock().await;
                guard.clone()
            };
            if let Some(tx) = tx {
                tx.send(json.to_string())
                    .await
                    .map_err(|e| format!("Send failed: {}", e))
            } else {
                Err("Not connected".to_string())
            }
        }

        pub async fn send_envelope(
            &self,
            msg_type: &str,
            payload: Option<Value>,
        ) -> Result<(), String> {
            let envelope = IpcEnvelope {
                msg_type: msg_type.to_string(),
                id: generate_id(),
                timestamp: now_millis(),
                payload,
            };
            let json = serde_json::to_string(&envelope).map_err(|e| e.to_string())?;
            self.send(&json).await
        }

        fn _is_connected(&self) -> bool {
            self.connected.load(Ordering::SeqCst)
        }

        pub fn set_connected(&self, val: bool) {
            self.connected.store(val, Ordering::SeqCst);
        }

        pub async fn set_sender(&self, tx: mpsc::Sender<String>) {
            *self.write_tx.lock().await = Some(tx);
        }
    }

    pub async fn start_pipe_server(app: AppHandle, handle: PipeServerHandle) {
        tokio::spawn(async move {
            let mut first_instance = true;
            loop {
                let (tx, mut rx) = mpsc::channel::<String>(64);
                handle.set_sender(tx).await;

                let mut options = ServerOptions::new();
                options.first_pipe_instance(first_instance);
                first_instance = false;

                match options.create(PIPE_NAME) {
                    Ok(server) => {
                        eprintln!("[PipeServer] Listening on '{}'...", PIPE_NAME);
                        match server.connect().await {
                            Ok(()) => {
                                eprintln!("[PipeServer] Client connected!");
                                handle.set_connected(true);
                                let _ = app.emit("ipc:client-connected", ());
                                run_connection(server, &app, &mut rx).await;
                                handle.set_connected(false);
                                let _ = app.emit("ipc:client-disconnected", ());
                                eprintln!("[PipeServer] Client disconnected.");
                            }
                            Err(e) => {
                                eprintln!("[PipeServer] Connect error: {}", e);
                            }
                        }
                    }
                    Err(e) => {
                        eprintln!("[PipeServer] Create pipe error: {}", e);
                        tokio::time::sleep(std::time::Duration::from_secs(1)).await;
                    }
                }
            }
        });
    }

    async fn run_connection(
        mut server: NamedPipeServer,
        app: &AppHandle,
        rx: &mut mpsc::Receiver<String>,
    ) {
        loop {
            tokio::select! {
                result = read_message(&mut server) => {
                    match result {
                        Ok(Some(json)) => {
                            if let Ok(envelope) = serde_json::from_str::<IpcEnvelope>(&json) {
                                eprintln!(
                                    "[PipeServer] Received: type={}, id={}",
                                    envelope.msg_type, envelope.id
                                );
                                let _ = app.emit("ipc:message", &envelope);
                            }
                        }
                        Ok(None) => break,
                        Err(e) => {
                            eprintln!("[PipeServer] Read error: {}", e);
                            break;
                        }
                    }
                }
                Some(msg) = rx.recv() => {
                    if let Err(e) = write_message(&mut server, &msg).await {
                        eprintln!("[PipeServer] Write error: {}", e);
                        break;
                    }
                }
            }
        }
    }

    async fn read_message(server: &mut NamedPipeServer) -> Result<Option<String>, io::Error> {
        let mut header = [0u8; HEADER_SIZE];
        match server.read_exact(&mut header).await {
            Ok(_) => {}
            Err(e) if e.kind() == io::ErrorKind::UnexpectedEof => return Ok(None),
            Err(e) => return Err(e),
        }

        let length = u32::from_le_bytes(header) as usize;
        if length == 0 || length > MAX_MESSAGE_SIZE {
            return Ok(None);
        }

        let mut buffer = vec![0u8; length];
        server.read_exact(&mut buffer).await?;
        Ok(Some(String::from_utf8_lossy(&buffer).to_string()))
    }

    async fn write_message(server: &mut NamedPipeServer, json: &str) -> Result<(), io::Error> {
        let data = json.as_bytes();
        let header = (data.len() as u32).to_le_bytes();
        server.write_all(&header).await?;
        server.write_all(data).await?;
        server.flush().await
    }
}

// ============================================================
// App State & Tauri Commands
// ============================================================

pub struct AppState {
    pub config: SharedConfig,
    pipe_handle: Arc<tokio::sync::Mutex<Option<ipc::PipeServerHandle>>>,
}

#[tauri::command]
async fn detect_game(
    _state: State<'_, AppState>,
    storefront: Option<String>,
) -> Result<game_detection::GameSearchResult, LauncherError> {
    let sf = storefront.as_deref().and_then(|s| match s {
        "steam" => Some(game_detection::Storefront::Steam),
        "epic" => Some(game_detection::Storefront::Epic),
        "microsoft_store" => Some(game_detection::Storefront::MicrosoftStore),
        _ => None,
    });
    Ok(game_detection::find_for_storefront(sf.as_ref()))
}

#[tauri::command]
async fn install_game(
    _state: State<'_, AppState>,
    _storefront: String,
) -> Result<String, LauncherError> {
    Err(LauncherError::InstallFailed(
        "Install not yet implemented".to_string(),
    ))
}

#[tauri::command]
async fn launch_game(
    _state: State<'_, AppState>,
    game_path: String,
) -> Result<String, LauncherError> {
    let exe = Path::new(&game_path).join("Among Us.exe");
    let game_path = game_path.clone();
    tokio::task::spawn_blocking(move || {
        if !exe.exists() {
            return Err(LauncherError::GameNotFound);
        }
        std::process::Command::new(&exe)
            .current_dir(&game_path)
            .spawn()
            .map_err(|e| LauncherError::InstallFailed(format!("Failed to launch: {}", e)))?;
        Ok("Game launched".to_string())
    })
    .await
    .map_err(|e| LauncherError::Ipc(format!("Task join error: {}", e)))?
}

#[tauri::command]
async fn get_mods(state: State<'_, AppState>) -> Result<Vec<config::ModEntry>, LauncherError> {
    let config = state.config.read().await;
    Ok(config
        .profiles
        .iter()
        .flat_map(|p| p.mods.clone())
        .collect())
}

#[tauri::command]
async fn read_config(state: State<'_, AppState>) -> Result<LauncherConfig, LauncherError> {
    Ok(state.config.read().await.clone())
}

#[tauri::command]
async fn write_config(
    state: State<'_, AppState>,
    new_config: LauncherConfig,
    config_debouncer: State<'_, config::ConfigDebouncer>,
) -> Result<(), LauncherError> {
    {
        let mut config = state.config.write().await;
        *config = new_config;
    }
    config_debouncer.request_save().await;
    Ok(())
}

#[tauri::command]
async fn send_ipc_message(
    state: State<'_, AppState>,
    msg_type: String,
    payload: Option<serde_json::Value>,
) -> Result<(), LauncherError> {
    let handle = state.pipe_handle.lock().await;
    if let Some(ref h) = *handle {
        h.send_envelope(&msg_type, payload)
            .await
            .map_err(|e| LauncherError::Ipc(e))
    } else {
        Err(LauncherError::Ipc("Pipe server not initialized".to_string()))
    }
}

#[tauri::command]
async fn stop_game(app: AppHandle) -> Result<(), LauncherError> {
    #[cfg(target_os = "windows")]
    {
        use std::process::Command;
        let output = Command::new("tasklist")
            .args(["/FI", "IMAGENAME eq Among Us.exe", "/FO", "CSV", "/NH"])
            .output()
            .map_err(|e| LauncherError::InstallFailed(format!("Failed to run tasklist: {}", e)))?;

        let stdout = String::from_utf8_lossy(&output.stdout);
        for line in stdout.lines() {
            if line.contains("Among Us.exe") {
                let pid = line
                    .split(',')
                    .nth(1)
                    .and_then(|s| s.trim_matches('"').parse::<u32>().ok());
                if let Some(pid) = pid {
                    Command::new("taskkill")
                        .args(["/PID", &pid.to_string(), "/F"])
                        .output()
                        .map_err(|e| LauncherError::InstallFailed(format!("Failed to kill process: {}", e)))?;
                }
            }
        }
    }

    let _ = app.emit("game-stopped", ());
    Ok(())
}

#[tauri::command]
async fn get_install_status(game_path: String) -> Result<InstallStatus, LauncherError> {
    let game_dir = std::path::Path::new(&game_path);
    let bepinex_installed = game_dir.join("BepInEx").exists();
    let among_api_installed = game_dir.join("BepInEx/Plugins/AmongApi.dll").exists();
    Ok(InstallStatus {
        bepinex_installed,
        among_api_installed,
    })
}

#[derive(serde::Serialize)]
struct InstallStatus {
    bepinex_installed: bool,
    among_api_installed: bool,
}

#[tauri::command]
async fn get_storefront(state: State<'_, AppState>) -> Result<Option<String>, LauncherError> {
    Ok(state.config.read().await.storefront.clone())
}

#[tauri::command]
async fn set_storefront(
    state: State<'_, AppState>,
    storefront: String,
    config_debouncer: State<'_, config::ConfigDebouncer>,
) -> Result<(), LauncherError> {
    {
        let mut config = state.config.write().await;
        config.storefront = Some(storefront);
    }
    config_debouncer.request_save().await;
    Ok(())
}

// ============================================================
// Entry Point
// ============================================================

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let shared_config = config::new_shared_config();
    let debouncer = config::ConfigDebouncer::new(shared_config.clone());
    let pipe_handle = ipc::PipeServerHandle::new();

    let state = AppState {
        config: shared_config,
        pipe_handle: Arc::new(tokio::sync::Mutex::new(Some(pipe_handle.clone()))),
    };

    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_fs::init())
        .plugin(tauri_plugin_dialog::init())
        .manage(state)
        .manage(debouncer)
        .invoke_handler(tauri::generate_handler![
            detect_game,
            install_game,
            launch_game,
            stop_game,
            get_mods,
            read_config,
            write_config,
            get_install_status,
            send_ipc_message,
            get_storefront,
            set_storefront,
        ])
        .setup(move |app| {
            let app_handle = app.handle().clone();
            let rt = tokio::runtime::Handle::current();
            rt.spawn(async move {
                ipc::start_pipe_server(app_handle, pipe_handle).await;
            });
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

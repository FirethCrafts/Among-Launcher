use futures_util::StreamExt;
use serde::{Deserialize, Serialize};
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Arc;
use tauri::{AppHandle, Emitter, Manager, State};

mod auth;
mod config;
mod error;
mod installer;
mod ipc_handler;
mod lobby;
mod lobby_backend;
mod lobby_ws;
mod mod_sync;
mod version_checker;

use config::{LauncherConfig, SharedConfig};
use error::LauncherError;
use lobby::SharedLobbyState;

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
                                let msg: ipc_handler::IpcMessage = envelope.into();
                                ipc_handler::handle_ipc_message(app, msg);
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
    pub lobby_state: SharedLobbyState,
    pipe_handle: Arc<tokio::sync::Mutex<Option<ipc::PipeServerHandle>>>,
    pub oauth_code: std::sync::Mutex<Option<String>>,
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
    app: AppHandle,
    state: State<'_, AppState>,
    config_debouncer: State<'_, config::ConfigDebouncer>,
    game_path: String,
    storefront: String,
) -> Result<(), LauncherError> {
    let (dest, needs_save) = {
        let config = state.config.read().await;
        let needs_save = config.modded_install_path.trim().is_empty();
        (config.effective_modded_path(), needs_save)
    };

    if needs_save {
        {
            let mut config = state.config.write().await;
            config.modded_install_path = dest.clone();
        }
        config_debouncer.request_save().await;
    }

    let _ = app.emit(
        "install-progress",
        serde_json::json!({ "stage": "copying", "progress": 0, "total": 0 }),
    );
    installer::copy_game(&game_path, &dest, app.clone()).await?;

    installer::download_bepinex(&dest, &storefront, &app).await?;

    installer::download_among_api(&dest, &app).await?;

    if storefront == "steam" {
        let dest_path = dest.clone();
        let _ = tokio::task::spawn_blocking(move || {
            std::fs::write(
                std::path::Path::new(&dest_path).join("steam_appid.txt"),
                "945360",
            )
        })
        .await;
    }

    let _ = app.emit(
        "install-progress",
        serde_json::json!({ "stage": "complete", "progress": 1, "total": 1 }),
    );
    Ok(())
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

#[derive(serde::Serialize)]
struct FilesystemMod {
    name: String,
    version: Option<String>,
}

#[tauri::command]
async fn get_filesystem_mods(game_path: String) -> Result<Vec<FilesystemMod>, LauncherError> {
    let plugins_dir = Path::new(&game_path).join("BepInEx").join("Plugins");
    let mut mods = Vec::new();

    if !plugins_dir.exists() {
        return Ok(mods);
    }

    let entries = fs::read_dir(&plugins_dir)
        .map_err(|e| LauncherError::InstallFailed(format!("Failed to read Plugins dir: {}", e)))?;

    for entry in entries.flatten() {
        let path = entry.path();
        if path.extension().map(|e| e == "dll").unwrap_or(false) {
            let name = path
                .file_stem()
                .and_then(|s| s.to_str())
                .unwrap_or("Unknown")
                .to_string();
            mods.push(FilesystemMod {
                name,
                version: None,
            });
        }
    }

    Ok(mods)
}

#[tauri::command]
async fn import_mod(game_path: String, mod_paths: Vec<String>) -> Result<(), LauncherError> {
    let plugins_dir = Path::new(&game_path).join("BepInEx").join("Plugins");

    if !plugins_dir.exists() {
        fs::create_dir_all(&plugins_dir)
            .map_err(|e| LauncherError::InstallFailed(format!("Failed to create Plugins dir: {}", e)))?;
    }

    for mod_path_str in &mod_paths {
        let src = Path::new(mod_path_str);
        let file_name = src
            .file_name()
            .ok_or_else(|| LauncherError::InstallFailed(format!("Invalid path: {}", mod_path_str)))?;
        let dest = plugins_dir.join(file_name);
        fs::copy(src, &dest).map_err(|e| {
            LauncherError::InstallFailed(format!(
                "Failed to copy {} to Plugins: {}",
                file_name.to_string_lossy(),
                e
            ))
        })?;
    }

    Ok(())
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

        if !output.status.success() {
            let stderr = String::from_utf8_lossy(&output.stderr);
            eprintln!("[stop_game] tasklist failed: {}", stderr);
            return Err(LauncherError::InstallFailed(format!(
                "tasklist exited with code {:?}",
                output.status.code()
            )));
        }

        let stdout = String::from_utf8_lossy(&output.stdout);
        let mut killed = false;
        for line in stdout.lines() {
            if line.contains("Among Us.exe") {
                let pid = line
                    .split(',')
                    .nth(1)
                    .and_then(|s| s.trim_matches('"').parse::<u32>().ok());
                if let Some(pid) = pid {
                    let kill_output = Command::new("taskkill")
                        .args(["/PID", &pid.to_string(), "/F"])
                        .output()
                        .map_err(|e| LauncherError::InstallFailed(format!("Failed to kill process: {}", e)))?;

                    if !kill_output.status.success() {
                        let stderr = String::from_utf8_lossy(&kill_output.stderr);
                        eprintln!(
                            "[stop_game] taskkill /PID {} failed: {}",
                            pid, stderr
                        );
                        return Err(LauncherError::InstallFailed(format!(
                            "taskkill failed for PID {}: {}",
                            pid, stderr
                        )));
                    }
                    killed = true;
                }
            }
        }
        if !killed {
            eprintln!("[stop_game] Among Us.exe not found in process list");
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
async fn browse_files(path: String) -> Result<(), LauncherError> {
    let target = std::path::Path::new(&path)
        .parent()
        .map(|p| p.to_path_buf())
        .unwrap_or_else(|| std::path::PathBuf::from(&path));
    open::that(&target).map_err(|e| LauncherError::Filesystem(e.to_string()))?;
    Ok(())
}

#[derive(serde::Serialize)]
struct ModEntry {
    name: String,
    filename: String,
    size: u64,
    path: String,
}

#[tauri::command]
async fn get_mod_list(game_path: String) -> Result<Vec<ModEntry>, LauncherError> {
    tokio::task::spawn_blocking(move || {
        let plugins_dir = std::path::Path::new(&game_path).join("BepInEx").join("Plugins");
        if !plugins_dir.exists() {
            return Ok(vec![]);
        }
        let mut mods = Vec::new();
        for entry in std::fs::read_dir(&plugins_dir)
            .map_err(|e| LauncherError::Filesystem(e.to_string()))?
        {
            let entry = entry.map_err(|e| LauncherError::Filesystem(e.to_string()))?;
            let path = entry.path();
            if path.extension() == Some(std::ffi::OsStr::new("dll")) {
                let metadata = entry
                    .metadata()
                    .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
                mods.push(ModEntry {
                    name: path
                        .file_stem()
                        .unwrap_or_default()
                        .to_string_lossy()
                        .to_string(),
                    filename: path
                        .file_name()
                        .unwrap_or_default()
                        .to_string_lossy()
                        .to_string(),
                    size: metadata.len(),
                    path: path.to_string_lossy().to_string(),
                });
            }
        }
        Ok(mods)
    })
    .await
    .map_err(|e| LauncherError::InstallFailed(format!("Task join error: {}", e)))?
}

#[tauri::command]
async fn remove_mod(game_path: String, filename: String) -> Result<(), LauncherError> {
    let plugins_dir = std::path::Path::new(&game_path).join("BepInEx").join("Plugins");
    let target = plugins_dir.join(&filename);

    if !target.exists() {
        return Err(LauncherError::Filesystem(format!(
            "Mod file not found: {}",
            filename
        )));
    }

    tokio::task::spawn_blocking(move || {
        std::fs::remove_file(&target).map_err(|e| LauncherError::Filesystem(e.to_string()))
    })
    .await
    .map_err(|e| LauncherError::InstallFailed(format!("Task join error: {}", e)))?
}

// ============================================================
// Profile Management
// ============================================================

#[tauri::command]
async fn save_profile(
    state: State<'_, AppState>,
    config_debouncer: State<'_, config::ConfigDebouncer>,
    name: String,
) -> Result<(), LauncherError> {
    let current_mods: Vec<config::ModEntry> = {
        let config = state.config.read().await;
        config
            .profiles
            .iter()
            .flat_map(|p| p.mods.clone())
            .collect()
    };

    {
        let mut config = state.config.write().await;
        if let Some(existing) = config.profiles.iter_mut().find(|p| p.name == name) {
            existing.mods = current_mods;
        } else {
            config.profiles.push(config::ModProfile {
                name,
                mods: current_mods,
            });
        }
    }
    config_debouncer.request_save().await;
    Ok(())
}

#[tauri::command]
async fn apply_profile(
    app: AppHandle,
    state: State<'_, AppState>,
    game_path: String,
    config_debouncer: State<'_, config::ConfigDebouncer>,
    name: String,
) -> Result<(), LauncherError> {
    let required_mods = {
        let config = state.config.read().await;
        config
            .profiles
            .iter()
            .find(|p| p.name == name)
            .ok_or_else(|| LauncherError::Config(format!("Profile '{}' not found", name)))?
            .mods
            .clone()
    };

    {
        let mut config = state.config.write().await;
        config.profiles.clear();
        config.profiles.push(config::ModProfile {
            name,
            mods: required_mods.clone(),
        });
    }
    config_debouncer.request_save().await;

    mod_sync::sync_mods(
        std::path::Path::new(&game_path),
        &required_mods,
        &app,
    )
    .await?;

    Ok(())
}

#[tauri::command]
async fn delete_profile(
    state: State<'_, AppState>,
    config_debouncer: State<'_, config::ConfigDebouncer>,
    name: String,
) -> Result<(), LauncherError> {
    let mut config = state.config.write().await;
    let before = config.profiles.len();
    config.profiles.retain(|p| p.name != name);
    if config.profiles.len() == before {
        return Err(LauncherError::Config(format!(
            "Profile '{}' not found",
            name
        )));
    }
    drop(config);
    config_debouncer.request_save().await;
    Ok(())
}

#[tauri::command]
async fn list_profiles(state: State<'_, AppState>) -> Result<Vec<config::ModProfile>, LauncherError> {
    let config = state.config.read().await;
    Ok(config.profiles.clone())
}

// ============================================================
// Library Management
// ============================================================

#[tauri::command]
async fn list_library(state: State<'_, AppState>) -> Result<Vec<config::LibraryEntry>, LauncherError> {
    let config = state.config.read().await;
    Ok(config.library.clone())
}

#[tauri::command]
async fn add_to_library(
    state: State<'_, AppState>,
    config_debouncer: State<'_, config::ConfigDebouncer>,
    source_path: String,
) -> Result<(), LauncherError> {
    let source = std::path::Path::new(&source_path);
    if !source.exists() {
        return Err(LauncherError::Filesystem(format!(
            "Source file not found: {}",
            source_path
        )));
    }

    let filename = source
        .file_name()
        .ok_or_else(|| LauncherError::Filesystem("Invalid source path".into()))?
        .to_string_lossy()
        .to_string();

    let lib_dir = dirs::data_local_dir()
        .unwrap_or_else(|| std::path::PathBuf::from("."))
        .join("AmongLauncher")
        .join("Library");

    let lib_dir_clone = lib_dir.clone();
    let filename_clone = filename.clone();
    let source_clone = source_path.clone();

    tokio::task::spawn_blocking(move || {
        std::fs::create_dir_all(&lib_dir_clone)
            .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
        let dest = lib_dir_clone.join(&filename_clone);
        std::fs::copy(&source_clone, &dest)
            .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
        Ok::<(), LauncherError>(())
    })
    .await
    .map_err(|e| LauncherError::InstallFailed(format!("Task join error: {}", e)))??;

    {
        let mut config = state.config.write().await;
        let dest_path = lib_dir.join(&filename).to_string_lossy().to_string();
        if !config.library.iter().any(|e| e.path == dest_path) {
            config.library.push(config::LibraryEntry {
                path: dest_path,
                storefront: None,
            });
        }
    }
    config_debouncer.request_save().await;
    Ok(())
}

#[tauri::command]
async fn remove_from_library(
    state: State<'_, AppState>,
    config_debouncer: State<'_, config::ConfigDebouncer>,
    filename: String,
) -> Result<(), LauncherError> {
    let lib_dir = dirs::data_local_dir()
        .unwrap_or_else(|| std::path::PathBuf::from("."))
        .join("AmongLauncher")
        .join("Library");

    let file_path = lib_dir.join(&filename);
    if file_path.exists() {
        let fp = file_path.clone();
        tokio::task::spawn_blocking(move || {
            std::fs::remove_file(&fp).map_err(|e| LauncherError::Filesystem(e.to_string()))
        })
        .await
        .map_err(|e| LauncherError::InstallFailed(format!("Task join error: {}", e)))??;
    }

    {
        let mut config = state.config.write().await;
        let path_str = file_path.to_string_lossy().to_string();
        config.library.retain(|e| e.path != path_str);
    }
    config_debouncer.request_save().await;
    Ok(())
}

#[tauri::command]
async fn install_from_library(
    state: State<'_, AppState>,
    config_debouncer: State<'_, config::ConfigDebouncer>,
    game_path: String,
    filename: String,
) -> Result<(), LauncherError> {
    let lib_dir = dirs::data_local_dir()
        .unwrap_or_else(|| std::path::PathBuf::from("."))
        .join("AmongLauncher")
        .join("Library");

    let source = lib_dir.join(&filename);
    if !source.exists() {
        return Err(LauncherError::Filesystem(format!(
            "Library mod not found: {}",
            filename
        )));
    }

    let plugins_dir = std::path::Path::new(&game_path).join("BepInEx").join("Plugins");
    let source_clone = source.clone();
    let plugins_clone = plugins_dir.clone();
    let filename_clone = filename.clone();

    tokio::task::spawn_blocking(move || {
        std::fs::create_dir_all(&plugins_clone)
            .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
        let dest = plugins_clone.join(&filename_clone);
        std::fs::copy(&source_clone, &dest)
            .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
        Ok::<(), LauncherError>(())
    })
    .await
    .map_err(|e| LauncherError::InstallFailed(format!("Task join error: {}", e)))??;

    let name = std::path::Path::new(&filename)
        .file_stem()
        .unwrap_or_default()
        .to_string_lossy()
        .to_string();

    {
        let mut config = state.config.write().await;
        if !config.profiles.iter().any(|p| p.mods.iter().any(|m| m.name == name)) {
            if let Some(first_profile) = config.profiles.first_mut() {
                first_profile.mods.push(config::ModEntry {
                    name,
                    version: None,
                    file_hash: None,
                    download_url: None,
                });
            }
        }
    }
    config_debouncer.request_save().await;
    Ok(())
}

// ============================================================
// Preset Mods
// ============================================================

#[derive(serde::Serialize, serde::Deserialize, Clone)]
struct PresetMod {
    name: String,
    repo_url: String,
    description: Option<String>,
    filename: String,
}

#[tauri::command]
async fn get_preset_mods() -> Result<Vec<PresetMod>, LauncherError> {
    let presets = vec![
        PresetMod {
            name: "AmongApi".to_string(),
            repo_url: "https://github.com/FirethCrafts/Among-Launcher/releases/latest/download/AmongApi.dll".to_string(),
            description: Some("Core API for Among Us modding".to_string()),
            filename: "AmongApi.dll".to_string(),
        },
    ];
    Ok(presets)
}

#[tauri::command]
async fn install_preset_mod(
    state: State<'_, AppState>,
    config_debouncer: State<'_, config::ConfigDebouncer>,
    game_path: String,
    repo_url: String,
) -> Result<(), LauncherError> {
    let plugins_dir = std::path::Path::new(&game_path).join("BepInEx").join("Plugins");
    let plugins_dir_clone = plugins_dir.clone();
    tokio::task::spawn_blocking(move || {
        std::fs::create_dir_all(&plugins_dir_clone)
            .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
        Ok::<(), LauncherError>(())
    })
    .await
    .map_err(|e| LauncherError::InstallFailed(format!("Task join error: {}", e)))??;

    let client = reqwest::Client::new();
    let resp = client
        .get(&repo_url)
        .send()
        .await
        .map_err(|e| LauncherError::Network(e.to_string()))?;

    let mut bytes = Vec::new();
    let mut stream = resp.bytes_stream();

    while let Some(chunk) = stream.next().await {
        let chunk = chunk.map_err(|e| LauncherError::Network(e.to_string()))?;
        bytes.extend_from_slice(&chunk);
    }

    let filename = repo_url
        .split('/')
        .last()
        .unwrap_or("mod.dll")
        .to_string();
    let name = std::path::Path::new(&filename)
        .file_stem()
        .unwrap_or_default()
        .to_string_lossy()
        .to_string();

    let dest = plugins_dir.join(&filename);

    tokio::task::spawn_blocking(move || {
        std::fs::write(&dest, &bytes)
            .map_err(|e| LauncherError::Filesystem(e.to_string()))
    })
    .await
    .map_err(|e| LauncherError::InstallFailed(format!("Task join error: {}", e)))??;

    {
        let mut config = state.config.write().await;
        let already_has = config
            .profiles
            .iter()
            .any(|p| p.mods.iter().any(|m| m.name == name));
        if !already_has {
            if let Some(first_profile) = config.profiles.first_mut() {
                first_profile.mods.push(config::ModEntry {
                    name,
                    version: None,
                    file_hash: None,
                    download_url: Some(repo_url),
                });
            }
        }
    }
    config_debouncer.request_save().await;
    Ok(())
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

#[tauri::command]
async fn post_lobby(state: State<'_, AppState>) -> Result<(), LauncherError> {
    let (code, region, max_players, token) = {
        let config = state.config.read().await;
        let lobby = state.lobby_state.read().await;
        let code = lobby
            .code
            .clone()
            .ok_or_else(|| LauncherError::Lobby("No lobby to post".into()))?;
        let token = config
            .discord_access_token
            .clone();
        if token.is_empty() {
            return Err(LauncherError::Auth("Not logged in".into()));
        }
        (
            code,
            lobby.region.clone().unwrap_or_else(|| "NA".into()),
            lobby.max_players.unwrap_or(10),
            token,
        )
    };

    let client = lobby_backend::LobbyBackendClient::new(token.clone());
    client.create_lobby(&code, &region, max_players).await?;

    {
        let mut lobby = state.lobby_state.write().await;
        lobby.posted = true;
        lobby.start_heartbeat(code, token).await;
    }
    Ok(())
}

#[tauri::command]
async fn disband_lobby(state: State<'_, AppState>) -> Result<(), LauncherError> {
    let (code, token) = {
        let config = state.config.read().await;
        let lobby = state.lobby_state.read().await;
        let code = lobby
            .code
            .clone()
            .ok_or_else(|| LauncherError::Lobby("No lobby to disband".into()))?;
        let token = config.discord_access_token.clone();
        if token.is_empty() {
            return Err(LauncherError::Auth("Not logged in".into()));
        }
        (code, token)
    };

    let client = lobby_backend::LobbyBackendClient::new(token);
    client.disband(&code).await?;

    let mut lobby = state.lobby_state.write().await;
    lobby.stop_heartbeat().await;
    lobby.code = None;
    lobby.posted = false;
    lobby.players.clear();
    Ok(())
}

#[tauri::command]
async fn kick_player(
    state: State<'_, AppState>,
    player_name: String,
) -> Result<(), LauncherError> {
    let (code, token) = {
        let config = state.config.read().await;
        let lobby = state.lobby_state.read().await;
        let code = lobby
            .code
            .clone()
            .ok_or_else(|| LauncherError::Lobby("No lobby".into()))?;
        let token = config.discord_access_token.clone();
        if token.is_empty() {
            return Err(LauncherError::Auth("Not logged in".into()));
        }
        (code, token)
    };

    let client = lobby_backend::LobbyBackendClient::new(token);
    client.kick(&code, &player_name).await?;
    Ok(())
}

#[tauri::command]
async fn login_discord(state: State<'_, AppState>) -> Result<auth::UserInfo, LauncherError> {
    auth::DiscordAuth::new()?;
    let url = auth::DiscordAuth::authorize_url();

    open::that(&url).map_err(|e| LauncherError::Auth(e.to_string()))?;

    // Wait for deep link callback to set the code
    let code = {
        let start = std::time::Instant::now();
        let timeout = std::time::Duration::from_secs(120);
        loop {
            if start.elapsed() > timeout {
                return Err(LauncherError::Auth("Timeout waiting for Discord callback".into()));
            }
            {
                let mut oauth = state.oauth_code.lock().map_err(|e| LauncherError::Auth(e.to_string()))?;
                if let Some(code) = oauth.take() {
                    break code;
                }
            }
            tokio::time::sleep(std::time::Duration::from_millis(200)).await;
        }
    };

    let token_resp = auth::DiscordAuth::exchange_token(&code).await?;
    let user = auth::DiscordAuth::fetch_user(&token_resp.access_token).await?;

    {
        let mut config = state.config.write().await;
        config.discord_access_token = token_resp.access_token;
        config.username = user.username.clone();
        if let Some(ref avatar) = user.avatar {
            config.avatar_url = format!(
                "https://cdn.discordapp.com/avatars/{}/{}.png",
                user.id, avatar
            );
        }
        config.save().map_err(|e| LauncherError::Config(e))?;
    }

    Ok(user)
}

#[tauri::command]
async fn handle_deep_link(state: State<'_, AppState>, url: String) -> Result<(), LauncherError> {
    if let Some(code) = auth::DiscordAuth::extract_code_from_url(&url) {
        let mut oauth = state.oauth_code.lock().map_err(|e| LauncherError::Auth(e.to_string()))?;
        *oauth = Some(code);
    }
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
    let lobby_state = lobby::LobbyState::new();

    let state = AppState {
        config: shared_config,
        lobby_state: lobby_state.clone(),
        pipe_handle: Arc::new(tokio::sync::Mutex::new(Some(pipe_handle.clone()))),
        oauth_code: std::sync::Mutex::new(None),
    };

    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, argv, _cwd| {
            // Focus the existing main window; do NOT spawn a second window.
            if let Some(window) = app.get_webview_window("main") {
                let _ = window.unminimize();
                let _ = window.show();
                let _ = window.set_focus();
            }
            // If the second instance was launched via deep link, forward the
            // OAuth code to the already-running login_discord polling loop.
            for arg in &argv {
                if let Some(code) = auth::DiscordAuth::extract_code_from_url(arg) {
                    if let Some(state) = app.try_state::<AppState>() {
                        if let Ok(mut oauth) = state.oauth_code.lock() {
                            *oauth = Some(code);
                        }
                    }
                    break;
                }
            }
        }))
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
            get_filesystem_mods,
            import_mod,
            read_config,
            write_config,
            get_install_status,
            send_ipc_message,
            get_storefront,
            set_storefront,
            browse_files,
            get_mod_list,
            post_lobby,
            disband_lobby,
            kick_player,
            login_discord,
            handle_deep_link,
            remove_mod,
            save_profile,
            apply_profile,
            delete_profile,
            list_profiles,
            list_library,
            add_to_library,
            remove_from_library,
            install_from_library,
            get_preset_mods,
            install_preset_mod,
            version_checker::check_for_among_api_update,
        ])
        .setup(move |app| {
            let app_handle = app.handle().clone();

            // Register amonglauncher:// protocol (Windows)
            #[cfg(target_os = "windows")]
            {
                use std::process::Command;
                let exe_path = std::env::current_exe().unwrap_or_default();
                let _ = Command::new("reg")
                    .args([
                        "add",
                        "HKCU\\Software\\Classes\\amonglauncher",
                        "/ve",
                        "/d",
                        "URL:Among Launcher Protocol",
                        "/f",
                    ])
                    .output();
                let _ = Command::new("reg")
                    .args([
                        "add",
                        "HKCU\\Software\\Classes\\amonglauncher\\shell\\open\\command",
                        "/ve",
                        "/d",
                        &format!("\"{}\" \"%1\"", exe_path.display()),
                        "/f",
                    ])
                    .output();
            }

            // Check command line args for deep link URL
            {
                let args: Vec<String> = std::env::args().collect();
                for arg in &args {
                    if let Some(code) = auth::DiscordAuth::extract_code_from_url(arg) {
                        let state = app.state::<AppState>();
                        let mut oauth = state.oauth_code.lock().unwrap();
                        *oauth = Some(code);
                        break;
                    }
                }
            }

            // Restore window state
            {
                let config_state = app.state::<AppState>();
                let cfg = config_state.config.blocking_read();
                if let Some(window) = app.get_webview_window("main") {
                    if let (Some(x), Some(y)) = (cfg.window_x, cfg.window_y) {
                        let _ = window.set_position(tauri::Position::Physical(
                            tauri::PhysicalPosition {
                                x: x as i32,
                                y: y as i32,
                            },
                        ));
                    }
                    if let (Some(w), Some(h)) = (cfg.window_width, cfg.window_height) {
                        let _ = window.set_size(tauri::Size::Physical(tauri::PhysicalSize {
                            width: w as u32,
                            height: h as u32,
                        }));
                    }
                    if cfg.window_maximized {
                        let _ = window.maximize();
                    }
                }
            }

            // Save window state on close
            {
                let config_state = app.state::<AppState>().clone();
                if let Some(window) = app.get_webview_window("main") {
                    let window_clone = window.clone();
                    let config_clone = config_state.config.clone();
                    window.on_window_event(move |event| {
                        if let tauri::WindowEvent::CloseRequested { .. } = event {
                            let pos = window_clone.inner_position().ok();
                            let size = window_clone.inner_size().ok();
                            let maximized = window_clone.is_maximized().unwrap_or(false);
                            let mut cfg = config_clone.blocking_write();
                            if let Some(pos) = pos {
                                cfg.window_x = Some(pos.x as f64);
                                cfg.window_y = Some(pos.y as f64);
                            }
                            if let Some(size) = size {
                                cfg.window_width = Some(size.width as f64);
                                cfg.window_height = Some(size.height as f64);
                            }
                            cfg.window_maximized = maximized;
                            let _ = cfg.save();
                        }
                    });
                }
            }

            tauri::async_runtime::spawn(async move {
                ipc::start_pipe_server(app_handle, pipe_handle).await;
            });

            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

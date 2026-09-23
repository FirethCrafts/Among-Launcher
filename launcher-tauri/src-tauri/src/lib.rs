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
mod github;
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

        /// Drop the sender so sends fail honestly when no client is
        /// connected (and the old channel + buffer are discarded).
        pub async fn clear_sender(&self) {
            *self.write_tx.lock().await = None;
        }
    }

    pub async fn start_pipe_server(app: AppHandle, handle: PipeServerHandle) {
        tokio::spawn(async move {
            let mut first_instance = true;
            loop {
                let mut options = ServerOptions::new();
                options.first_pipe_instance(first_instance);
                first_instance = false;

                match options.create(PIPE_NAME) {
                    Ok(server) => {
                        eprintln!("[PipeServer] Listening on '{}'...", PIPE_NAME);
                        match server.connect().await {
                            Ok(()) => {
                                eprintln!("[PipeServer] Client connected!");
                                // Only install a sender AFTER the client has
                                // connected; otherwise send_ipc_message would
                                // report Ok while buffering messages that flush
                                // into the NEXT game session.
                                let (tx, mut rx) = mpsc::channel::<String>(64);
                                handle.set_sender(tx).await;
                                handle.set_connected(true);
                                let _ = app.emit("ipc:client-connected", ());
                                run_connection(server, &app, &mut rx).await;
                                handle.set_connected(false);
                                // Old channel + any buffered messages are dropped here.
                                handle.clear_sender().await;
                                // The game is gone: stop its heartbeat and wipe
                                // its lobby state BEFORE announcing the
                                // disconnect — otherwise the 30s heartbeat keeps
                                // the backend lobby alive (zombie listing) and a
                                // later snapshot read resurrects a ghost lobby.
                                {
                                    let state = app.state::<AppState>();
                                    let mut lobby = state.lobby_state.write().await;
                                    lobby.clear_lobby().await;
                                }
                                let _ = app.emit("ipc:client-disconnected", ());
                                // The game's mod closes the pipe when the game
                                // exits naturally (not via stop_game), so treat
                                // disconnect as game-stopped too.
                                let _ = app.emit("game-stopped", ());
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
                                ipc_handler::handle_ipc_message(app, msg).await;
                            } else {
                                eprintln!(
                                    "[PipeServer] Dropping malformed JSON: {}",
                                    json.chars().take(500).collect::<String>()
                                );
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
    /// One-shot storage for a join deep-link whose `deep-link` event may
    /// have been missed (cold start slower than the 1500ms delayed emit —
    /// Tauri does not queue events). Taken + cleared by
    /// `get_pending_deep_link`. Private field: `DeepLink` is not `pub`.
    pending_deep_link: std::sync::Mutex<Option<DeepLink>>,
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
    tokio::task::spawn_blocking(move || {
        let plugins_dir = Path::new(&game_path).join("BepInEx").join("Plugins");

        if !plugins_dir.exists() {
            fs::create_dir_all(&plugins_dir).map_err(|e| {
                LauncherError::InstallFailed(format!("Failed to create Plugins dir: {}", e))
            })?;
        }

        for mod_path_str in &mod_paths {
            let src = Path::new(mod_path_str);
            let file_name = src.file_name().ok_or_else(|| {
                LauncherError::InstallFailed(format!("Invalid path: {}", mod_path_str))
            })?;
            let dest = plugins_dir.join(file_name);
            fs::copy(src, &dest).map_err(|e| {
                LauncherError::InstallFailed(format!(
                    "Failed to copy {} to Plugins: {}",
                    file_name.to_string_lossy(),
                    e
                ))
            })?;
        }

        Ok::<(), LauncherError>(())
    })
    .await
    .map_err(|e| LauncherError::InstallFailed(format!("Task join error: {}", e)))?
}

#[tauri::command]
async fn stop_game(app: AppHandle) -> Result<(), LauncherError> {
    #[cfg(target_os = "windows")]
    {
        tokio::task::spawn_blocking(|| -> Result<(), LauncherError> {
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
            Ok(())
        })
        .await
        .map_err(|e| LauncherError::InstallFailed(format!("Task join error: {}", e)))??;
    }

    // The game is being stopped — its lobby state is meaningless. Stop the
    // heartbeat and clear it (same shared helper as the LobbyClosed and
    // pipe-disconnect paths) before announcing the stop.
    {
        let state = app.state::<AppState>();
        let mut lobby = state.lobby_state.write().await;
        lobby.clear_lobby().await;
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
    tokio::task::spawn_blocking(move || {
        open::that(&target).map_err(|e| LauncherError::Filesystem(e.to_string()))
    })
    .await
    .map_err(|e| LauncherError::InstallFailed(format!("Task join error: {}", e)))??;
    Ok(())
}

#[derive(serde::Serialize)]
struct ModEntry {
    name: String,
    filename: String,
    size: u64,
    path: String,
    version: Option<String>,
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
                    version: crate::version_checker::extract_file_version(&path),
                });
            }
        }
        Ok(mods)
    })
    .await
    .map_err(|e| LauncherError::InstallFailed(format!("Task join error: {}", e)))?
}

/// Reject path traversal: the filename must be a single path component
/// (no separators, no `..`, no drive prefixes, not empty).
fn sanitize_filename(filename: &str) -> Result<(), LauncherError> {
    let valid = Path::new(filename)
        .file_name()
        .map(|name| name == std::ffi::OsStr::new(filename))
        .unwrap_or(false);
    if valid {
        Ok(())
    } else {
        Err(LauncherError::Filesystem(format!(
            "Invalid filename: {}",
            filename
        )))
    }
}

#[tauri::command]
async fn remove_mod(game_path: String, filename: String) -> Result<(), LauncherError> {
    sanitize_filename(&filename)?;
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
    sanitize_filename(&filename)?;
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
    sanitize_filename(&filename)?;
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
    // The AmongApi preset entry historically pointed at /releases/latest, which is
    // now a launcher release without mod assets. Resolve it lazily from the
    // newest `mod/` release instead.
    let filename_hint = repo_url.split('/').last().unwrap_or("mod.dll");
    let resolved_url = if filename_hint.eq_ignore_ascii_case("AmongApi.dll") {
        crate::github::latest_mod_asset_url("AmongApi.dll").await?
    } else {
        repo_url.clone()
    };
    let resp = client
        .get(&resolved_url)
        .send()
        .await
        .map_err(|e| LauncherError::Network(e.to_string()))?
        .error_for_status()
        .map_err(|e| LauncherError::Network(e.to_string()))?;

    let mut bytes = Vec::new();
    let mut stream = resp.bytes_stream();

    while let Some(chunk) = stream.next().await {
        let chunk = chunk.map_err(|e| LauncherError::Network(e.to_string()))?;
        bytes.extend_from_slice(&chunk);
    }

    let filename = resolved_url
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
                    download_url: Some(resolved_url),
                });
            }
        }
    }
    config_debouncer.request_save().await;
    Ok(())
}

#[tauri::command]
async fn update_among_api(
    app: AppHandle,
    state: State<'_, AppState>,
    download_url: String,
) -> Result<String, LauncherError> {
    let dest_dir = {
        let config = state.config.read().await;
        config.effective_modded_path()
    };
    let plugins_dir = Path::new(&dest_dir).join("BepInEx").join("Plugins");
    let dest = plugins_dir.join("AmongApi.dll");

    let _ = app.emit(
        "update-progress",
        serde_json::json!({ "stage": "downloading", "progress": 0, "total": 0 }),
    );

    let client = reqwest::Client::builder()
        .user_agent("among-launcher")
        .build()
        .map_err(|e| LauncherError::Network(e.to_string()))?;
    let resp = client
        .get(&download_url)
        .send()
        .await
        .map_err(|e| LauncherError::Network(e.to_string()))?
        .error_for_status()
        .map_err(|e| LauncherError::Network(e.to_string()))?;

    let total = resp.content_length().unwrap_or(0);
    let mut downloaded: u64 = 0;
    let mut bytes = Vec::new();
    let mut stream = resp.bytes_stream();
    while let Some(chunk) = stream.next().await {
        let chunk = chunk.map_err(|e| LauncherError::Network(e.to_string()))?;
        downloaded += chunk.len() as u64;
        bytes.extend_from_slice(&chunk);
        let _ = app.emit(
            "update-progress",
            serde_json::json!({ "stage": "downloading", "progress": downloaded, "total": total }),
        );
    }

    let _ = app.emit(
        "update-progress",
        serde_json::json!({ "stage": "installing", "progress": downloaded, "total": total }),
    );

    if let Some(parent) = dest.parent() {
        std::fs::create_dir_all(parent).map_err(|e| LauncherError::Filesystem(e.to_string()))?;
    }

    let tmp_path = dest.with_extension("tmp");
    if let Err(e) = std::fs::write(&tmp_path, &bytes) {
        return Err(LauncherError::Filesystem(format!(
            "Close the game and try again: {}",
            e
        )));
    }
    if let Err(e) = std::fs::rename(&tmp_path, &dest) {
        return Err(LauncherError::Filesystem(format!(
            "Close the game and try again: {}",
            e
        )));
    }

    let _ = app.emit(
        "update-progress",
        serde_json::json!({ "stage": "complete", "progress": downloaded, "total": total }),
    );
    Ok("Updated".to_string())
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

/// Version of the launcher itself (from the Tauri package info, i.e.
/// tauri.conf.json's `version` field).
/// Frontend contract: `invoke("get_version")` → `"1.2.10"` (string).
#[tauri::command]
fn get_version(app: AppHandle) -> String {
    app.package_info().version.to_string()
}

/// Payload returned by `check_for_launcher_update`.
/// Frontend contract: `invoke("check_for_launcher_update")` →
/// `{ version, downloadUrl, notes } | null` (camelCase; `notes` is `null`
/// when the release has no body). `null` = up to date.
#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct LauncherUpdateInfo {
    version: String,
    download_url: String,
    notes: Option<String>,
}

/// Checks the GitHub releases list for a `launcher/v*` release newer than
/// the running launcher.
///
/// Contract (CRITICAL — the user bug was failures being indistinguishable
/// from "up to date"):
/// - `Ok(Some(LauncherUpdateInfo))` — a strictly newer launcher release
///   exists (`{version, downloadUrl, notes}` to the frontend).
/// - `Ok(None)` — genuinely up to date (latest parsed ≤ current) OR the API
///   responded successfully with no launcher release at all.
/// - `Err(_)` — network/API failure, unparsable tag, missing `.exe` asset,
///   or incomparable versions. NEVER silently `None` on failure; the
///   frontend toasts the rejection as "check failed".
#[tauri::command]
async fn check_for_launcher_update(
    app: AppHandle,
) -> Result<Option<LauncherUpdateInfo>, LauncherError> {
    let current = app.package_info().version.to_string();

    let release = github::latest_launcher_release().await?;
    let Some(release) = release else {
        // API responded fine but has zero launcher/* releases (true first release).
        return Ok(None);
    };

    let tag = release["tag_name"].as_str().unwrap_or_default();
    let latest = github::parse_launcher_tag_version(tag).ok_or_else(|| {
        LauncherError::Network(format!("Unparsable launcher release tag: {}", tag))
    })?;

    // Numeric tuple comparison (reuses Rust-B1 helpers), NOT string compare.
    match version_checker::compare_versions(latest, &current) {
        // latest ≤ current → up to date (also covers downgrade/rollback tags).
        Some(std::cmp::Ordering::Less) | Some(std::cmp::Ordering::Equal) => Ok(None),
        Some(std::cmp::Ordering::Greater) => {
            let download_url = github::launcher_exe_asset_url(&release).ok_or_else(|| {
                LauncherError::Network(format!(
                    "Launcher release {} has no .exe download asset",
                    tag
                ))
            })?;
            Ok(Some(LauncherUpdateInfo {
                version: latest.to_string(),
                download_url,
                notes: github::launcher_release_notes(&release),
            }))
        }
        // Unparsable version on either side → report, never guess.
        None => Err(LauncherError::Network(format!(
            "Cannot compare launcher versions: installed {} vs release {}",
            current, latest
        ))),
    }
}

/// Snapshot of the current lobby state returned by `get_lobby_state`.
#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct LobbyStateSnapshot {
    code: Option<String>,
    posted: bool,
    players: Vec<PlayerSnapshot>,
    host_name: Option<String>,
    map: Option<String>,
    max_players: Option<u32>,
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct PlayerSnapshot {
    name: String,
    level: Option<u32>,
    ping: Option<u32>,
    color: Option<String>,
    is_host: bool,
}

#[tauri::command]
async fn get_lobby_state(state: State<'_, AppState>) -> Result<LobbyStateSnapshot, LauncherError> {
    let lobby = state.lobby_state.read().await;
    Ok(LobbyStateSnapshot {
        code: lobby.code.clone(),
        posted: lobby.posted,
        players: lobby
            .players
            .iter()
            .map(|p| PlayerSnapshot {
                name: p.name.clone(),
                level: p.level,
                ping: p.ping,
                color: p.color.clone(),
                is_host: p.is_host,
            })
            .collect(),
        host_name: lobby.host.clone(),
        map: lobby.map.clone(),
        max_players: lobby.max_players,
    })
}

/// Re-validate that the lobby we started POSTing for is still the one held
/// in state once the lock-free HTTP window closes. `clear_lobby` (LobbyClosed
/// / pipe disconnect / `stop_game`) can wipe the state while `create_lobby`
/// is in flight; committing `posted`/heartbeat then would resurrect
/// `{code: null, posted: true}` plus a zombie heartbeat on a dead code.
fn post_still_valid(state_code: Option<&str>, posted_code: &str) -> bool {
    state_code == Some(posted_code)
}

#[tauri::command]
async fn post_lobby(app: AppHandle, state: State<'_, AppState>) -> Result<(), LauncherError> {
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
        // Re-validate: the state lock was dropped for the HTTP call above,
        // so LobbyClosed / disconnect / `stop_game` may have run
        // `clear_lobby` (or a new lobby may have replaced this one) while
        // the POST was in flight. Committing then would resurrect
        // `{code: null, posted: true}` + a zombie heartbeat on a dead code.
        if !post_still_valid(lobby.code.as_deref(), &code) {
            // The backend lobby WAS created by the HTTP call in that
            // window; we deliberately do not disband it here (would need
            // another network call against state we no longer own) — the
            // message tells the user it may need re-posting.
            return Err(LauncherError::Lobby(format!(
                "Lobby closed while posting '{}' — it may need re-posting",
                code
            )));
        }
        lobby.posted = true;
        lobby.start_heartbeat(app, code, token).await;
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

/// A parsed amonglauncher:// deep link.
#[derive(Debug, Clone, PartialEq, Eq)]
enum DeepLink {
    /// OAuth redirect carrying a Discord authorization code.
    OAuthCode(String),
    /// Join-a-lobby link carrying a lobby code.
    Join { code: String },
}

/// Lobby codes are 4-8 ASCII alphanumerics.
fn is_valid_lobby_code(s: &str) -> bool {
    (4..=8).contains(&s.len()) && s.chars().all(|c| c.is_ascii_alphanumeric())
}

/// Parse amonglauncher:// deep links.
///
/// Supported:
/// - `amonglauncher://callback?code=...` (OAuth redirect — unchanged behavior)
/// - `amonglauncher://join/<CODE>`
/// - `amonglauncher://join?code=<CODE>`
/// - `amonglauncher://<CODE>` (bare 4-8 alphanumeric lobby code containing at
///   least one letter AND one digit, so words like `home` don't match)
fn parse_deep_link(url: &str) -> Option<DeepLink> {
    // OAuth callback takes precedence and keeps its existing extraction.
    if url.starts_with("amonglauncher://callback") {
        return auth::DiscordAuth::extract_code_from_url(url).map(DeepLink::OAuthCode);
    }

    let rest = url.strip_prefix("amonglauncher://")?;

    // amonglauncher://join/<CODE>
    if let Some(tail) = rest.strip_prefix("join/") {
        let code = tail.split(['?', '#']).next().unwrap_or("");
        return if is_valid_lobby_code(code) {
            Some(DeepLink::Join {
                code: code.to_string(),
            })
        } else {
            None
        };
    }

    // amonglauncher://join?code=<CODE>
    if let Some(tail) = rest.strip_prefix("join?") {
        let query = tail.split('#').next().unwrap_or("");
        for pair in query.split('&') {
            if let Some(value) = pair.strip_prefix("code=") {
                return if is_valid_lobby_code(value) {
                    Some(DeepLink::Join {
                        code: value.to_string(),
                    })
                } else {
                    None
                };
            }
        }
        return None;
    }

    // amonglauncher://join with nothing after it is not a join link.
    if rest == "join" {
        return None;
    }

    // Bare: amonglauncher://<CODE>
    // Tighter than the explicit join/ forms: require at least one letter AND
    // one digit so ordinary words like `amonglauncher://home` or `auth` are
    // not mistaken for a join request (InGameView accepts `^[A-Za-z0-9]{4,8}$`
    // and real lobby codes are alphanumeric mixes; a digit is the safe
    // discriminator). Explicit join/<CODE> / join?code= URLs above keep the
    // plain 4-8 alnum validation.
    if is_valid_lobby_code(rest)
        && rest.chars().any(|c| c.is_ascii_digit())
        && rest.chars().any(|c| c.is_ascii_alphabetic())
    {
        return Some(DeepLink::Join {
            code: rest.to_string(),
        });
    }

    None
}

#[tauri::command]
async fn handle_deep_link(
    app: AppHandle,
    state: State<'_, AppState>,
    url: String,
) -> Result<(), LauncherError> {
    match parse_deep_link(&url) {
        Some(DeepLink::OAuthCode(code)) => {
            let mut oauth = state.oauth_code.lock().map_err(|e| LauncherError::Auth(e.to_string()))?;
            *oauth = Some(code);
        }
        Some(DeepLink::Join { code }) => {
            // Store first so `get_pending_deep_link` can recover the link
            // even if the frontend misses the event; then emit for
            // listeners already mounted. (OAuth codes are never stored.)
            if let Ok(mut pending) = state.pending_deep_link.lock() {
                *pending = Some(DeepLink::Join { code: code.clone() });
            }
            let _ = app.emit("deep-link", DeepLinkPayload::join(code));
        }
        None => {}
    }
    Ok(())
}

/// Payload shared by the `deep-link` event and the
/// `get_pending_deep_link` command — `{ "kind": "join", "code": "ABCD" }`.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize)]
struct DeepLinkPayload {
    kind: String,
    code: String,
}

impl DeepLinkPayload {
    fn join(code: String) -> Self {
        Self {
            kind: "join".to_string(),
            code,
        }
    }
}

/// One-shot take of the stored join deep-link: returns the payload AND
/// clears the storage, so a stale link is never re-joined on a later read.
/// Only the `Join` variant is ever stored; anything else is put back and
/// reported as absent (defensive — OAuth codes must not be handed out here).
fn take_pending_deep_link(
    pending: &std::sync::Mutex<Option<DeepLink>>,
) -> Option<DeepLinkPayload> {
    let mut guard = pending.lock().ok()?;
    match guard.take() {
        Some(DeepLink::Join { code }) => Some(DeepLinkPayload::join(code)),
        other => {
            *guard = other;
            None
        }
    }
}

/// Recovery channel for the frontend: if the `deep-link` event was missed
/// (cold start slower than the 1500ms delayed emit — Tauri does not queue
/// events), invoke this to fetch the pending join link exactly once.
/// Returns `{ kind: "join", code: "..." }` or `null`.
#[tauri::command]
fn get_pending_deep_link(state: State<'_, AppState>) -> Option<DeepLinkPayload> {
    take_pending_deep_link(&state.pending_deep_link)
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
        pending_deep_link: std::sync::Mutex::new(None),
    };

    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, argv, _cwd| {
            // Focus the existing main window; do NOT spawn a second window.
            if let Some(window) = app.get_webview_window("main") {
                let _ = window.unminimize();
                let _ = window.show();
                let _ = window.set_focus();
            }
            // If the second instance was launched via deep link, forward it to
            // the already-running instance: OAuth code → login_discord polling
            // loop, join link → `deep-link` event for the frontend.
            for arg in &argv {
                match parse_deep_link(arg) {
                    Some(DeepLink::OAuthCode(code)) => {
                        if let Some(state) = app.try_state::<AppState>() {
                            if let Ok(mut oauth) = state.oauth_code.lock() {
                                *oauth = Some(code);
                            }
                        }
                    }
                    Some(DeepLink::Join { code }) => {
                        // Store for `get_pending_deep_link` recovery, then
                        // emit for the already-mounted frontend.
                        if let Some(state) = app.try_state::<AppState>() {
                            if let Ok(mut pending) = state.pending_deep_link.lock() {
                                *pending = Some(DeepLink::Join { code: code.clone() });
                            }
                        }
                        let _ = app.emit("deep-link", DeepLinkPayload::join(code));
                    }
                    None => {}
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
            get_lobby_state,
            login_discord,
            handle_deep_link,
            get_pending_deep_link,
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
            update_among_api,
            get_version,
            check_for_launcher_update,
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

            // Check command line args for deep link URLs
            {
                let args: Vec<String> = std::env::args().collect();
                for arg in &args {
                    match parse_deep_link(arg) {
                        Some(DeepLink::OAuthCode(code)) => {
                            let state = app.state::<AppState>();
                            let mut oauth = state.oauth_code.lock().unwrap();
                            *oauth = Some(code);
                        }
                        Some(DeepLink::Join { code }) => {
                            // Cold start: the webview has not registered its
                            // `deep-link` listener yet, so emit shortly after
                            // launch instead of losing the event immediately.
                            // ALSO store it right now so
                            // `get_pending_deep_link` can recover it if the
                            // webview mounts later than the 1500ms delay
                            // (Tauri does not queue events).
                            let state = app.state::<AppState>();
                            if let Ok(mut pending) = state.pending_deep_link.lock() {
                                *pending = Some(DeepLink::Join { code: code.clone() });
                            }
                            let handle = app_handle.clone();
                            tauri::async_runtime::spawn(async move {
                                tokio::time::sleep(std::time::Duration::from_millis(1500)).await;
                                let _ = handle.emit("deep-link", DeepLinkPayload::join(code));
                            });
                        }
                        None => {}
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

#[cfg(test)]
mod deep_link_tests {
    use super::*;

    #[test]
    fn join_path_form() {
        assert_eq!(
            parse_deep_link("amonglauncher://join/ABCD1234"),
            Some(DeepLink::Join {
                code: "ABCD1234".into()
            })
        );
    }

    #[test]
    fn join_query_form() {
        assert_eq!(
            parse_deep_link("amonglauncher://join?code=ABCD"),
            Some(DeepLink::Join {
                code: "ABCD".into()
            })
        );
    }

    #[test]
    fn bare_code_form() {
        assert_eq!(
            parse_deep_link("amonglauncher://XYZ1"),
            Some(DeepLink::Join {
                code: "XYZ1".into()
            })
        );
    }

    #[test]
    fn oauth_callback_still_works() {
        assert_eq!(
            parse_deep_link("amonglauncher://callback?code=oauthcode123&state=x"),
            Some(DeepLink::OAuthCode("oauthcode123".into()))
        );
    }

    #[test]
    fn oauth_callback_without_code_is_rejected() {
        assert_eq!(parse_deep_link("amonglauncher://callback"), None);
    }

    #[test]
    fn bare_join_without_code_is_rejected() {
        assert_eq!(parse_deep_link("amonglauncher://join"), None);
    }

    #[test]
    fn join_with_traversal_is_rejected() {
        assert_eq!(parse_deep_link("amonglauncher://join/../EVIL"), None);
    }

    #[test]
    fn code_too_short_or_too_long_is_rejected() {
        assert_eq!(parse_deep_link("amonglauncher://ABC"), None);
        assert_eq!(parse_deep_link("amonglauncher://ABCDEFGHI"), None);
    }

    #[test]
    fn non_amonglauncher_scheme_is_ignored() {
        assert_eq!(parse_deep_link("https://join/ABCD"), None);
    }

    #[test]
    fn bare_words_without_digits_are_rejected() {
        // The bare form must not swallow ordinary 4-8 letter words.
        assert_eq!(parse_deep_link("amonglauncher://home"), None);
        assert_eq!(parse_deep_link("amonglauncher://auth"), None);
        assert_eq!(parse_deep_link("amonglauncher://joinABC"), None);
        assert_eq!(parse_deep_link("amonglauncher://settings"), None);
    }

    #[test]
    fn bare_codes_with_letters_and_digits_are_accepted() {
        for code in ["AB12CD", "Among7"] {
            assert_eq!(
                parse_deep_link(&format!("amonglauncher://{}", code)),
                Some(DeepLink::Join { code: code.into() }),
                "bare code {:?} should parse as a join",
                code
            );
        }
    }

    #[test]
    fn explicit_join_forms_still_accept_letter_only_codes() {
        // The digit requirement applies ONLY to the bare form — explicit
        // join URLs keep the plain 4-8 alnum validation.
        assert_eq!(
            parse_deep_link("amonglauncher://join/home"),
            Some(DeepLink::Join {
                code: "home".into()
            })
        );
        assert_eq!(
            parse_deep_link("amonglauncher://join?code=ABCD"),
            Some(DeepLink::Join {
                code: "ABCD".into()
            })
        );
    }

    #[test]
    fn pending_deep_link_is_one_shot() {
        let pending = std::sync::Mutex::new(Some(DeepLink::Join {
            code: "AB12CD".into(),
        }));
        assert_eq!(
            take_pending_deep_link(&pending),
            Some(DeepLinkPayload {
                kind: "join".into(),
                code: "AB12CD".into(),
            })
        );
        // Second read must be empty — a stale link is never re-joined.
        assert_eq!(take_pending_deep_link(&pending), None);
        // An empty slot reads as null.
        let empty = std::sync::Mutex::new(None);
        assert_eq!(take_pending_deep_link(&empty), None);
    }

    #[test]
    fn deep_link_payload_matches_event_shape() {
        // Frontend contract: invoke("get_pending_deep_link") and the
        // `deep-link` event payload must be byte-identical JSON.
        let value = serde_json::to_value(DeepLinkPayload::join("ABCD".into())).unwrap();
        assert_eq!(
            value,
            serde_json::json!({ "kind": "join", "code": "ABCD" })
        );
    }

    #[test]
    fn sanitize_accepts_plain_filename() {
        assert!(sanitize_filename("AmongApi.dll").is_ok());
    }

    #[test]
    fn sanitize_rejects_traversal() {
        assert!(sanitize_filename("..").is_err());
        assert!(sanitize_filename(".").is_err());
        assert!(sanitize_filename("").is_err());
        assert!(sanitize_filename("../evil.dll").is_err());
        assert!(sanitize_filename("sub/evil.dll").is_err());
    }

    #[cfg(windows)]
    #[test]
    fn sanitize_rejects_backslash_traversal() {
        assert!(sanitize_filename(r"..\evil.dll").is_err());
        assert!(sanitize_filename(r"sub\evil.dll").is_err());
    }
}

#[cfg(test)]
mod post_lobby_tests {
    use super::*;

    #[test]
    fn post_still_valid_when_state_unchanged() {
        // Nothing cleared/replaced the lobby during the HTTP window.
        assert!(post_still_valid(Some("AB12CD"), "AB12CD"));
    }

    #[test]
    fn post_still_valid_rejects_cleared_state() {
        // `clear_lobby` ran mid-HTTP (LobbyClosed / disconnect / stop_game).
        assert!(!post_still_valid(None, "AB12CD"));
    }

    #[test]
    fn post_still_valid_rejects_replaced_state() {
        // A different lobby replaced this one mid-HTTP.
        assert!(!post_still_valid(Some("ZZ99YY"), "AB12CD"));
    }
}

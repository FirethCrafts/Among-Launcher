use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::{mpsc, RwLock};

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct LauncherConfig {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub storefront: Option<String>,
    #[serde(default = "default_modded_path")]
    pub modded_install_path: String,
    #[serde(default)]
    pub avatar_url: String,
    #[serde(default)]
    pub username: String,
    #[serde(default)]
    pub discord_access_token: String,
    #[serde(default)]
    pub profiles: Vec<ModProfile>,
    #[serde(default)]
    pub library: Vec<LibraryEntry>,
    #[serde(default)]
    pub debug_mode: bool,
    #[serde(default)]
    pub auto_post_lobby: bool,
    #[serde(default)]
    pub last_seen_version: String,
    #[serde(default)]
    pub window_x: Option<f64>,
    #[serde(default)]
    pub window_y: Option<f64>,
    #[serde(default)]
    pub window_width: Option<f64>,
    #[serde(default)]
    pub window_height: Option<f64>,
    #[serde(default)]
    pub window_maximized: bool,
}

fn default_modded_path() -> String {
    dirs::data_local_dir()
        .unwrap_or_else(|| PathBuf::from("."))
        .join("AmongLauncher")
        .join("ModdedAmongUs")
        .to_string_lossy()
        .to_string()
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct ModProfile {
    pub name: String,
    #[serde(default)]
    pub mods: Vec<ModEntry>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct ModEntry {
    pub name: String,
    #[serde(default)]
    pub version: Option<String>,
    #[serde(default)]
    pub file_hash: Option<String>,
    #[serde(default)]
    pub download_url: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct LibraryEntry {
    pub path: String,
    #[serde(default)]
    pub storefront: Option<String>,
}

impl LauncherConfig {
    fn config_dir() -> PathBuf {
        dirs::data_local_dir()
            .unwrap_or_else(|| PathBuf::from("."))
            .join("AmongLauncher")
    }

    fn config_path() -> PathBuf {
        Self::config_dir().join("config.json")
    }

    fn backup_path() -> PathBuf {
        Self::config_dir().join("config.json.bak")
    }

    pub fn load() -> Self {
        for path in [Self::config_path(), Self::backup_path()] {
            if let Ok(content) = fs::read_to_string(&path) {
                if !content.trim().is_empty() {
                    if let Ok(config) = serde_json::from_str::<LauncherConfig>(&content) {
                        return config;
                    }
                }
            }
        }
        Self::default()
    }

    pub fn save(&self) -> Result<(), String> {
        let dir = Self::config_dir();
        fs::create_dir_all(&dir).map_err(|e| format!("Failed to create dir: {}", e))?;

        let json =
            serde_json::to_string_pretty(self).map_err(|e| format!("Failed to serialize: {}", e))?;

        let temp_path = Self::config_path().with_extension("json.tmp");
        fs::write(&temp_path, &json).map_err(|e| format!("Failed to write temp: {}", e))?;
        fs::rename(&temp_path, Self::config_path())
            .map_err(|e| format!("Rename failed: {}", e))?;
        fs::copy(Self::config_path(), Self::backup_path())
            .map_err(|e| format!("Copy backup failed: {}", e))?;

        Ok(())
    }
}

pub type SharedConfig = Arc<RwLock<LauncherConfig>>;

pub fn new_shared_config() -> SharedConfig {
    let config = LauncherConfig::load();
    Arc::new(RwLock::new(config))
}

pub struct ConfigDebouncer {
    tx: mpsc::Sender<()>,
}

impl ConfigDebouncer {
    pub fn new(config: SharedConfig) -> Self {
        let (tx, mut rx) = mpsc::channel::<()>(1);
        tokio::spawn(async move {
            let mut pending = false;
            loop {
                tokio::select! {
                    msg = rx.recv() => {
                        if msg.is_some() {
                            pending = true;
                        } else {
                            break;
                        }
                    }
                    _ = tokio::time::sleep(Duration::from_millis(500)), if pending => {
                        pending = false;
                        let cfg = config.read().await;
                        if let Err(e) = cfg.save() {
                            eprintln!("Failed to save config: {}", e);
                        }
                    }
                }
            }
        });
        Self { tx }
    }

    pub async fn request_save(&self) {
        let _ = self.tx.send(()).await;
    }
}

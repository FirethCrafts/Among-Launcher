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
    /// Persisted result of the last unfiltered game detection scan. `None`
    /// for pre-existing configs (serde default) so they load unchanged.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cached_game: Option<CachedGame>,
}

/// On-disk mirror of `game_detection::GameSearchResult` plus the time it was
/// captured. Lives here (not in `game_detection`) so it can be serialized
/// without pulling the detection module into the config layer. The storefront
/// is stored in its snake_case API form ("steam" / "epic" / "microsoft_store").
#[derive(Debug, Clone, Serialize, Deserialize, Default, PartialEq)]
pub struct CachedGame {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub storefront: Option<String>,
    #[serde(default)]
    pub detected_but_unavailable: bool,
    /// Unix time in seconds. 0 means "unknown/never" and is always stale.
    #[serde(default)]
    pub detected_at_unix: u64,
}

impl CachedGame {
    /// Detection cache lifetime: 24 hours.
    pub const TTL_SECS: u64 = 24 * 60 * 60;

    pub fn is_fresh(&self, now_unix: u64) -> bool {
        self.detected_at_unix > 0 && now_unix.saturating_sub(self.detected_at_unix) < Self::TTL_SECS
    }
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
    pub fn effective_modded_path(&self) -> String {
        let trimmed = self.modded_install_path.trim();
        if trimmed.is_empty() {
            default_modded_path()
        } else {
            trimmed.to_string()
        }
    }

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
        tauri::async_runtime::spawn(async move {
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

#[cfg(test)]
mod tests {
    use super::*;

    fn cached(at: u64) -> CachedGame {
        CachedGame {
            path: Some(r"C:\Games\Among Us".into()),
            storefront: Some("steam".into()),
            detected_but_unavailable: false,
            detected_at_unix: at,
        }
    }

    #[test]
    fn cached_game_fresh_within_ttl() {
        let c = cached(1_000);
        assert!(c.is_fresh(1_000));
        assert!(c.is_fresh(1_000 + CachedGame::TTL_SECS - 1));
    }

    #[test]
    fn cached_game_stale_at_or_after_ttl() {
        let c = cached(1_000);
        assert!(!c.is_fresh(1_000 + CachedGame::TTL_SECS));
        assert!(!c.is_fresh(1_000 + CachedGame::TTL_SECS + 10_000));
    }

    #[test]
    fn cached_game_zero_timestamp_is_always_stale() {
        assert!(!CachedGame::default().is_fresh(u64::MAX));
    }

    #[test]
    fn cached_game_deserializes_from_empty_object() {
        let c: CachedGame = serde_json::from_str("{}").unwrap();
        assert!(c.path.is_none());
        assert!(c.storefront.is_none());
        assert!(!c.detected_but_unavailable);
        assert_eq!(c.detected_at_unix, 0);
    }

    #[test]
    fn old_config_without_cached_game_still_loads() {
        // Pre-existing config.json has no `cached_game` key.
        let cfg: LauncherConfig = serde_json::from_str(r#"{"username":"crew"}"#).unwrap();
        assert!(cfg.cached_game.is_none());
        assert_eq!(cfg.username, "crew");
    }
}

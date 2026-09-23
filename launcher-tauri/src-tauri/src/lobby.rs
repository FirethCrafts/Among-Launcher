use std::sync::Arc;
use tauri::{AppHandle, Emitter};
use tokio::sync::RwLock;

#[derive(Debug, Clone)]
pub struct PlayerInfo {
    pub name: String,
    pub level: Option<u32>,
    pub ping: Option<u32>,
    pub color: Option<String>,
    pub is_host: bool,
}

#[derive(Debug)]
pub struct LobbyState {
    pub code: Option<String>,
    pub region: Option<String>,
    /// Region server endpoint the mod reported in `lobby_created`; posted
    /// to the backend so joiners can fetch it and connect directly.
    pub region_ip: Option<String>,
    pub region_port: Option<u32>,
    pub host: Option<String>,
    pub max_players: Option<u32>,
    pub map: Option<String>,
    pub players: Vec<PlayerInfo>,
    pub posted: bool,
    /// Whether THIS machine is the in-game host. Surfaced via
    /// `get_lobby_state` so the Host Panel can show host-only affordances.
    pub local_is_host: bool,
    pub heartbeat_handle: Option<tokio::task::JoinHandle<()>>,
}

pub type SharedLobbyState = Arc<RwLock<LobbyState>>;

impl LobbyState {
    pub fn new() -> SharedLobbyState {
        Arc::new(RwLock::new(Self {
            code: None,
            region: None,
            region_ip: None,
            region_port: None,
            host: None,
            max_players: None,
            map: None,
            players: Vec::new(),
            posted: false,
            local_is_host: false,
            heartbeat_handle: None,
        }))
    }

    pub async fn start_heartbeat(&mut self, app: AppHandle, code: String, token: String) {
        // Abort any prior loop FIRST: `heartbeat_handle` is about to be
        // overwritten, and without this a second `post_lobby` would orphan
        // loop #1 forever (stop_heartbeat/clear_lobby only ever see the
        // latest handle). Redundant with LobbyCreated's explicit
        // stop_heartbeat — harmless.
        self.stop_heartbeat().await;
        let handle = tokio::spawn(async move {
            let client = crate::lobby_backend::LobbyBackendClient::new(token);
            // Check FIRST, then sleep: an immediate initial heartbeat means the
            // frontend's `heartbeat-status` pill reflects the real backend
            // status right after a successful POST instead of sitting on
            // Offline/unknown for a full 30s interval. `start_heartbeat` is only
            // ever called after `create_lobby` succeeded, so this first check
            // can never fail for a "not yet posted" lobby.
            loop {
                let result = client.heartbeat(&code).await;
                // Report every heartbeat outcome to the frontend.
                let payload = match &result {
                    Ok(()) => serde_json::json!({ "ok": true, "error": null }),
                    Err(e) => serde_json::json!({ "ok": false, "error": e.to_string() }),
                };
                let _ = app.emit("heartbeat-status", payload);
                if let Err(e) = result {
                    eprintln!("Heartbeat failed: {}", e);
                }
                tokio::time::sleep(std::time::Duration::from_secs(30)).await;
            }
        });
        self.heartbeat_handle = Some(handle);
    }

    pub async fn stop_heartbeat(&mut self) {
        if let Some(handle) = self.heartbeat_handle.take() {
            handle.abort();
        }
    }

    /// Stop the heartbeat and wipe every field describing a lobby.
    ///
    /// Shared by the three "this game-side lobby is gone" paths —
    /// `lobby_closed` IPC, pipe disconnect, and `stop_game` — so they cannot
    /// drift apart. Without this, the 30s heartbeat task keeps the backend
    /// lobby alive forever (zombie listing) and a mount-time
    /// `get_lobby_state` snapshot resurrects a ghost lobby after the
    /// in-game one closed.
    pub async fn clear_lobby(&mut self) {
        self.stop_heartbeat().await;
        self.code = None;
        self.region = None;
        self.region_ip = None;
        self.region_port = None;
        self.host = None;
        self.max_players = None;
        self.map = None;
        self.players.clear();
        self.posted = false;
        self.local_is_host = false;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn clear_lobby_wipes_every_field_and_aborts_heartbeat() {
        let shared = LobbyState::new();
        let mut lobby = shared.write().await;
        lobby.code = Some("AB12CD".into());
        lobby.region = Some("NA".into());
        lobby.region_ip = Some("1.2.3.4".into());
        lobby.region_port = Some(22023);
        lobby.host = Some("PlayerOne".into());
        lobby.max_players = Some(15);
        lobby.map = Some("The Skeld".into());
        lobby.players.push(PlayerInfo {
            name: "PlayerOne".into(),
            level: Some(3),
            ping: Some(42),
            color: Some("#ff0000".into()),
            is_host: true,
        });
        lobby.posted = true;
        lobby.local_is_host = true;
        lobby.heartbeat_handle = Some(tokio::spawn(async {
            tokio::time::sleep(std::time::Duration::from_secs(3600)).await;
        }));

        lobby.clear_lobby().await;

        assert_eq!(lobby.code, None);
        assert_eq!(lobby.region, None);
        assert_eq!(lobby.region_ip, None);
        assert_eq!(lobby.region_port, None);
        assert_eq!(lobby.host, None);
        assert_eq!(lobby.max_players, None);
        assert_eq!(lobby.map, None);
        assert!(lobby.players.is_empty());
        assert!(!lobby.posted);
        assert!(!lobby.local_is_host);
        // Heartbeat task was aborted AND the handle released.
        assert!(lobby.heartbeat_handle.is_none());
    }

    #[tokio::test]
    async fn clear_lobby_is_safe_with_no_heartbeat() {
        let shared = LobbyState::new();
        let mut lobby = shared.write().await;
        lobby.clear_lobby().await;
        assert!(lobby.heartbeat_handle.is_none());
        assert_eq!(lobby.code, None);
    }
}

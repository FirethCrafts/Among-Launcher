use std::sync::Arc;
use tokio::sync::RwLock;

#[derive(Debug, Clone)]
pub struct PlayerInfo {
    pub name: String,
    pub level: Option<u32>,
    pub ping: Option<u32>,
    pub is_host: bool,
}

#[derive(Debug)]
pub struct LobbyState {
    pub code: Option<String>,
    pub region: Option<String>,
    pub host: Option<String>,
    pub max_players: Option<u32>,
    pub map: Option<String>,
    pub players: Vec<PlayerInfo>,
    pub posted: bool,
    pub heartbeat_handle: Option<tokio::task::JoinHandle<()>>,
}

pub type SharedLobbyState = Arc<RwLock<LobbyState>>;

impl LobbyState {
    pub fn new() -> SharedLobbyState {
        Arc::new(RwLock::new(Self {
            code: None,
            region: None,
            host: None,
            max_players: None,
            map: None,
            players: Vec::new(),
            posted: false,
            heartbeat_handle: None,
        }))
    }

    pub async fn start_heartbeat(&mut self, code: String, token: String) {
        let handle = tokio::spawn(async move {
            let client = crate::lobby_backend::LobbyBackendClient::new(token);
            loop {
                tokio::time::sleep(std::time::Duration::from_secs(30)).await;
                if let Err(e) = client.heartbeat(&code).await {
                    eprintln!("Heartbeat failed: {}", e);
                }
            }
        });
        self.heartbeat_handle = Some(handle);
    }

    pub async fn stop_heartbeat(&mut self) {
        if let Some(handle) = self.heartbeat_handle.take() {
            handle.abort();
        }
    }
}

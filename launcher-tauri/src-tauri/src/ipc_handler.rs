use serde::Deserialize;
use tauri::{AppHandle, Emitter, Manager};

use crate::ipc::IpcEnvelope;

/// A player as reported by the game over IPC (used for both
/// `player_joined` single events and `players_list` snapshots).
#[derive(Debug, Clone, Deserialize)]
pub struct PlayerEntry {
    pub name: String,
    #[serde(default)]
    pub level: Option<u32>,
    #[serde(default)]
    pub ping: Option<u32>,
    #[serde(default)]
    pub color: Option<String>,
    /// Accepts `is_host` or `isHost` from the game message.
    #[serde(default, alias = "isHost")]
    pub is_host: Option<bool>,
}

#[derive(Debug, Deserialize)]
#[serde(tag = "type", content = "payload")]
pub enum IpcMessage {
    #[serde(rename = "game_ready")]
    GameReady,
    #[serde(rename = "lobby_created")]
    LobbyCreated {
        code: String,
        region: String,
        host: String,
        max_players: u32,
        map: String,
    },
    #[serde(rename = "lobby_closed")]
    LobbyClosed,
    #[serde(rename = "player_joined")]
    PlayerJoined(PlayerEntry),
    #[serde(rename = "player_left")]
    PlayerLeft { name: String },
    #[serde(rename = "players_list")]
    PlayersList(Vec<PlayerEntry>),
    #[serde(rename = "join_lobby_result")]
    JoinResult {
        success: bool,
        error: Option<String>,
    },
    Unknown(String),
}

impl From<IpcEnvelope> for IpcMessage {
    fn from(envelope: IpcEnvelope) -> Self {
        let known_types = [
            "game_ready",
            "lobby_created",
            "lobby_closed",
            "player_joined",
            "player_left",
            "players_list",
            "join_lobby_result",
        ];
        if !known_types.contains(&envelope.msg_type.as_str()) {
            let raw = serde_json::to_string(&serde_json::json!({
                "type": envelope.msg_type,
                "payload": envelope.payload,
            }))
            .unwrap_or_default();
            return IpcMessage::Unknown(raw);
        }

        let mut map = serde_json::Map::new();
        map.insert(
            "type".to_string(),
            serde_json::Value::String(envelope.msg_type),
        );
        if let Some(payload) = envelope.payload {
            map.insert("payload".to_string(), payload);
        } else {
            map.insert("payload".to_string(), serde_json::Value::Null);
        }
        let value = serde_json::Value::Object(map);
        serde_json::from_value(value).unwrap_or_else(|e| {
            IpcMessage::Unknown(format!("Deserialization error: {}", e))
        })
    }
}

/// Resolve whether a player is the lobby host: trust the game's flag OR
/// fall back to matching the player name against the known lobby host.
/// Used for both the stored `lobby_state.players` and the emitted
/// `player-joined` payload so state and event cannot disagree (the raw
/// flag alone can be null/absent even for the host).
fn resolve_is_host(entry_is_host: Option<bool>, lobby_host: Option<&str>, name: &str) -> bool {
    entry_is_host.unwrap_or(false) || lobby_host == Some(name)
}

pub async fn handle_ipc_message(app: &AppHandle, msg: IpcMessage) {
    match msg {
        IpcMessage::GameReady => {
            let _ = app.emit("game-ready", ());
        }
        IpcMessage::LobbyCreated {
            code,
            region,
            host,
            max_players,
            map,
        } => {
            // Populate AppState.lobby_state so post_lobby/disband_lobby/
            // kick_player can find the lobby the game just created.
            {
                let state = app.state::<crate::AppState>();
                let mut lobby = state.lobby_state.write().await;
                // Any heartbeat from a previous lobby is stale now.
                lobby.stop_heartbeat().await;
                lobby.code = Some(code.clone());
                lobby.region = Some(region.clone());
                lobby.host = Some(host.clone());
                lobby.max_players = Some(max_players);
                lobby.map = Some(map.clone());
                lobby.players = vec![crate::lobby::PlayerInfo {
                    name: host.clone(),
                    level: None,
                    ping: None,
                    color: None,
                    is_host: true,
                }];
                // A brand-new lobby has not been posted yet.
                lobby.posted = false;
            }
            let _ = app.emit(
                "lobby-created",
                serde_json::json!({
                    "code": code,
                    "region": region,
                    "host": host,
                    "maxPlayers": max_players,
                    "map": map,
                }),
            );
        }
        IpcMessage::LobbyClosed => {
            // The in-game lobby is gone: stop the heartbeat and clear the
            // state BEFORE emitting so any snapshot read when the event
            // lands is already clean — otherwise the 30s heartbeat keeps the
            // backend lobby alive (zombie listing) and a mount-time
            // `get_lobby_state` resurrects a ghost "Already Posted" lobby.
            {
                let state = app.state::<crate::AppState>();
                let mut lobby = state.lobby_state.write().await;
                lobby.clear_lobby().await;
            }
            let _ = app.emit("lobby-closed", ());
        }
        IpcMessage::PlayerJoined(entry) => {
            let is_host = {
                let state = app.state::<crate::AppState>();
                let mut lobby = state.lobby_state.write().await;
                let is_host = resolve_is_host(entry.is_host, lobby.host.as_deref(), &entry.name);
                match lobby.players.iter_mut().find(|p| p.name == entry.name) {
                    Some(existing) => {
                        if entry.level.is_some() {
                            existing.level = entry.level;
                        }
                        if entry.ping.is_some() {
                            existing.ping = entry.ping;
                        }
                        if entry.color.is_some() {
                            existing.color = entry.color.clone();
                        }
                        // Store the SAME computed value the event emits —
                        // not a sticky `= true` — so the stored snapshot
                        // and the `player-joined` payload can never
                        // disagree when a prior `players_list` flagged
                        // this player as host but the fresh resolution
                        // computes false.
                        existing.is_host = is_host;
                    }
                    None => {
                        lobby.players.push(crate::lobby::PlayerInfo {
                            name: entry.name.clone(),
                            level: entry.level,
                            ping: entry.ping,
                            color: entry.color.clone(),
                            is_host,
                        });
                    }
                }
                is_host
            };
            let _ = app.emit(
                "player-joined",
                serde_json::json!({
                    "name": entry.name,
                    "level": entry.level,
                    "ping": entry.ping,
                    "color": entry.color,
                    // Same computed value stored in lobby_state (resolves
                    // against lobby.host) — raw entry.is_host can be null
                    // even for the host itself.
                    "is_host": is_host,
                }),
            );
        }
        IpcMessage::PlayerLeft { name } => {
            {
                let state = app.state::<crate::AppState>();
                let mut lobby = state.lobby_state.write().await;
                lobby.players.retain(|p| p.name != name);
            }
            let _ = app.emit("player-left", serde_json::json!({ "name": name }));
        }
        IpcMessage::PlayersList(entries) => {
            // Full snapshot: replace the cached player list.
            let state = app.state::<crate::AppState>();
            let mut lobby = state.lobby_state.write().await;
            let host = lobby.host.clone();
            lobby.players = entries
                .into_iter()
                .map(|entry| {
                    let is_host = resolve_is_host(entry.is_host, host.as_deref(), &entry.name);
                    crate::lobby::PlayerInfo {
                        name: entry.name,
                        level: entry.level,
                        ping: entry.ping,
                        color: entry.color,
                        is_host,
                    }
                })
                .collect();
        }
        IpcMessage::JoinResult { success, error } => {
            let _ = app.emit(
                "join-result",
                serde_json::json!({
                    "success": success,
                    "error": error,
                }),
            );
        }
        IpcMessage::Unknown(raw) => {
            eprintln!("Unknown IPC message: {}", raw);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parse_game_ready() {
        let json = r#"{"type": "game_ready"}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        assert!(matches!(msg, IpcMessage::GameReady));
    }

    #[test]
    fn test_parse_lobby_created() {
        let json = r#"{"type": "lobby_created", "payload": {"code": "ABCD", "region": "NA", "host": "player1", "max_players": 10, "map": "The Skeld"}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::LobbyCreated {
                code,
                region,
                host,
                max_players,
                map,
            } => {
                assert_eq!(code, "ABCD");
                assert_eq!(region, "NA");
                assert_eq!(host, "player1");
                assert_eq!(max_players, 10);
                assert_eq!(map, "The Skeld");
            }
            _ => panic!("Expected LobbyCreated"),
        }
    }

    #[test]
    fn test_parse_lobby_closed() {
        let json = r#"{"type": "lobby_closed"}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        assert!(matches!(msg, IpcMessage::LobbyClosed));
    }

    #[test]
    fn test_parse_player_joined() {
        let json = r#"{"type": "player_joined", "payload": {"name": "Alice", "level": 5, "ping": 30}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::PlayerJoined(entry) => {
                assert_eq!(entry.name, "Alice");
                assert_eq!(entry.level, Some(5));
                assert_eq!(entry.ping, Some(30));
                assert_eq!(entry.color, None);
                assert_eq!(entry.is_host, None);
            }
            _ => panic!("Expected PlayerJoined"),
        }
    }

    #[test]
    fn test_parse_player_joined_with_color_and_host() {
        let json = r##"{"type": "player_joined", "payload": {"name": "Alice", "level": 5, "ping": 30, "color": "#ff0000", "is_host": true}}"##;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::PlayerJoined(entry) => {
                assert_eq!(entry.name, "Alice");
                assert_eq!(entry.color, Some("#ff0000".to_string()));
                assert_eq!(entry.is_host, Some(true));
            }
            _ => panic!("Expected PlayerJoined"),
        }
    }

    #[test]
    fn test_parse_player_joined_is_host_camel_alias() {
        let json = r#"{"type": "player_joined", "payload": {"name": "Alice", "isHost": true}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::PlayerJoined(entry) => {
                assert_eq!(entry.is_host, Some(true));
            }
            _ => panic!("Expected PlayerJoined"),
        }
    }

    #[test]
    fn test_parse_player_joined_optional_fields() {
        let json = r#"{"type": "player_joined", "payload": {"name": "Bob"}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::PlayerJoined(entry) => {
                assert_eq!(entry.name, "Bob");
                assert_eq!(entry.level, None);
                assert_eq!(entry.ping, None);
            }
            _ => panic!("Expected PlayerJoined"),
        }
    }

    #[test]
    fn test_parse_players_list() {
        let json = r##"{"type": "players_list", "payload": [{"name": "Alice", "color": "#111111", "is_host": true}, {"name": "Bob", "level": 2, "ping": 40}]}"##;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::PlayersList(entries) => {
                assert_eq!(entries.len(), 2);
                assert_eq!(entries[0].name, "Alice");
                assert_eq!(entries[0].color, Some("#111111".to_string()));
                assert_eq!(entries[0].is_host, Some(true));
                assert_eq!(entries[1].name, "Bob");
                assert_eq!(entries[1].level, Some(2));
                assert_eq!(entries[1].ping, Some(40));
            }
            _ => panic!("Expected PlayersList"),
        }
    }

    #[test]
    fn test_parse_player_left() {
        let json = r#"{"type": "player_left", "payload": {"name": "Charlie"}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::PlayerLeft { name } => {
                assert_eq!(name, "Charlie");
            }
            _ => panic!("Expected PlayerLeft"),
        }
    }

    #[test]
    fn test_parse_join_result_success() {
        let json = r#"{"type": "join_lobby_result", "payload": {"success": true}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::JoinResult { success, error } => {
                assert!(success);
                assert_eq!(error, None);
            }
            _ => panic!("Expected JoinResult"),
        }
    }

    #[test]
    fn test_parse_join_result_failure() {
        let json = r#"{"type": "join_lobby_result", "payload": {"success": false, "error": "Lobby full"}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::JoinResult { success, error } => {
                assert!(!success);
                assert_eq!(error, Some("Lobby full".to_string()));
            }
            _ => panic!("Expected JoinResult"),
        }
    }

    #[test]
    fn test_parse_unknown_type() {
        let envelope = IpcEnvelope {
            msg_type: "some_future_event".to_string(),
            id: "test-id".to_string(),
            timestamp: 1234567890,
            payload: Some(serde_json::json!({"data": 123})),
        };
        let msg: IpcMessage = envelope.into();
        match msg {
            IpcMessage::Unknown(raw) => {
                assert!(raw.contains("some_future_event"));
            }
            _ => panic!("Expected Unknown"),
        }
    }

    #[test]
    fn test_from_envelope() {
        let envelope = IpcEnvelope {
            msg_type: "game_ready".to_string(),
            id: "test-id".to_string(),
            timestamp: 1234567890,
            payload: None,
        };
        let msg: IpcMessage = envelope.into();
        assert!(matches!(msg, IpcMessage::GameReady));
    }

    #[test]
    fn resolve_is_host_trusts_game_flag() {
        assert!(resolve_is_host(Some(true), None, "Alice"));
        assert!(resolve_is_host(Some(true), Some("Bob"), "Alice"));
        assert!(!resolve_is_host(Some(false), None, "Alice"));
        assert!(!resolve_is_host(None, None, "Alice"));
    }

    #[test]
    fn resolve_is_host_falls_back_to_lobby_host_name() {
        // The host join often arrives with no/null is_host flag — the
        // name match against lobby.host must still resolve it (this is
        // the value emitted in `player-joined`).
        assert!(resolve_is_host(None, Some("Alice"), "Alice"));
        assert!(resolve_is_host(Some(false), Some("Alice"), "Alice"));
        assert!(!resolve_is_host(None, Some("Alice"), "Bob"));
    }
}

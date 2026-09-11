use serde::Deserialize;
use tauri::{AppHandle, Emitter};

use crate::ipc::IpcEnvelope;

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
    PlayerJoined {
        name: String,
        level: Option<u32>,
        ping: Option<u32>,
    },
    #[serde(rename = "player_left")]
    PlayerLeft { name: String },
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

pub fn handle_ipc_message(app: &AppHandle, msg: IpcMessage) {
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
            let _ = app.emit("lobby-closed", ());
        }
        IpcMessage::PlayerJoined { name, level, ping } => {
            let _ = app.emit(
                "player-joined",
                serde_json::json!({
                    "name": name,
                    "level": level,
                    "ping": ping,
                }),
            );
        }
        IpcMessage::PlayerLeft { name } => {
            let _ = app.emit("player-left", serde_json::json!({ "name": name }));
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
            IpcMessage::PlayerJoined { name, level, ping } => {
                assert_eq!(name, "Alice");
                assert_eq!(level, Some(5));
                assert_eq!(ping, Some(30));
            }
            _ => panic!("Expected PlayerJoined"),
        }
    }

    #[test]
    fn test_parse_player_joined_optional_fields() {
        let json = r#"{"type": "player_joined", "payload": {"name": "Bob"}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::PlayerJoined { name, level, ping } => {
                assert_eq!(name, "Bob");
                assert_eq!(level, None);
                assert_eq!(ping, None);
            }
            _ => panic!("Expected PlayerJoined"),
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
}

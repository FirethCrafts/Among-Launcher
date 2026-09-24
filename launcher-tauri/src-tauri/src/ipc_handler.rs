use serde::Deserialize;
use tauri::{AppHandle, Emitter, Manager};

use crate::ipc::IpcEnvelope;

/// Launcher↔mod IPC protocol version this launcher expects. Must match
/// `Plugin.ProtocolVersion` in `Among API/Plugin.cs` (bumped to 3 for the
/// `-1` ping sentinel and the per-player color fields).
pub(crate) const EXPECTED_MOD_PROTOCOL: u32 = 3;

/// A player as reported by the game over IPC (used for both
/// `player_joined` single events and `players_list` snapshots).
#[derive(Debug, Clone, Deserialize)]
pub struct PlayerEntry {
    /// Accepts `name` (snake fixtures / older mod) or the real mod key
    /// `playerName` — the game serializes C# anonymous types with default
    /// `JsonSerializer` settings, so property names are verbatim.
    #[serde(alias = "playerName")]
    pub name: String,
    #[serde(default)]
    pub level: Option<u32>,
    /// The mod reports a per-player ping, but Among Us has no such value:
    /// non-local players are sent as `-1` (the mod's "unknown" sentinel).
    /// This is therefore `i32`, not `u32` — deserializing `-1` into
    /// `Option<u32>` hard-errors and, via `From<IpcEnvelope>`, silently
    /// drops the whole message. Every use site normalizes negatives to
    /// `None` with [`normalize_ping`] so the rest of the system keeps
    /// `Option<u32>` semantics.
    #[serde(default)]
    pub ping: Option<i32>,
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
    GameReady {
        /// Protocol version reported by the mod (`{ "protocol": 3 }`).
        /// Absent on mod builds that predate the field — treated as
        /// incompatible (an old mod predates the whole IPC contract).
        #[serde(default, alias = "protocol")]
        protocol: Option<u32>,
    },
    #[serde(rename = "lobby_created")]
    LobbyCreated {
        code: String,
        region: String,
        // Real mod keys are camelCase (`regionIp`/`regionPort`); keep the
        // snake_case names so fixtures / older mod builds still parse.
        #[serde(default, alias = "regionIp")]
        region_ip: Option<String>,
        #[serde(default, alias = "regionPort")]
        region_port: Option<u32>,
        host: String,
        // Real mod key is camelCase (`maxPlayers`); keep the snake_case
        // name so existing fixtures and older mod builds still parse.
        #[serde(alias = "maxPlayers")]
        max_players: u32,
        // Real mod key is `map_name`; `map` is kept for fixtures.
        #[serde(alias = "map_name")]
        map: String,
        // Mod builds before the isHost addition omit this entirely.
        #[serde(default, alias = "isHost")]
        is_host: bool,
        // Full roster the mod snapshots at lobby creation. The real mod keys
        // are camelCase (`playerNames`/`playerLevels`/`playerPings`) and the
        // mod sends `?? new List<...>()`, so they are never null — only
        // absent (older builds), which `default` covers. `Plugin.cs` uses
        // `List<int>` for levels/pings, hence `i32` here.
        #[serde(default, alias = "playerNames")]
        player_names: Vec<String>,
        #[serde(default, alias = "playerLevels")]
        player_levels: Vec<i32>,
        #[serde(default, alias = "playerPings")]
        player_pings: Vec<i32>,
        // Index-aligned with `playerNames`; lowercase color names, `""` for
        // unknown. Same camelCase alias style as the other roster arrays.
        #[serde(default, alias = "playerColors")]
        player_colors: Vec<String>,
    },
    #[serde(rename = "lobby_closed")]
    LobbyClosed {
        #[serde(default, alias = "isHost")]
        is_host: bool,
    },
    #[serde(rename = "player_joined")]
    PlayerJoined(PlayerEntry),
    #[serde(rename = "player_left")]
    PlayerLeft {
        // Real mod key is `playerName`.
        #[serde(alias = "playerName")]
        name: String,
    },
    #[serde(rename = "players_list")]
    PlayersList(Vec<PlayerEntry>),
    #[serde(rename = "join_lobby_result")]
    JoinResult {
        success: bool,
        error: Option<String>,
        /// Lobby code the result refers to. The mod (sibling change)
        /// added this so the frontend can correlate a late result with the
        /// code it actually tried to join. `#[serde(default)]` keeps older
        /// mod builds (which omit it) parsing.
        #[serde(default)]
        code: Option<String>,
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
            // Struct variants (`game_ready`, `lobby_closed`, …) require a
            // content object; a missing payload is an empty object, not JSON
            // null (which would fail to deserialize as a struct). This keeps
            // legacy `game_ready` messages with no payload parsing as
            // `GameReady { protocol: None }`.
            map.insert(
                "payload".to_string(),
                serde_json::Value::Object(serde_json::Map::new()),
            );
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

/// Whether a lobby host name is real enough to flag as the host. Mirrors the
/// mod's `ResolveIsHost` guard: an empty or literal "UNKNOWN" host — the mod's
/// sentinel when the name could not be resolved — must never be marked host.
fn host_is_known(host: &str) -> bool {
    !host.is_empty() && !host.eq_ignore_ascii_case("UNKNOWN")
}

/// Normalize a raw mod ping into the launcher's `Option<u32>` semantics:
/// negative values (the mod's `-1` "unknown" sentinel for non-local players)
/// map to `None`; real pings pass through. Used at every `PlayerEntry` use
/// site so `-1` never reaches `lobby_state` or an emitted payload.
fn normalize_ping(p: Option<i32>) -> Option<u32> {
    p.filter(|v| *v >= 0).map(|v| v as u32)
}

/// Merge a `player_joined` entry into the cached roster, or insert it when
/// the player is new. Non-destructive: a field the entry omits (`None`)
/// never wipes a known value — in particular a normalized `ping` of `None`
/// (the mod's `-1` "unknown" sentinel) must not clobber a previously known
/// ping, mirroring the `color` guard. `ping` is passed in pre-normalized so
/// the stored value always has `Option<u32>` semantics.
fn upsert_player(
    players: &mut Vec<crate::lobby::PlayerInfo>,
    entry: &PlayerEntry,
    ping: Option<u32>,
    is_host: bool,
) {
    match players.iter_mut().find(|p| p.name == entry.name) {
        Some(existing) => {
            if entry.level.is_some() {
                existing.level = entry.level;
            }
            if ping.is_some() {
                existing.ping = ping;
            }
            if entry.color.is_some() {
                existing.color = entry.color.clone();
            }
            // Store the SAME computed value the event emits — not a sticky
            // `= true` — so the stored snapshot and the `player-joined`
            // payload can never disagree when a prior `players_list` flagged
            // this player as host but the fresh resolution computes false.
            existing.is_host = is_host;
        }
        None => {
            players.push(crate::lobby::PlayerInfo {
                name: entry.name.clone(),
                level: entry.level,
                ping,
                color: entry.color.clone(),
                is_host,
            });
        }
    }
}

/// Convert a `players_list` snapshot entry into stored `PlayerInfo`,
/// normalizing the raw ping (`-1` → `None`) and resolving the host flag
/// against the known lobby host. Shared so the full-replace snapshot path
/// and the `player_joined` path cannot drift on either concern.
fn player_info_from_entry(
    entry: PlayerEntry,
    lobby_host: Option<&str>,
) -> crate::lobby::PlayerInfo {
    let is_host = resolve_is_host(entry.is_host, lobby_host, &entry.name);
    crate::lobby::PlayerInfo {
        name: entry.name,
        level: entry.level,
        ping: normalize_ping(entry.ping),
        color: entry.color,
        is_host,
    }
}

/// Build the player list the mod snapshotted at lobby creation by zipping
/// `playerNames` with `playerLevels`/`playerPings`/`playerColors` **by index**.
/// Every side-array may be shorter than (or absent from) the names list, so
/// every access is bounds-checked and a missing value maps to `None` — never a
/// panic. Negative levels/pings (the mod's "unknown" sentinel) also map to
/// `None` rather than wrapping into a bogus `u32`; an empty color string maps
/// to `None`. Each player's host flag is resolved against the lobby host name
/// exactly like the `player_joined` / `players_list` paths so stored state and
/// emitted events cannot disagree.
fn seed_players_from_roster(
    names: &[String],
    levels: &[i32],
    pings: &[i32],
    colors: &[String],
    lobby_host: Option<&str>,
) -> Vec<crate::lobby::PlayerInfo> {
    names
        .iter()
        .enumerate()
        .map(|(i, name)| crate::lobby::PlayerInfo {
            name: name.clone(),
            level: levels.get(i).copied().and_then(|v| u32::try_from(v).ok()),
            ping: normalize_ping(pings.get(i).copied()),
            color: colors.get(i).cloned().filter(|c| !c.is_empty()),
            is_host: resolve_is_host(None, lobby_host, name),
        })
        .collect()
}

/// Serialize a player list into the exact array-of-objects shape every
/// `players_list` event carries: `{name, level, ping, color, is_host}`.
/// Shared by the `LobbyCreated` roster emit and the `PlayersList` snapshot
/// emit so the two can never drift.
fn players_list_payload(players: &[crate::lobby::PlayerInfo]) -> serde_json::Value {
    serde_json::Value::Array(
        players
            .iter()
            .map(|p| {
                serde_json::json!({
                    "name": p.name,
                    "level": p.level,
                    "ping": p.ping,
                    "color": p.color,
                    // snake_case, matching the `player-joined` payload.
                    "is_host": p.is_host,
                })
            })
            .collect(),
    )
}

/// The exact payload every `lobby-closed` event carries: `{ "isHost": bool }`.
/// Shared by the `LobbyClosed` IPC arm, `disband_lobby`, and the pipe
/// disconnect path so the frontend listener always sees one shape.
pub(crate) fn lobby_closed_payload(is_host: bool) -> serde_json::Value {
    serde_json::json!({ "isHost": is_host })
}

/// Why the in-game mod's protocol version is unacceptable, or `None` when it
/// matches what this launcher expects.
///
/// A `None` protocol (the field is absent) is treated as incompatible on
/// purpose: a mod build that predates the protocol field predates the whole
/// IPC contract, so it cannot be trusted to speak the current message shapes.
fn protocol_mismatch_reason(protocol: Option<u32>) -> Option<String> {
    match protocol {
        Some(p) if p == EXPECTED_MOD_PROTOCOL => None,
        Some(p) => Some(format!(
            "AmongApi protocol v{} is not supported (expected v{})",
            p, EXPECTED_MOD_PROTOCOL
        )),
        None => Some(
            "AmongApi did not report a protocol version; update it in the launcher".to_string(),
        ),
    }
}

/// The exact payload every `mod-incompatible` event carries:
/// `{ "reason": "<string>" }`.
pub(crate) fn mod_incompatible_payload(reason: &str) -> serde_json::Value {
    serde_json::json!({ "reason": reason })
}

/// Evaluate a `game_ready` protocol report: cache + announce an
/// `incompatible` verdict on mismatch, or clear a previous verdict on match.
async fn handle_game_ready_protocol(app: &AppHandle, protocol: Option<u32>) {
    match protocol_mismatch_reason(protocol) {
        Some(reason) => {
            // The game is running a mod we cannot talk to. Cache the verdict
            // (so `launch_game` rejects until it is updated) and tell the UI.
            let installed = {
                let state = app.state::<crate::AppState>();
                let game_path = state.config.read().await.effective_modded_path();
                crate::version_checker::read_installed_version(&game_path)
            };
            let state = app.state::<crate::AppState>();
            crate::version_checker::mark_mod_incompatible(&state, installed, &reason);
            let _ = app.emit("mod-incompatible", mod_incompatible_payload(&reason));
        }
        None => {
            let state = app.state::<crate::AppState>();
            crate::version_checker::clear_mod_incompatible(&state);
        }
    }
}

pub async fn handle_ipc_message(app: &AppHandle, msg: IpcMessage) {
    match msg {
        IpcMessage::GameReady { protocol } => {
            let _ = app.emit("game-ready", ());
            handle_game_ready_protocol(app, protocol).await;
        }
        IpcMessage::LobbyCreated {
            code,
            region,
            region_ip,
            region_port,
            host,
            max_players,
            map,
            is_host,
            player_names,
            player_levels,
            player_pings,
            player_colors,
        } => {
            // Populate AppState.lobby_state so post_lobby/disband_lobby/
            // kick_player can find the lobby the game just created. The mod
            // snapshots the full roster in `playerNames`/`playerLevels`/
            // `playerPings`; seed from it so entering a populated lobby does
            // not show an empty player list until the first join/leave.
            let roster: Vec<crate::lobby::PlayerInfo> = {
                let state = app.state::<crate::AppState>();
                let mut lobby = state.lobby_state.write().await;
                // Any heartbeat from a previous lobby is stale now.
                lobby.stop_heartbeat().await;
                lobby.code = Some(code.clone());
                lobby.region = Some(region.clone());
                lobby.region_ip = region_ip.clone();
                lobby.region_port = region_port;
                lobby.host = Some(host.clone());
                lobby.max_players = Some(max_players);
                lobby.map = Some(map.clone());
                // Whether THIS machine is the in-game host (drives Host
                // Panel "you are host" affordances via get_lobby_state).
                lobby.local_is_host = is_host;
                let seeded = seed_players_from_roster(
                    &player_names,
                    &player_levels,
                    &player_pings,
                    &player_colors,
                    lobby.host.as_deref(),
                );
                lobby.players = if seeded.is_empty() {
                    // No roster reported (older mod / not yet populated):
                    // keep the historical host-only seed so the snapshot
                    // still has something.
                    vec![crate::lobby::PlayerInfo {
                        name: host.clone(),
                        level: None,
                        ping: None,
                        color: None,
                        is_host: host_is_known(&host),
                    }]
                } else {
                    seeded.clone()
                };
                // A brand-new lobby has not been posted yet.
                lobby.posted = false;
                // The roster the mod actually reported (empty when absent) —
                // the emit below is gated on this, not the host-only
                // fallback stored in `lobby.players`.
                seeded
            };
            let _ = app.emit(
                "lobby-created",
                serde_json::json!({
                    "code": code,
                    "region": region,
                    "host": host,
                    "maxPlayers": max_players,
                    "map": map,
                    "isHost": is_host,
                }),
            );
            // Emit the seeded roster AFTER `lobby-created`: the frontend's
            // `lobby-created` handler resets `players` to `[]`, so this must
            // land second to repopulate it. `roster` is the mod-reported
            // vector (NOT the host-only fallback stored in `lobby.players`
            // above): this emit runs whenever that vector is non-empty and is
            // skipped only when the mod reported no roster at all.
            if !roster.is_empty() {
                let _ = app.emit("players_list", players_list_payload(&roster));
            }
        }
        IpcMessage::LobbyClosed { is_host } => {
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
            let _ = app.emit("lobby-closed", lobby_closed_payload(is_host));
        }
        IpcMessage::PlayerJoined(entry) => {
            // Normalize once: a `-1` sentinel (non-local player) becomes
            // `None` and must never reach stored state or the emitted payload.
            let ping = normalize_ping(entry.ping);
            let is_host = {
                let state = app.state::<crate::AppState>();
                let mut lobby = state.lobby_state.write().await;
                let is_host = resolve_is_host(entry.is_host, lobby.host.as_deref(), &entry.name);
                upsert_player(&mut lobby.players, &entry, ping, is_host);
                is_host
            };
            let _ = app.emit(
                "player-joined",
                serde_json::json!({
                    "name": entry.name,
                    "level": entry.level,
                    "ping": ping,
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
            // Full snapshot: replace the cached player list. Raw `-1` pings
            // (non-local players) are normalized to `None` on the way in so
            // the sentinel never reaches stored state or the emitted event.
            let state = app.state::<crate::AppState>();
            let mut lobby = state.lobby_state.write().await;
            let host = lobby.host.clone();
            lobby.players = entries
                .into_iter()
                .map(|entry| player_info_from_entry(entry, host.as_deref()))
                .collect();
            // Forward the snapshot so the frontend sees the full roster with
            // real names (the `player_joined`/`player_left` events carry the
            // literal "<unknown>" for names). Same shape as the roster emit
            // after `lobby-created`.
            let _ = app.emit("players_list", players_list_payload(&lobby.players));
        }
        IpcMessage::JoinResult { success, error, code } => {
            let _ = app.emit(
                "join-result",
                serde_json::json!({
                    "success": success,
                    "error": error,
                    "code": code,
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
        // Legacy: empty payload → protocol defaults to None.
        let json = r#"{"type": "game_ready", "payload": {}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        assert!(matches!(msg, IpcMessage::GameReady { protocol: None }));
    }

    #[test]
    fn legacy_game_ready_without_payload_parses_via_envelope() {
        // The pipe server parses an envelope and converts it; a legacy
        // `game_ready` with no payload must still yield `protocol: None`.
        let envelope = IpcEnvelope {
            msg_type: "game_ready".to_string(),
            id: "test-id".to_string(),
            timestamp: 1234567890,
            payload: None,
        };
        let msg: IpcMessage = envelope.into();
        assert!(matches!(msg, IpcMessage::GameReady { protocol: None }));
    }

    #[test]
    fn test_parse_game_ready_with_protocol() {
        // Exact shape the mod sends (`Among API/Plugin.cs`).
        let json = r#"{"type": "game_ready", "payload": {"protocol": 3}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        assert!(matches!(msg, IpcMessage::GameReady { protocol: Some(3) }));
    }

    #[test]
    fn test_parse_game_ready_with_unexpected_protocol() {
        let json = r#"{"type": "game_ready", "payload": {"protocol": 1}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        assert!(matches!(msg, IpcMessage::GameReady { protocol: Some(1) }));
    }

    #[test]
    fn test_parse_lobby_created() {
        let json = r#"{"type": "lobby_created", "payload": {"code": "ABCD", "region": "NA", "host": "player1", "max_players": 10, "map": "The Skeld"}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::LobbyCreated {
                code,
                region,
                region_ip,
                region_port,
                host,
                max_players,
                map,
                is_host,
                ..
            } => {
                assert_eq!(code, "ABCD");
                assert_eq!(region, "NA");
                assert_eq!(region_ip, None);
                assert_eq!(region_port, None);
                assert_eq!(host, "player1");
                assert_eq!(max_players, 10);
                assert_eq!(map, "The Skeld");
                // Absent from this legacy fixture → defaults to false.
                assert!(!is_host);
            }
            _ => panic!("Expected LobbyCreated"),
        }
    }

    #[test]
    fn test_parse_lobby_closed() {
        // Legacy fixture adjusted: LobbyClosed is now a struct variant, so
        // the content field must be present (the mod always sends an
        // object; `From<IpcEnvelope>` always injects a payload key).
        let json = r#"{"type": "lobby_closed", "payload": {}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        assert!(matches!(msg, IpcMessage::LobbyClosed { is_host: false }));
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

    // ---- negative ping sentinel (`-1` = unknown, non-local players) ----

    #[test]
    fn player_joined_negative_ping_parses_and_normalizes_to_none() {
        // The mod sends `ping: -1` for non-local players. Deserializing `-1`
        // into `Option<u32>` hard-errors and `From<IpcEnvelope>` swallows the
        // error into `Unknown`, dropping the whole message. It must parse, and
        // the sentinel must normalize to `None`.
        let json = r#"{"type": "player_joined", "payload": {"name": "x", "ping": -1}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::PlayerJoined(entry) => {
                assert_eq!(entry.name, "x");
                assert_eq!(entry.ping, Some(-1)); // sentinel accepted, not an error
                assert_eq!(normalize_ping(entry.ping), None); // normalized
            }
            other => panic!("Expected PlayerJoined, got {:?}", other),
        }
    }

    #[test]
    fn player_joined_negative_ping_via_envelope_is_not_unknown() {
        // The real failure mode was the envelope → IpcMessage conversion
        // degrading to `Unknown`; exercise that exact path.
        let envelope = IpcEnvelope {
            msg_type: "player_joined".to_string(),
            id: "test-id".to_string(),
            timestamp: 1234567890,
            payload: Some(serde_json::json!({"name": "x", "ping": -1})),
        };
        let msg: IpcMessage = envelope.into();
        match msg {
            IpcMessage::PlayerJoined(entry) => {
                assert_eq!(normalize_ping(entry.ping), None);
            }
            other => panic!("Expected PlayerJoined, got {:?}", other),
        }
    }

    #[test]
    fn players_list_negative_ping_parses_and_normalizes_to_none() {
        let json = r#"{"type": "players_list", "payload": [{"name": "Alice", "ping": -1}, {"name": "Bob", "ping": 40}]}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::PlayersList(entries) => {
                assert_eq!(entries.len(), 2);
                assert_eq!(entries[0].ping, Some(-1));
                assert_eq!(normalize_ping(entries[0].ping), None);
                assert_eq!(normalize_ping(entries[1].ping), Some(40));
            }
            other => panic!("Expected PlayersList, got {:?}", other),
        }
    }

    #[test]
    fn player_info_from_entry_normalizes_negative_ping() {
        // The `players_list` full-replace path stores normalized pings.
        let entry = PlayerEntry {
            name: "Alice".to_string(),
            level: Some(5),
            ping: Some(-1),
            color: None,
            is_host: None,
        };
        let info = player_info_from_entry(entry, Some("Host"));
        assert_eq!(info.ping, None);
        assert_eq!(info.level, Some(5));
        assert!(!info.is_host);
    }

    #[test]
    fn negative_ping_does_not_overwrite_known_ping() {
        // Non-destructive merge: a `-1`/unknown ping must not wipe a known
        // one, while a real ping still updates it.
        let mut players = vec![crate::lobby::PlayerInfo {
            name: "Alice".to_string(),
            level: Some(5),
            ping: Some(30),
            color: Some("red".to_string()),
            is_host: false,
        }];
        let unknown = PlayerEntry {
            name: "Alice".to_string(),
            level: Some(6),
            ping: Some(-1),
            color: None,
            is_host: None,
        };
        upsert_player(&mut players, &unknown, normalize_ping(unknown.ping), false);
        assert_eq!(players[0].ping, Some(30)); // known ping preserved
        assert_eq!(players[0].level, Some(6)); // known level still updated
        assert_eq!(players[0].color, Some("red".to_string())); // preserved

        let known = PlayerEntry {
            name: "Alice".to_string(),
            level: None,
            ping: Some(50),
            color: None,
            is_host: None,
        };
        upsert_player(&mut players, &known, normalize_ping(known.ping), false);
        assert_eq!(players[0].ping, Some(50)); // real ping updates
    }

    #[test]
    fn players_list_payload_negative_ping_emits_null() {
        // End-to-end: a raw `-1` entry becomes `None` in stored state and
        // `null` in the emitted `players_list` payload — never `-1`.
        let entry = PlayerEntry {
            name: "Alice".to_string(),
            level: None,
            ping: Some(-1),
            color: None,
            is_host: None,
        };
        let payload = players_list_payload(&[player_info_from_entry(entry, None)]);
        assert_eq!(payload[0]["ping"], serde_json::Value::Null);
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

    // ---- Regression tests: EXACT payloads the mod actually sends ----
    //
    // The original bug slipped through because every fixture above is
    // synthetic snake_case. The mod (`Among API/Plugin.cs`) serializes C#
    // anonymous types with DEFAULT `JsonSerializer` settings, so property
    // names are verbatim camelCase / mixedCase. These tests encode that
    // real contract and fail against the old snake_case-only enum.

    #[test]
    fn real_mod_lobby_created_parses() {
        // Shape copied from Among API/Plugin.cs LobbyCreated handler.
        let json = r#"{
            "type": "lobby_created",
            "payload": {
                "code": "ABCDEF",
                "region": "NA",
                "regionIp": "1.2.3.4",
                "regionPort": 22023,
                "host": "HostPlayer",
                "playerCount": 4,
                "maxPlayers": 10,
                "playerNames": ["HostPlayer", "Alice", "Bob", "Carol"],
                "playerLevels": [1, 5, 7, 9],
                "playerPings": [0, 30, 45, 60],
                "playerColors": ["red", "blue", "green", "pink"],
                "mod_type": "vanilla",
                "status": "lobby",
                "mods": [],
                "game_version": "2023.7.12",
                "map_name": "The Skeld",
                "language": "en",
                "chat_type": "chat",
                "isHost": true
            }
        }"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::LobbyCreated {
                code,
                region,
                region_ip,
                region_port,
                host,
                max_players,
                map,
                is_host,
                player_names,
                player_levels,
                player_pings,
                player_colors,
            } => {
                assert_eq!(code, "ABCDEF");
                assert_eq!(region, "NA");
                assert_eq!(region_ip, Some("1.2.3.4".to_string())); // regionIp
                assert_eq!(region_port, Some(22023)); // regionPort
                assert_eq!(host, "HostPlayer");
                assert_eq!(max_players, 10); // maxPlayers
                assert_eq!(map, "The Skeld"); // map_name
                assert!(is_host); // isHost
                assert_eq!(player_names, vec!["HostPlayer", "Alice", "Bob", "Carol"]);
                assert_eq!(player_levels, vec![1, 5, 7, 9]);
                assert_eq!(player_pings, vec![0, 30, 45, 60]);
                assert_eq!(player_colors, vec!["red", "blue", "green", "pink"]);
            }
            other => panic!("Expected LobbyCreated, got {:?}", other),
        }
    }

    #[test]
    fn real_mod_lobby_created_without_is_host_parses() {
        // Mod builds predating the isHost addition must still parse.
        let json = r#"{
            "type": "lobby_created",
            "payload": {
                "code": "ABCDEF",
                "region": "NA",
                "host": "HostPlayer",
                "maxPlayers": 15,
                "map_name": "Polus"
            }
        }"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::LobbyCreated {
                max_players,
                map,
                is_host,
                ..
            } => {
                assert_eq!(max_players, 15);
                assert_eq!(map, "Polus");
                assert!(!is_host);
            }
            other => panic!("Expected LobbyCreated, got {:?}", other),
        }
    }

    #[test]
    fn real_mod_lobby_created_via_envelope_is_not_unknown() {
        // The swallow at `From<IpcEnvelope>` turned parse failures into
        // `Unknown`, which is why the bug was silent. Exercise the full
        // envelope → IpcMessage path with the real payload.
        let envelope = IpcEnvelope {
            msg_type: "lobby_created".to_string(),
            id: "test-id".to_string(),
            timestamp: 1234567890,
            payload: Some(serde_json::json!({
                "code": "ABCDEF",
                "region": "NA",
                "host": "HostPlayer",
                "maxPlayers": 10,
                "map_name": "The Skeld",
                "isHost": false
            })),
        };
        let msg: IpcMessage = envelope.into();
        assert!(
            matches!(msg, IpcMessage::LobbyCreated { .. }),
            "real mod payload must not degrade to Unknown: {:?}",
            msg
        );
    }

    // ---- R4: roster seeding from `lobby_created` ----

    #[test]
    fn real_mod_lobby_created_roster_parses_and_seeds_players() {
        // Exact shape from Among API/Plugin.cs (playerNames/Levels/Pings).
        let json = r#"{
            "type": "lobby_created",
            "payload": {
                "code": "ABCDEF",
                "region": "NA",
                "host": "HostPlayer",
                "maxPlayers": 10,
                "map_name": "The Skeld",
                "isHost": true,
                "playerNames": ["HostPlayer", "Alice", "Bob"],
                "playerLevels": [1, 5, 7],
                "playerPings": [0, 30, 45],
                "playerColors": ["red", "blue", "green"]
            }
        }"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::LobbyCreated {
                player_names,
                player_levels,
                player_pings,
                player_colors,
                host,
                ..
            } => {
                assert_eq!(player_names, vec!["HostPlayer", "Alice", "Bob"]);
                assert_eq!(player_levels, vec![1, 5, 7]);
                assert_eq!(player_pings, vec![0, 30, 45]);
                assert_eq!(player_colors, vec!["red", "blue", "green"]);

                let players = seed_players_from_roster(
                    &player_names,
                    &player_levels,
                    &player_pings,
                    &player_colors,
                    Some(&host),
                );
                assert_eq!(players.len(), 3);
                assert_eq!(players[0].name, "HostPlayer");
                assert_eq!(players[0].level, Some(1));
                assert_eq!(players[0].ping, Some(0));
                assert_eq!(players[0].color, Some("red".to_string()));
                assert!(players[0].is_host); // name matches lobby host
                assert_eq!(players[1].name, "Alice");
                assert_eq!(players[1].level, Some(5));
                assert_eq!(players[1].ping, Some(30));
                assert_eq!(players[1].color, Some("blue".to_string()));
                assert!(!players[1].is_host);
                assert_eq!(players[2].name, "Bob");
                assert_eq!(players[2].color, Some("green".to_string()));
                assert!(!players[2].is_host);
            }
            other => panic!("Expected LobbyCreated, got {:?}", other),
        }
    }

    #[test]
    fn lobby_created_absent_roster_parses_as_empty() {
        // Older mod builds / fixtures omit the roster entirely.
        let json = r#"{"type": "lobby_created", "payload": {"code": "ABCDEF", "region": "NA", "host": "HostPlayer", "maxPlayers": 10, "map_name": "The Skeld"}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::LobbyCreated {
                player_names,
                player_levels,
                player_pings,
                player_colors,
                ..
            } => {
                assert!(player_names.is_empty());
                assert!(player_levels.is_empty());
                assert!(player_pings.is_empty());
                assert!(player_colors.is_empty());
            }
            other => panic!("Expected LobbyCreated, got {:?}", other),
        }
    }

    #[test]
    fn seed_players_from_roster_short_arrays_do_not_panic() {
        // levels/pings/colors shorter than names (or absent) → None, never a
        // panic.
        let names = vec!["Host".to_string(), "Alice".to_string(), "Bob".to_string()];
        let players =
            seed_players_from_roster(&names, &[3], &[], &["red".to_string()], Some("Host"));
        assert_eq!(players.len(), 3);
        assert_eq!(players[0].level, Some(3));
        assert_eq!(players[0].ping, None);
        assert_eq!(players[0].color, Some("red".to_string()));
        assert_eq!(players[1].level, None);
        assert_eq!(players[1].ping, None);
        assert_eq!(players[1].color, None); // colors shorter → None
        assert_eq!(players[2].level, None);
        assert_eq!(players[2].ping, None);
        assert_eq!(players[2].color, None);
        assert!(players[0].is_host);
        assert!(!players[1].is_host);
    }

    #[test]
    fn seed_players_from_roster_empty_color_becomes_none() {
        // The mod sends `""` for an unknown color (index-aligned) — that must
        // map to None, not an empty-string color.
        let names = vec!["Alice".to_string(), "Bob".to_string()];
        let colors = vec!["".to_string(), "blue".to_string()];
        let players = seed_players_from_roster(&names, &[], &[], &colors, None);
        assert_eq!(players[0].color, None);
        assert_eq!(players[1].color, Some("blue".to_string()));
    }

    #[test]
    fn seed_players_from_roster_negative_values_become_none() {
        // The mod uses negative sentinels for unknown level/ping; they must
        // not wrap into a bogus u32.
        let names = vec!["Alice".to_string()];
        let players = seed_players_from_roster(&names, &[-1], &[-1], &[], None);
        assert_eq!(players[0].level, None);
        assert_eq!(players[0].ping, None);
        assert_eq!(players[0].color, None);
    }

    #[test]
    fn seed_players_from_roster_empty_names_is_empty() {
        let players = seed_players_from_roster(&[], &[1, 2], &[3, 4], &["red".to_string()], Some("Host"));
        assert!(players.is_empty());
    }

    #[test]
    fn players_list_payload_shape_is_stable() {
        // The emitted `players_list` array element shape must stay
        // `{name, level, ping, color, is_host}` — the frontend listener
        // (F3) and `player-joined` consumers depend on it.
        let players = vec![crate::lobby::PlayerInfo {
            name: "Alice".to_string(),
            level: Some(5),
            ping: Some(30),
            color: Some("#ff0000".to_string()),
            is_host: true,
        }];
        let payload = players_list_payload(&players);
        assert_eq!(
            payload,
            serde_json::json!([{
                "name": "Alice",
                "level": 5,
                "ping": 30,
                "color": "#ff0000",
                "is_host": true,
            }])
        );
    }

    #[test]
    fn players_list_payload_none_fields_serialize_as_null() {
        let players = vec![crate::lobby::PlayerInfo {
            name: "Bob".to_string(),
            level: None,
            ping: None,
            color: None,
            is_host: false,
        }];
        assert_eq!(
            players_list_payload(&players),
            serde_json::json!([{
                "name": "Bob",
                "level": null,
                "ping": null,
                "color": null,
                "is_host": false,
            }])
        );
    }

    #[test]
    fn real_mod_lobby_closed_object_parses() {
        // Shape copied from Among API/Plugin.cs LobbyClosed handler:
        // an OBJECT (not the old unit variant). `reason` is extra/ignored.
        let json = r#"{"type": "lobby_closed", "payload": {"code": "ABCDEF", "reason": "disbanded"}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::LobbyClosed { is_host } => assert!(!is_host),
            other => panic!("Expected LobbyClosed, got {:?}", other),
        }
    }

    #[test]
    fn real_mod_lobby_closed_with_is_host_parses() {
        let json = r#"{"type": "lobby_closed", "payload": {"code": "ABCDEF", "reason": "disbanded", "isHost": true}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::LobbyClosed { is_host } => assert!(is_host),
            other => panic!("Expected LobbyClosed, got {:?}", other),
        }
    }

    #[test]
    fn real_mod_player_joined_uses_player_name() {
        // Shape copied from Among API/Plugin.cs PlayerJoined handler.
        let json = r#"{"type": "player_joined", "payload": {"playerName": "Alice", "playerCount": 3}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::PlayerJoined(entry) => {
                assert_eq!(entry.name, "Alice"); // playerName
                assert_eq!(entry.level, None);
                assert_eq!(entry.ping, None);
                assert_eq!(entry.color, None);
                assert_eq!(entry.is_host, None);
            }
            other => panic!("Expected PlayerJoined, got {:?}", other),
        }
    }

    #[test]
    fn real_mod_player_left_uses_player_name() {
        // Shape copied from Among API/Plugin.cs PlayerLeft handler.
        let json = r#"{"type": "player_left", "payload": {"playerName": "Charlie", "playerCount": 2}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::PlayerLeft { name } => assert_eq!(name, "Charlie"), // playerName
            other => panic!("Expected PlayerLeft, got {:?}", other),
        }
    }

    #[test]
    fn test_parse_join_result_success() {
        let json = r#"{"type": "join_lobby_result", "payload": {"success": true}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::JoinResult { success, error, code } => {
                assert!(success);
                assert_eq!(error, None);
                // Older mod build: `code` absent → None.
                assert_eq!(code, None);
            }
            _ => panic!("Expected JoinResult"),
        }
    }

    #[test]
    fn test_parse_join_result_failure() {
        let json = r#"{"type": "join_lobby_result", "payload": {"success": false, "error": "Lobby full"}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::JoinResult { success, error, code } => {
                assert!(!success);
                assert_eq!(error, Some("Lobby full".to_string()));
                assert_eq!(code, None);
            }
            _ => panic!("Expected JoinResult"),
        }
    }

    #[test]
    fn test_parse_join_result_with_code() {
        // Sibling mod change: `join_lobby_result` now carries the lobby code.
        let json = r#"{"type": "join_lobby_result", "payload": {"success": true, "error": null, "code": "AB12CD"}}"#;
        let msg: IpcMessage = serde_json::from_str(json).unwrap();
        match msg {
            IpcMessage::JoinResult { success, error, code } => {
                assert!(success);
                assert_eq!(error, None);
                assert_eq!(code, Some("AB12CD".to_string()));
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
        assert!(matches!(msg, IpcMessage::GameReady { protocol: None }));
    }

    // ---- protocol contract (A2a) ----

    #[test]
    fn protocol_reason_none_when_version_matches() {
        assert_eq!(protocol_mismatch_reason(Some(EXPECTED_MOD_PROTOCOL)), None);
    }

    #[test]
    fn protocol_reason_flags_other_versions() {
        let reason = protocol_mismatch_reason(Some(1)).expect("v1 must mismatch");
        assert!(reason.contains("v1"), "reason: {}", reason);
        assert!(reason.contains("v3"), "reason: {}", reason);
        // A future protocol is rejected too (forward incompatibility).
        assert!(protocol_mismatch_reason(Some(4)).is_some());
    }

    #[test]
    fn protocol_reason_flags_absent_protocol() {
        // An old mod predates the protocol field: deliberate mismatch.
        let reason = protocol_mismatch_reason(None).expect("absent protocol must mismatch");
        assert!(reason.contains("protocol"), "reason: {}", reason);
        assert!(reason.contains("update"), "reason: {}", reason);
    }

    #[test]
    fn mod_incompatible_payload_shape_is_stable() {
        // Frontend contract: the `mod-incompatible` event payload is exactly
        // `{ "reason": "<string>" }`.
        assert_eq!(
            mod_incompatible_payload("AmongApi protocol v1 is not supported (expected v3)"),
            serde_json::json!({ "reason": "AmongApi protocol v1 is not supported (expected v3)" })
        );
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

    #[test]
    fn host_is_known_rejects_blank_and_unknown() {
        // The fallback seed must not flag a blank or literal "UNKNOWN"
        // host — the mod uses "UNKNOWN" as its unresolved-name sentinel
        // (case-insensitively), mirroring the mod's own `ResolveIsHost`.
        assert!(host_is_known("Alice"));
        assert!(host_is_known("unknownPlayer"));
        assert!(!host_is_known(""));
        assert!(!host_is_known("UNKNOWN"));
        assert!(!host_is_known("unknown"));
        assert!(!host_is_known("UnKnOwN"));
    }

    #[test]
    fn lobby_closed_payload_shape_is_stable() {
        // Every `lobby-closed` producer (the `LobbyClosed` IPC arm,
        // `disband_lobby`, and the pipe disconnect path) must emit this
        // exact shape — the frontend listener keys off `isHost`.
        assert_eq!(
            lobby_closed_payload(true),
            serde_json::json!({ "isHost": true })
        );
        assert_eq!(
            lobby_closed_payload(false),
            serde_json::json!({ "isHost": false })
        );
    }
}

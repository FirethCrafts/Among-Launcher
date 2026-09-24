use crate::error::LauncherError;

pub(crate) const BASE_URL: &str = "https://among-us.mel-homes.com";

pub struct LobbyBackendClient {
    client: reqwest::Client,
    token: String,
}

/// The connection endpoint the backend stored for a posted lobby, as
/// returned (flat snake_case) by `GET /api/v1/lobby/{code}/details`.
/// The launcher forwards these to the game's `join_lobby` IPC handler so
/// joiners can reach the host's region server directly.
#[derive(Debug, Clone, serde::Deserialize)]
pub struct LobbyEndpoint {
    #[serde(default)]
    pub region: Option<String>,
    #[serde(default)]
    pub region_ip: Option<String>,
    #[serde(default)]
    pub region_port: Option<u32>,
}

/// `mod_type` this launcher always posts. It only ever hosts from its own
/// BepInEx + AmongApi ("modded") install — there is no vanilla hosting path —
/// so this is a constant rather than config-derived. The backend validates it
/// against `^(modded|vanilla)$` (`backend/app/schemas.py` `LobbyCreate.mod_type`).
pub(crate) const MOD_TYPE_MODDED: &str = "modded";

/// The backend's `LobbyCreate.status` default, sent explicitly for clarity.
/// Backend pattern: `^(lobby|in_game)$`.
pub(crate) const STATUS_LOBBY: &str = "lobby";

/// Deterministic id for a player, shared by the `players` array we POST and
/// the kick endpoint.
///
/// The backend's `PlayerCreate.id` is REQUIRED and its `KickRequest.player_id`
/// addresses a player by that same id (`routes.py` broadcasts
/// `target_id: payload.player_id`). The launcher's kick command only knows a
/// player's *name* (`kick_player(player_name)`), so the id is a pure function
/// of the name: it is stable across roster reordering/joins and can be
/// recovered from `lobby_state.players` by name without storing anything
/// extra. (The backend also keys dashboard rows on this id.)
pub(crate) fn player_id(name: &str) -> String {
    name.to_string()
}

/// Build the `players` array sent to the backend, one entry per
/// `lobby_state.players` element, each matching the backend's `PlayerCreate`:
/// `{ id, name, is_host, level, ping, color }`.
///
/// `id` is synthesized via [`player_id`] (required by the backend). `level`
/// and `ping` are `null` when unknown — the mod's `-1` sentinel already maps
/// to `None` on ingest, so this never fabricates a `0`. `color` is `null`
/// when unknown.
fn players_payload(players: &[crate::lobby::PlayerInfo]) -> serde_json::Value {
    serde_json::Value::Array(
        players
            .iter()
            .map(|p| {
                serde_json::json!({
                    "id": player_id(&p.name),
                    "name": p.name,
                    "is_host": p.is_host,
                    "level": p.level,
                    "ping": p.ping,
                    "color": p.color,
                })
            })
            .collect(),
    )
}

/// Build the JSON body for `POST /api/v1/lobbies`.
///
/// Mirrors the backend's `LobbyCreate` schema (`backend/app/schemas.py`):
/// `code`, `region`, and `host` are REQUIRED (the old payload omitted `host`,
/// which made the endpoint answer 422); `mod_type` must match
/// `^(modded|vanilla)$`; `status` must match `^(lobby|in_game)$`;
/// `max_players`, `region_ip`, `region_port`, `map_name`, and `players` are
/// optional.
///
/// Kept pure and separate from the HTTP call so its shape can be unit-tested
/// without a network round-trip (same idea as `join_payload` in `lib.rs`).
///
/// Deliberately NOT included: `game_version`, `language`, `chat_type`, and
/// `mods`. The mod sends the first three on `lobby_created` but the launcher's
/// `IpcMessage::LobbyCreated` parser does not read them, so fabricating them
/// here would be worse than omitting them (all are optional on the backend).
#[allow(clippy::too_many_arguments)]
pub(crate) fn create_lobby_payload(
    code: &str,
    region: &str,
    host: &str,
    max_players: u32,
    region_ip: Option<&str>,
    region_port: Option<u32>,
    mod_type: &str,
    status: &str,
    map_name: Option<&str>,
    players: &[crate::lobby::PlayerInfo],
) -> serde_json::Value {
    serde_json::json!({
        "code": code,
        "region": region,
        "host": host,
        "mod_type": mod_type,
        "status": status,
        "max_players": max_players,
        "region_ip": region_ip,
        "region_port": region_port,
        "map_name": map_name,
        "players": players_payload(players),
    })
}

/// Build the JSON body for `POST /api/v1/lobbies/{code}/kick`.
///
/// The backend's `KickRequest` requires `player_id` (NOT `player_name`); the
/// old `{ "player_name": ... }` body 422'd. Pure for unit-testing.
pub(crate) fn kick_payload(player_id: &str) -> serde_json::Value {
    serde_json::json!({ "player_id": player_id })
}

#[allow(dead_code)]
impl LobbyBackendClient {
    pub fn new(token: String) -> Self {
        Self { client: reqwest::Client::new(), token }
    }

    #[allow(clippy::too_many_arguments)]
    pub async fn create_lobby(
        &self,
        code: &str,
        region: &str,
        host: &str,
        max_players: u32,
        region_ip: Option<&str>,
        region_port: Option<u32>,
        mod_type: &str,
        status: &str,
        map_name: Option<&str>,
        players: &[crate::lobby::PlayerInfo],
    ) -> Result<serde_json::Value, LauncherError>
    {
        let payload = create_lobby_payload(
            code, region, host, max_players, region_ip, region_port, mod_type, status, map_name,
            players,
        );
        let resp = self.client.post(format!("{}/api/v1/lobbies", BASE_URL))
            .bearer_auth(&self.token)
            .json(&payload)
            .send().await.map_err(|e| LauncherError::Network(e.to_string()))?
            .error_for_status().map_err(|e| LauncherError::Network(e.to_string()))?;
        resp.json().await.map_err(|e| LauncherError::Network(e.to_string()))
    }

    pub async fn heartbeat(&self, code: &str) -> Result<(), LauncherError> {
        self.client.post(format!("{}/api/v1/lobbies/{}/heartbeat", BASE_URL, code))
            .bearer_auth(&self.token)
            .send().await.map_err(|e| LauncherError::Network(e.to_string()))?
            .error_for_status().map_err(|e| LauncherError::Network(e.to_string()))?;
        Ok(())
    }

    pub async fn kick(&self, code: &str, player_id: &str) -> Result<(), LauncherError> {
        self.client.post(format!("{}/api/v1/lobbies/{}/kick", BASE_URL, code))
            .bearer_auth(&self.token)
            .json(&kick_payload(player_id))
            .send().await.map_err(|e| LauncherError::Network(e.to_string()))?
            .error_for_status().map_err(|e| LauncherError::Network(e.to_string()))?;
        Ok(())
    }

    pub async fn disband(&self, code: &str) -> Result<(), LauncherError> {
        self.client.delete(format!("{}/api/v1/lobbies/{}", BASE_URL, code))
            .bearer_auth(&self.token)
            .send().await.map_err(|e| LauncherError::Network(e.to_string()))?
            .error_for_status().map_err(|e| LauncherError::Network(e.to_string()))?;
        Ok(())
    }

    /// Disband, treating a 404 (lobby already gone) as success.
    ///
    /// A DELETE returning 404 means the backend lobby has already expired or
    /// was disbanded by another path — that is the desired end state, so it
    /// must not abort the caller before it clears its local state and notifies
    /// the frontend. Any other non-2xx status is still a real error. Mirrors
    /// `get_lobby_endpoint`'s 404 handling.
    pub async fn disband_allow_missing(&self, code: &str) -> Result<(), LauncherError> {
        let resp = self.client.delete(format!("{}/api/v1/lobbies/{}", BASE_URL, code))
            .bearer_auth(&self.token)
            .send().await.map_err(|e| LauncherError::Network(e.to_string()))?;

        // 404 is "already gone" — the operation's goal is met.
        if resp.status() == reqwest::StatusCode::NOT_FOUND {
            return Ok(());
        }

        resp.error_for_status().map_err(|e| LauncherError::Network(e.to_string()))?;
        Ok(())
    }

    pub async fn repost(&self, code: &str) -> Result<(), LauncherError> {
        self.client.post(format!("{}/api/v1/lobbies/{}/repost", BASE_URL, code))
            .bearer_auth(&self.token)
            .send().await.map_err(|e| LauncherError::Network(e.to_string()))?
            .error_for_status().map_err(|e| LauncherError::Network(e.to_string()))?;
        Ok(())
    }

    /// Look up a posted lobby's connection endpoint.
    ///
    /// `Ok(None)` means the lobby is simply not on the backend (HTTP 404 —
    /// e.g. it was never posted, or was already disbanded). That is a benign,
    /// expected outcome: the caller falls back to an empty region so the
    /// game can still attempt the join with its current region. Any other
    /// non-2xx status is a real error.
    pub async fn get_lobby_endpoint(&self, code: &str)
        -> Result<Option<LobbyEndpoint>, LauncherError>
    {
        let resp = self.client.get(format!("{}/api/v1/lobby/{}/details", BASE_URL, code))
            .bearer_auth(&self.token)
            .send().await.map_err(|e| LauncherError::Network(e.to_string()))?;

        // 404 is "not posted / already gone" — not an error for the caller.
        if resp.status() == reqwest::StatusCode::NOT_FOUND {
            return Ok(None);
        }

        let resp = resp
            .error_for_status()
            .map_err(|e| LauncherError::Network(e.to_string()))?;
        let endpoint = resp
            .json::<LobbyEndpoint>()
            .await
            .map_err(|e| LauncherError::Network(e.to_string()))?;
        Ok(Some(endpoint))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_players() -> Vec<crate::lobby::PlayerInfo> {
        vec![
            crate::lobby::PlayerInfo {
                name: "HostPlayer".to_string(),
                level: Some(1),
                ping: Some(0),
                color: Some("red".to_string()),
                is_host: true,
            },
            crate::lobby::PlayerInfo {
                name: "Alice".to_string(),
                level: Some(5),
                ping: None, // unknown ping must serialize as null, never 0
                color: None,
                is_host: false,
            },
        ]
    }

    fn full_payload() -> serde_json::Value {
        create_lobby_payload(
            "ABCDEF",
            "NA",
            "HostPlayer",
            10,
            Some("1.2.3.4"),
            Some(22023),
            MOD_TYPE_MODDED,
            STATUS_LOBBY,
            Some("The Skeld"),
            &sample_players(),
        )
    }

    #[test]
    fn create_lobby_payload_matches_backend_schema() {
        // Every key the backend's `LobbyCreate` accepts, with the values we
        // actually send. `host` is the one the old payload omitted (the 422).
        assert_eq!(
            full_payload(),
            serde_json::json!({
                "code": "ABCDEF",
                "region": "NA",
                "host": "HostPlayer",
                "mod_type": "modded",
                "status": "lobby",
                "max_players": 10,
                "region_ip": "1.2.3.4",
                "region_port": 22023,
                "map_name": "The Skeld",
                "players": [
                    {
                        "id": "HostPlayer",
                        "name": "HostPlayer",
                        "is_host": true,
                        "level": 1,
                        "ping": 0,
                        "color": "red",
                    },
                    {
                        "id": "Alice",
                        "name": "Alice",
                        "is_host": false,
                        "level": 5,
                        "ping": null,
                        "color": null,
                    },
                ],
            })
        );
    }

    #[test]
    fn create_lobby_payload_players_match_backend_player_create_shape() {
        // Backend `PlayerCreate` requires `id` and `name`; `level`/`ping`/
        // `color` are optional. `id` must be a non-empty string.
        let payload = full_payload();
        let players = payload["players"].as_array().expect("players array");
        assert_eq!(players.len(), 2);
        for p in players {
            assert!(p["id"].as_str().map(|s| !s.is_empty()).unwrap_or(false));
            assert!(p["name"].is_string());
            assert!(p["is_host"].is_boolean());
        }
        // Unknown ping is null — never a fabricated 0.
        assert!(players[1]["ping"].is_null());
        assert!(players[1]["color"].is_null());
    }

    #[test]
    fn player_id_is_deterministic_and_name_derived() {
        // Kick resolves the id from a player's name, so the same name must
        // always yield the same id.
        assert_eq!(player_id("Alice"), player_id("Alice"));
        assert_eq!(player_id("Alice"), "Alice");
        assert_ne!(player_id("Alice"), player_id("Bob"));
    }

    #[test]
    fn create_lobby_payload_empty_players_serializes_as_empty_array() {
        // No roster reported: still send `players` (an empty array), which the
        // backend treats as falsy and falls back to its host-only seed.
        let payload = create_lobby_payload(
            "ABCDEF",
            "NA",
            "HostPlayer",
            10,
            None,
            None,
            MOD_TYPE_MODDED,
            STATUS_LOBBY,
            None,
            &[],
        );
        assert_eq!(payload["players"], serde_json::json!([]));
    }

    #[test]
    fn kick_payload_sends_player_id_not_player_name() {
        // Backend `KickRequest` requires `player_id`; the old
        // `player_name` key 422'd.
        assert_eq!(
            kick_payload("Alice"),
            serde_json::json!({ "player_id": "Alice" })
        );
        assert!(kick_payload("Alice").get("player_name").is_none());
    }

    #[test]
    fn create_lobby_payload_always_includes_required_host_key() {
        // The backend requires `host: str`; the key must be present even when
        // the caller passes an empty string (the guard lives in `post_lobby`,
        // but the payload builder must never silently drop the key).
        let payload = create_lobby_payload(
            "ABCDEF",
            "NA",
            "",
            10,
            None,
            None,
            MOD_TYPE_MODDED,
            STATUS_LOBBY,
            None,
            &[],
        );
        assert!(payload.get("host").is_some());
        assert_eq!(payload["host"], "");
        // And the other two required keys are always present too.
        assert!(payload.get("code").is_some());
        assert!(payload.get("region").is_some());
    }

    #[test]
    fn create_lobby_payload_serializes_absent_optionals_as_null() {
        // `LobbyCreate` declares all three optional with default None, so an
        // explicit JSON null is accepted — we do not omit the keys.
        let payload = create_lobby_payload(
            "ABCDEF",
            "NA",
            "HostPlayer",
            10,
            None,
            None,
            MOD_TYPE_MODDED,
            STATUS_LOBBY,
            None,
            &[],
        );
        assert!(payload["region_ip"].is_null());
        assert!(payload["region_port"].is_null());
        assert!(payload["map_name"].is_null());
    }

    #[test]
    fn create_lobby_payload_mod_type_and_status_satisfy_backend_patterns() {
        // backend/app/schemas.py: mod_type ^(modded|vanilla)$, status ^(lobby|in_game)$.
        let payload = full_payload();
        assert!(matches!(
            payload["mod_type"].as_str(),
            Some("modded") | Some("vanilla")
        ));
        assert!(matches!(
            payload["status"].as_str(),
            Some("lobby") | Some("in_game")
        ));
    }

    #[test]
    fn create_lobby_payload_omits_fields_the_launcher_does_not_parse() {
        // game_version/language/chat_type ride along on the mod's
        // `lobby_created` but `IpcMessage::LobbyCreated` ignores them, so the
        // launcher must not invent values. `mods` is optional on the backend
        // and not trivially available here. `players` IS sent (see above).
        let payload = full_payload();
        assert!(payload.get("game_version").is_none());
        assert!(payload.get("language").is_none());
        assert!(payload.get("chat_type").is_none());
        assert!(payload.get("mods").is_none());
        assert!(payload.get("players").is_some());
    }
}

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

#[allow(dead_code)]
impl LobbyBackendClient {
    pub fn new(token: String) -> Self {
        Self { client: reqwest::Client::new(), token }
    }

    pub async fn create_lobby(
        &self,
        code: &str,
        region: &str,
        max_players: u32,
        region_ip: Option<&str>,
        region_port: Option<u32>,
    ) -> Result<serde_json::Value, LauncherError>
    {
        let resp = self.client.post(format!("{}/api/v1/lobbies", BASE_URL))
            .bearer_auth(&self.token)
            .json(&serde_json::json!({
                "code": code, "region": region, "max_players": max_players,
                "region_ip": region_ip, "region_port": region_port,
            }))
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

    pub async fn kick(&self, code: &str, player_name: &str) -> Result<(), LauncherError> {
        self.client.post(format!("{}/api/v1/lobbies/{}/kick", BASE_URL, code))
            .bearer_auth(&self.token)
            .json(&serde_json::json!({ "player_name": player_name }))
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

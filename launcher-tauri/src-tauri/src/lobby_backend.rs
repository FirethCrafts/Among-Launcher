use crate::error::LauncherError;

const BASE_URL: &str = "https://among-us.mel-homes.com";

pub struct LobbyBackendClient {
    client: reqwest::Client,
    token: String,
}

impl LobbyBackendClient {
    pub fn new(token: String) -> Self {
        Self { client: reqwest::Client::new(), token }
    }

    pub async fn create_lobby(&self, code: &str, region: &str, max_players: u32)
        -> Result<serde_json::Value, LauncherError>
    {
        let resp = self.client.post(format!("{}/api/v1/lobbies", BASE_URL))
            .bearer_auth(&self.token)
            .json(&serde_json::json!({
                "code": code, "region": region, "max_players": max_players,
            }))
            .send().await.map_err(|e| LauncherError::Network(e.to_string()))?;
        resp.json().await.map_err(|e| LauncherError::Network(e.to_string()))
    }

    pub async fn heartbeat(&self, code: &str) -> Result<(), LauncherError> {
        self.client.post(format!("{}/api/v1/lobbies/{}/heartbeat", BASE_URL, code))
            .bearer_auth(&self.token)
            .send().await.map_err(|e| LauncherError::Network(e.to_string()))?;
        Ok(())
    }

    pub async fn kick(&self, code: &str, player_name: &str) -> Result<(), LauncherError> {
        self.client.post(format!("{}/api/v1/lobbies/{}/kick", BASE_URL, code))
            .bearer_auth(&self.token)
            .json(&serde_json::json!({ "player_name": player_name }))
            .send().await.map_err(|e| LauncherError::Network(e.to_string()))?;
        Ok(())
    }

    pub async fn disband(&self, code: &str) -> Result<(), LauncherError> {
        self.client.delete(format!("{}/api/v1/lobbies/{}", BASE_URL, code))
            .bearer_auth(&self.token)
            .send().await.map_err(|e| LauncherError::Network(e.to_string()))?;
        Ok(())
    }

    pub async fn repost(&self, code: &str) -> Result<(), LauncherError> {
        self.client.post(format!("{}/api/v1/lobbies/{}/repost", BASE_URL, code))
            .bearer_auth(&self.token)
            .send().await.map_err(|e| LauncherError::Network(e.to_string()))?;
        Ok(())
    }

    pub async fn get_lobby(&self, code: &str) -> Result<serde_json::Value, LauncherError> {
        let resp = self.client.get(format!("{}/api/v1/lobby/{}/details", BASE_URL, code))
            .bearer_auth(&self.token)
            .send().await.map_err(|e| LauncherError::Network(e.to_string()))?;
        resp.json().await.map_err(|e| LauncherError::Network(e.to_string()))
    }
}

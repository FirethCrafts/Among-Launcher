use crate::error::LauncherError;

const CLIENT_ID: &str = "1533706803748147240";
const CLIENT_SECRET: &str = "D_pwSkYUjRKkGw7YEdFsHAZZj4EUxPA4";
const REDIRECT_URI: &str = "https://among-us.mel-homes.com/appLogin";

pub struct DiscordAuth;

impl DiscordAuth {
    pub fn new() -> Result<Self, LauncherError> {
        Ok(Self)
    }

    pub fn authorize_url() -> String {
        format!(
            "https://discord.com/api/oauth2/authorize?client_id={}&redirect_uri={}&response_type=code&scope=identify",
            CLIENT_ID,
            urlencoding::encode(REDIRECT_URI),
        )
    }

    pub async fn exchange_token(code: &str) -> Result<TokenResponse, LauncherError> {
        let client = reqwest::Client::new();
        let resp = client
            .post("https://discord.com/api/oauth2/token")
            .form(&[
                ("client_id", CLIENT_ID),
                ("client_secret", CLIENT_SECRET),
                ("grant_type", "authorization_code"),
                ("code", code),
                ("redirect_uri", REDIRECT_URI),
            ])
            .send()
            .await
            .map_err(|e| LauncherError::Network(e.to_string()))?;

        if !resp.status().is_success() {
            let status = resp.status();
            let body = resp.text().await.unwrap_or_default();
            return Err(LauncherError::Auth(format!(
                "Token exchange failed ({}): {}",
                status, body
            )));
        }

        resp.json()
            .await
            .map_err(|e| LauncherError::Network(e.to_string()))
    }

    pub async fn fetch_user(access_token: &str) -> Result<UserInfo, LauncherError> {
        let client = reqwest::Client::new();
        let resp = client
            .get("https://discord.com/api/v10/users/@me")
            .bearer_auth(access_token)
            .send()
            .await
            .map_err(|e| LauncherError::Network(e.to_string()))?;
        resp.json()
            .await
            .map_err(|e| LauncherError::Network(e.to_string()))
    }

    /// Extract OAuth code from amonglauncher:// deep link URL
    pub fn extract_code_from_url(url: &str) -> Option<String> {
        if url.starts_with("amonglauncher://callback") {
            url.split("code=")
                .nth(1)
                .and_then(|s| s.split('&').next())
                .map(|s| s.to_string())
        } else {
            None
        }
    }
}

#[derive(serde::Deserialize)]
pub struct TokenResponse {
    pub access_token: String,
}

#[derive(serde::Serialize, serde::Deserialize)]
pub struct UserInfo {
    pub id: String,
    pub username: String,
    pub global_name: Option<String>,
    pub avatar: Option<String>,
}

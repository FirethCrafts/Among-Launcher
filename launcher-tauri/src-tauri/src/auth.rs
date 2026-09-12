use crate::error::LauncherError;
use tiny_http::Server;

const CLIENT_ID: &str = "1533706803748147240";
const CLIENT_SECRET: &str = "Um7wPIDVkCS9ro-0ZltYrs1NUI2q2LLh";
const REDIRECT_URI: &str = "http://127.0.0.1:5000/callback/";
const PORT: u16 = 5000;

pub struct DiscordAuth;

impl DiscordAuth {
    pub fn new() -> Result<Self, LauncherError> {
        let addr = format!("127.0.0.1:{}", PORT);
        let _server = Server::http(&addr)
            .map_err(|e| LauncherError::Auth(format!("Port {} is already in use: {}", PORT, e)))?;
        Ok(Self)
    }

    pub fn authorize_url() -> String {
        format!(
            "https://discord.com/api/oauth2/authorize?client_id={}&redirect_uri={}&response_type=code&scope=identify",
            CLIENT_ID,
            urlencoding::encode(REDIRECT_URI),
        )
    }

    pub fn wait_for_callback() -> Result<String, LauncherError> {
        let addr = format!("127.0.0.1:{}", PORT);
        let server =
            Server::http(&addr).map_err(|e| LauncherError::Auth(e.to_string()))?;

        let start = std::time::Instant::now();
        let total_timeout = std::time::Duration::from_secs(120);

        loop {
            let remaining = total_timeout
                .checked_sub(start.elapsed())
                .unwrap_or_default();
            if remaining.is_zero() {
                return Err(LauncherError::Auth(
                    "Timeout waiting for callback".into(),
                ));
            }

            match server.recv_timeout(std::time::Duration::from_secs(60)) {
                Ok(Some(request)) => {
                    let url = request.url();
                    if url.starts_with("/callback") {
                        let code = url
                            .split("code=")
                            .nth(1)
                            .and_then(|s| s.split('&').next())
                            .ok_or_else(|| LauncherError::Auth("No code in callback".into()))?
                            .to_string();

                        let response_body = "You can close this window.";
                        let response = tiny_http::Response::from_string(response_body).with_header(
                            tiny_http::Header::from_bytes(
                                &b"Content-Type"[..],
                                &b"text/html"[..],
                            )
                            .unwrap(),
                        );
                        let _ = request.respond(response);

                        return Ok(code);
                    }
                }
                Ok(None) => {
                    return Err(LauncherError::Auth(
                        "Timeout waiting for callback".into(),
                    ));
                }
                Err(e) => {
                    return Err(LauncherError::Auth(e.to_string()));
                }
            }
        }
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

use serde::Serialize;

#[derive(Debug, Serialize)]
pub enum LauncherError {
    #[serde(rename = "config")]
    Config(String),
    #[serde(rename = "game_not_found")]
    GameNotFound,
    #[serde(rename = "install_failed")]
    InstallFailed(String),
    #[serde(rename = "ipc")]
    Ipc(String),
    #[serde(rename = "lobby")]
    Lobby(String),
    #[serde(rename = "auth")]
    Auth(String),
    #[serde(rename = "network")]
    Network(String),
    #[serde(rename = "filesystem")]
    Filesystem(String),
    #[serde(rename = "not_installed")]
    NotInstalled,
}

impl std::fmt::Display for LauncherError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Config(e) => write!(f, "Config error: {}", e),
            Self::GameNotFound => write!(f, "Among Us not found"),
            Self::InstallFailed(e) => write!(f, "Install failed: {}", e),
            Self::Ipc(e) => write!(f, "IPC error: {}", e),
            Self::Lobby(e) => write!(f, "Lobby error: {}", e),
            Self::Auth(e) => write!(f, "Auth error: {}", e),
            Self::Network(e) => write!(f, "Network error: {}", e),
            Self::Filesystem(e) => write!(f, "Filesystem error: {}", e),
            Self::NotInstalled => write!(f, "Game not installed"),
        }
    }
}

impl std::error::Error for LauncherError {}

impl From<anyhow::Error> for LauncherError {
    fn from(e: anyhow::Error) -> Self {
        Self::Config(e.to_string())
    }
}

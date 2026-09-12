use crate::error::LauncherError;

fn client() -> reqwest::Client {
    reqwest::Client::builder()
        .user_agent("among-launcher")
        .build()
        .unwrap_or_else(|_| reqwest::Client::new())
}

/// Returns the full JSON of the newest release whose tag starts with "mod/".
/// GitHub returns releases newest-first, so `find` gets the newest `mod/` release.
/// `User-Agent` is required by the GitHub API.
pub async fn latest_mod_release() -> Result<serde_json::Value, LauncherError> {
    let client = client();
    let releases: Vec<serde_json::Value> = client
        .get("https://api.github.com/repos/FirethCrafts/Among-Launcher/releases?per_page=30")
        .header("Accept", "application/vnd.github.v3+json")
        .send()
        .await
        .map_err(|e| LauncherError::Network(e.to_string()))?
        .error_for_status()
        .map_err(|e| LauncherError::Network(e.to_string()))?
        .json()
        .await
        .map_err(|e| LauncherError::Network(e.to_string()))?;
    releases
        .into_iter()
        .find(|r| {
            r["tag_name"]
                .as_str()
                .map(|t| t.starts_with("mod/"))
                .unwrap_or(false)
        })
        .ok_or_else(|| LauncherError::Network("No mod release found".into()))
}

/// Returns the browser_download_url of `asset_name` from the newest `mod/` release.
pub async fn latest_mod_asset_url(asset_name: &str) -> Result<String, LauncherError> {
    let release = latest_mod_release().await?;
    release["assets"]
        .as_array()
        .and_then(|a| {
            a.iter()
                .find(|x| x["name"] == asset_name)
        })
        .and_then(|x| x["browser_download_url"].as_str())
        .map(|s| s.to_string())
        .ok_or_else(|| {
            LauncherError::Network(format!(
                "Asset {} not found in latest mod release",
                asset_name
            ))
        })
}

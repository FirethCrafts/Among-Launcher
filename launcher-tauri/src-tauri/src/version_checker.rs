use crate::error::LauncherError;
use tauri::State;

use crate::AppState;

#[derive(serde::Serialize, Clone)]
pub struct UpdateInfo {
    pub current: String,
    pub latest: String,
    pub changelog: String,
    pub download_url: String,
}

#[tauri::command]
pub async fn check_for_among_api_update(
    state: State<'_, AppState>,
) -> Result<Option<UpdateInfo>, LauncherError> {
    let config = state.config.read().await;
    let game_path = &config.modded_install_path;
    let dll_path = std::path::Path::new(game_path)
        .join("BepInEx")
        .join("Plugins")
        .join("AmongApi.dll");

    let installed_version = if dll_path.exists() {
        extract_file_version(&dll_path).unwrap_or_else(|| "0.0.0".to_string())
    } else {
        return Ok(None);
    };

    let release: serde_json::Value = crate::github::latest_mod_release().await?;

    let tag = release["tag_name"].as_str().unwrap_or("v0.0.0");
    let latest_version = tag
        .strip_prefix("mod/")
        .unwrap_or(tag)
        .trim_start_matches('v');
    let changelog = release["body"].as_str().unwrap_or("");

    if installed_version != latest_version {
        Ok(Some(UpdateInfo {
            current: installed_version,
            latest: latest_version.to_string(),
            changelog: changelog.to_string(),
            download_url: release["assets"]
                .as_array()
                .and_then(|a| {
                    a.iter()
                        .find(|x| x["name"] == "AmongApi.dll")
                        .or_else(|| a.first())
                })
                .and_then(|a| a["browser_download_url"].as_str())
                .unwrap_or("")
                .to_string(),
        }))
    } else {
        Ok(None)
    }
}

fn extract_file_version(dll_path: &std::path::Path) -> Option<String> {
    let data = std::fs::read(dll_path).ok()?;

    // VS_FIXEDFILEINFO signature: 0xFEEF04BD
    let sig_bytes = 0xFEEF04BDu32.to_le_bytes();
    let mut offset = 0;
    while offset + 52 <= data.len() {
        if data[offset..offset + 4] == sig_bytes {
            let major = u32::from_le_bytes(data[offset + 8..offset + 12].try_into().ok()?);
            let minor = u32::from_le_bytes(data[offset + 12..offset + 16].try_into().ok()?);
            let build = u32::from_le_bytes(data[offset + 16..offset + 20].try_into().ok()?);
            return Some(format!("{}.{}.{}", major, minor, build));
        }
        offset += 4;
    }
    None
}

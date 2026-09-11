use crate::config::ModEntry;
use crate::error::LauncherError;
use sha2::{Digest, Sha256};
use std::path::Path;
use tauri::{AppHandle, Emitter};

pub async fn compute_hash(path: &Path) -> Result<String, LauncherError> {
    let bytes = std::fs::read(path).map_err(|e| LauncherError::Filesystem(e.to_string()))?;
    let mut hasher = Sha256::new();
    hasher.update(&bytes);
    Ok(format!("{:x}", hasher.finalize()))
}

pub async fn quarantine_mod(path: &Path) -> Result<(), LauncherError> {
    let disabled_dir = path
        .parent()
        .ok_or_else(|| LauncherError::Filesystem("No parent dir".into()))?
        .join(".disabled");
    std::fs::create_dir_all(&disabled_dir)
        .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
    let filename = path
        .file_name()
        .ok_or_else(|| LauncherError::Filesystem("No filename".into()))?;
    std::fs::rename(path, disabled_dir.join(filename))
        .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
    Ok(())
}

pub async fn download_mod(
    url: &str,
    dest: &Path,
    expected_hash: Option<&str>,
) -> Result<(), LauncherError> {
    let client = reqwest::Client::new();
    let resp = client
        .get(url)
        .send()
        .await
        .map_err(|e| LauncherError::Network(e.to_string()))?;
    let bytes = resp
        .bytes()
        .await
        .map_err(|e| LauncherError::Network(e.to_string()))?;

    if let Some(expected) = expected_hash {
        let mut hasher = Sha256::new();
        hasher.update(&bytes);
        let actual = format!("{:x}", hasher.finalize());
        if actual != expected {
            return Err(LauncherError::InstallFailed(format!(
                "Hash mismatch: expected {}, got {}",
                expected, actual
            )));
        }
    }

    let tmp_path = dest.with_extension("tmp");
    std::fs::write(&tmp_path, &bytes)
        .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
    std::fs::rename(&tmp_path, dest)
        .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
    Ok(())
}

pub async fn sync_mods(
    game_path: &Path,
    required_mods: &[ModEntry],
    app: &AppHandle,
) -> Result<(), LauncherError> {
    let plugins_dir = game_path.join("BepInEx").join("Plugins");
    if !plugins_dir.exists() {
        std::fs::create_dir_all(&plugins_dir)
            .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
    }

    let installed_mods = get_installed_mods(&plugins_dir).await?;

    let required_names: Vec<&str> = required_mods.iter().map(|m| m.name.as_str()).collect();

    let _ = app.emit(
        "install-progress",
        serde_json::json!({ "stage": "quarantine", "progress": 0, "total": installed_mods.len() as u64 }),
    );

    let mut quarantined = 0u64;
    for (name, path) in &installed_mods {
        if !required_names.contains(&name.as_str()) {
            quarantine_mod(path).await?;
            quarantined += 1;
            let _ = app.emit(
                "install-progress",
                serde_json::json!({
                    "stage": "quarantine",
                    "progress": quarantined,
                    "total": installed_mods.len() as u64,
                }),
            );
        }
    }

    let _ = app.emit(
        "install-progress",
        serde_json::json!({ "stage": "download", "progress": 0, "total": required_mods.len() as u64 }),
    );

    let mut downloaded = 0u64;
    for required in required_mods {
        let already_installed = installed_mods.iter().any(|(name, _)| name == &required.name);
        if !already_installed {
            if let Some(ref url) = required.download_url {
                let filename = format!("{}.dll", required.name);
                let dest = plugins_dir.join(&filename);
                download_mod(url, &dest, required.file_hash.as_deref()).await?;
                downloaded += 1;
                let _ = app.emit(
                    "install-progress",
                    serde_json::json!({
                        "stage": "download",
                        "progress": downloaded,
                        "total": required_mods.len() as u64,
                    }),
                );
            }
        }
    }

    Ok(())
}

async fn get_installed_mods(
    plugins_dir: &Path,
) -> Result<Vec<(String, std::path::PathBuf)>, LauncherError> {
    let mut mods = Vec::new();
    if !plugins_dir.exists() {
        return Ok(mods);
    }

    let entries = std::fs::read_dir(plugins_dir)
        .map_err(|e| LauncherError::Filesystem(e.to_string()))?;

    for entry in entries.flatten() {
        let path = entry.path();
        if path.extension().map(|e| e == "dll").unwrap_or(false) {
            let name = path
                .file_stem()
                .and_then(|s| s.to_str())
                .unwrap_or("Unknown")
                .to_string();
            mods.push((name, path));
        }
    }

    Ok(mods)
}
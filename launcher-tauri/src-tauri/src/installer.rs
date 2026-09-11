use crate::error::LauncherError;
use futures_util::StreamExt;
use std::path::Path;
use tauri::{AppHandle, Emitter};

pub async fn copy_game(source: &str, dest: &str) -> Result<(), LauncherError> {
    let source_path = Path::new(source);
    let dest_path = Path::new(dest);

    let source_canonical =
        std::fs::canonicalize(source_path).map_err(|e| LauncherError::Filesystem(e.to_string()))?;
    let dest_canonical = std::fs::canonicalize(dest_path)
        .or_else(|_| {
            std::fs::create_dir_all(dest_path)
                .and_then(|_| std::fs::canonicalize(dest_path))
        })
        .map_err(|e| LauncherError::Filesystem(e.to_string()))?;

    if dest_canonical.starts_with(&source_canonical) {
        return Err(LauncherError::Filesystem(
            "Cannot copy game into its own directory".into(),
        ));
    }

    for entry in walkdir::WalkDir::new(source_path) {
        let entry = entry.map_err(|e| LauncherError::Filesystem(e.to_string()))?;
        let rel = entry
            .path()
            .strip_prefix(source_path)
            .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
        let target = dest_path.join(rel);

        if entry.file_type().is_dir() {
            if rel.file_name() == Some(std::ffi::OsStr::new("BepInEx")) {
                continue;
            }
            std::fs::create_dir_all(&target)
                .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
        } else {
            if rel.extension() == Some(std::ffi::OsStr::new("pdb")) {
                continue;
            }
            std::fs::copy(entry.path(), &target)
                .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
        }
    }
    Ok(())
}

pub async fn download_bepinex(
    dest: &str,
    storefront: &str,
    app: &AppHandle,
) -> Result<(), LauncherError> {
    let zip_name = match storefront {
        "steam" => "BepInEx.zip",
        _ => "bepinex-ms-epic.zip",
    };
    let url = format!(
        "https://github.com/FirethCrafts/Among-Launcher/releases/latest/download/{}",
        zip_name
    );
    let zip_path = std::path::Path::new(dest).join(zip_name);

    let client = reqwest::Client::new();
    let resp = client
        .get(&url)
        .send()
        .await
        .map_err(|e| LauncherError::Network(e.to_string()))?;
    let total = resp.content_length().unwrap_or(0);
    let mut bytes = Vec::new();
    let mut downloaded = 0u64;

    let mut stream = resp.bytes_stream();
    while let Some(chunk) = stream.next().await {
        let chunk = chunk.map_err(|e| LauncherError::Network(e.to_string()))?;
        bytes.extend_from_slice(&chunk);
        downloaded += chunk.len() as u64;
        let _ = app.emit(
            "install-progress",
            serde_json::json!({
                "stage": "bepinex",
                "downloaded": downloaded,
                "total": total,
            }),
        );
    }

    std::fs::write(&zip_path, &bytes).map_err(|e| LauncherError::Filesystem(e.to_string()))?;

    let dest_path = std::path::Path::new(dest);
    let file =
        std::fs::File::open(&zip_path).map_err(|e| LauncherError::Filesystem(e.to_string()))?;
    let mut archive =
        zip::ZipArchive::new(file).map_err(|e| LauncherError::InstallFailed(e.to_string()))?;
    archive
        .extract(dest_path)
        .map_err(|e| LauncherError::InstallFailed(e.to_string()))?;

    let _ = std::fs::remove_file(&zip_path);
    Ok(())
}

pub async fn download_among_api(dest: &str, app: &AppHandle) -> Result<(), LauncherError> {
    let url =
        "https://github.com/FirethCrafts/Among-Launcher/releases/latest/download/AmongApi.dll";
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

    let plugins_dir = std::path::Path::new(dest).join("BepInEx/Plugins");
    std::fs::create_dir_all(&plugins_dir)
        .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
    std::fs::write(plugins_dir.join("AmongApi.dll"), &bytes)
        .map_err(|e| LauncherError::Filesystem(e.to_string()))?;

    let _ = app.emit(
        "install-progress",
        serde_json::json!({
            "stage": "among_api",
            "downloaded": bytes.len(),
            "total": bytes.len(),
        }),
    );
    Ok(())
}

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
    // Resolve through effective_modded_path (same as other commands) so an
    // empty `modded_install_path` never probes a CWD-relative path and
    // silently reports "no update".
    let game_path = {
        let config = state.config.read().await;
        config.effective_modded_path()
    };
    let dll_path = std::path::Path::new(&game_path)
        .join("BepInEx")
        .join("Plugins")
        .join("AmongApi.dll");

    let installed_version = if dll_path.exists() {
        extract_file_version(&dll_path).unwrap_or_else(|| "0.0.0".to_string())
    } else {
        // DLL not installed — legitimately no update to offer.
        return Ok(None);
    };

    let release: serde_json::Value = crate::github::latest_mod_release().await?;

    let tag = release["tag_name"].as_str().unwrap_or("v0.0.0");
    let latest_version = tag
        .strip_prefix("mod/")
        .unwrap_or(tag)
        .trim_start_matches('v');
    let changelog = release["body"].as_str().unwrap_or("");

    if versions_differ(&installed_version, latest_version) {
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

/// Parse a dotted version into numeric components.
/// Returns `None` when any component is empty or not a `u32`.
fn parse_version_tuple(version: &str) -> Option<Vec<u32>> {
    let parts = version
        .split('.')
        .map(|part| part.trim().parse::<u32>().ok())
        .collect::<Option<Vec<u32>>>()?;
    if parts.is_empty() {
        None
    } else {
        Some(parts)
    }
}

/// Compare two dotted versions component-wise, treating missing components
/// as 0 so `1.0.16` equals `1.0.16.0`. Returns `None` when either version
/// is unparsable (caller falls back to string comparison).
pub(crate) fn compare_versions(a: &str, b: &str) -> Option<std::cmp::Ordering> {
    let a = parse_version_tuple(a)?;
    let b = parse_version_tuple(b)?;
    let len = a.len().max(b.len());
    for i in 0..len {
        let ord = a
            .get(i)
            .copied()
            .unwrap_or(0)
            .cmp(&b.get(i).copied().unwrap_or(0));
        if ord != std::cmp::Ordering::Equal {
            return Some(ord);
        }
    }
    Some(std::cmp::Ordering::Equal)
}

/// True when the installed version differs from the latest release.
/// Uses numeric tuple comparison (so `1.0.16` vs `1.0.16.0` compare equal)
/// and falls back to plain string inequality when either side is
/// unparsable — never panics, never silently reports "no update ever".
fn versions_differ(installed: &str, latest: &str) -> bool {
    match compare_versions(installed, latest) {
        Some(ordering) => ordering != std::cmp::Ordering::Equal,
        None => installed != latest,
    }
}

pub(crate) fn extract_file_version(dll_path: &std::path::Path) -> Option<String> {
    let data = std::fs::read(dll_path).ok()?;
    parse_file_version(&data)
}

/// Scan `data` for a VS_FIXEDFILEINFO signature (0xFEEF04BD) and render the
/// file-version fields as a 3-part string.
fn parse_file_version(data: &[u8]) -> Option<String> {
    // VS_FIXEDFILEINFO DWORD layout relative to the signature at `offset`:
    //   +0  dwSignature        (0xFEEF04BD)
    //   +4  dwStrucVersion
    //   +8  dwFileVersionMS    (major << 16 | minor)
    //   +12 dwFileVersionLS    (build << 16 | revision)
    //   +16 dwProductVersionMS (product field — must NOT be read)
    //   +20 dwProductVersionLS
    let sig_bytes = 0xFEEF04BDu32.to_le_bytes();
    let mut offset = 0;
    while offset + 52 <= data.len() {
        if data[offset..offset + 4] == sig_bytes {
            let ms = u32::from_le_bytes(data[offset + 8..offset + 12].try_into().ok()?);
            let ls = u32::from_le_bytes(data[offset + 12..offset + 16].try_into().ok()?);
            let major = ms >> 16;
            let minor = ms & 0xFFFF;
            let build = ls >> 16;
            return Some(format!("{}.{}.{}", major, minor, build));
        }
        offset += 4;
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cmp::Ordering;

    /// Synthetic VS_VERSIONINFO-shaped blob: a 16-byte, 4-aligned header
    /// stand-in followed by a full 52-byte VS_FIXEDFILEINFO root at the
    /// offset the production scanner's `offset` math expects.
    fn synthetic_version_blob(file_ms: u32, file_ls: u32, prod_ms: u32, prod_ls: u32) -> Vec<u8> {
        let mut data = vec![0u8; 16]; // VS_VERSIONINFO header stand-in
        data.extend_from_slice(&0xFEEF04BDu32.to_le_bytes()); // +0  dwSignature
        data.extend_from_slice(&0x00010000u32.to_le_bytes()); // +4  dwStrucVersion
        data.extend_from_slice(&file_ms.to_le_bytes()); //      +8  dwFileVersionMS
        data.extend_from_slice(&file_ls.to_le_bytes()); //      +12 dwFileVersionLS
        data.extend_from_slice(&prod_ms.to_le_bytes()); //      +16 dwProductVersionMS
        data.extend_from_slice(&prod_ls.to_le_bytes()); //      +20 dwProductVersionLS
        data.extend_from_slice(&[0u8; 28]); //                 +24..+52 FILEFLAGS..FILEDATELS
        data
    }

    #[test]
    fn parse_reads_file_version_not_product_version() {
        // File version 1.0.16; product version 1.0.x — the old bug read
        // dwProductVersionMS as the low DWORD and rendered "1.0.1".
        let blob = synthetic_version_blob(
            (1 << 16) | 0,  // dwFileVersionMS: 1.0
            (16 << 16) | 0, // dwFileVersionLS: build 16
            (1 << 16) | 0,  // dwProductVersionMS: 1.0 (old bug → "1.0.1")
            (1 << 16) | 0,  // dwProductVersionLS
        );
        assert_eq!(parse_file_version(&blob), Some("1.0.16".to_string()));
    }

    #[test]
    fn extract_file_version_reads_synthetic_blob_from_disk() {
        let blob = synthetic_version_blob((2 << 16) | 3, (4 << 16) | 0, (9 << 16) | 9, 0);
        let path = std::env::temp_dir().join(format!(
            "among_launcher_version_test_{}.dll",
            std::process::id()
        ));
        std::fs::write(&path, &blob).expect("write synthetic version blob");
        let result = extract_file_version(&path);
        let _ = std::fs::remove_file(&path);
        assert_eq!(result, Some("2.3.4".to_string()));
    }

    #[test]
    fn extract_file_version_missing_file_is_none() {
        let path = std::env::temp_dir().join(format!(
            "among_launcher_version_test_missing_{}.dll",
            std::process::id()
        ));
        let _ = std::fs::remove_file(&path);
        assert_eq!(extract_file_version(&path), None);
    }

    #[test]
    fn compare_equal_versions() {
        assert_eq!(compare_versions("1.0.16", "1.0.16"), Some(Ordering::Equal));
    }

    #[test]
    fn compare_pads_missing_components_with_zero() {
        assert_eq!(compare_versions("1.0.16", "1.0.16.0"), Some(Ordering::Equal));
    }

    #[test]
    fn compare_orders_numerically_not_lexically() {
        assert_eq!(compare_versions("1.0.9", "1.0.16"), Some(Ordering::Less));
        assert_eq!(compare_versions("1.0.16", "1.0.9"), Some(Ordering::Greater));
    }

    #[test]
    fn compare_unparsable_returns_none() {
        assert_eq!(compare_versions("garbage", "1.0.0"), None);
        assert_eq!(compare_versions("1.0.x", "1.0.0"), None);
        assert_eq!(compare_versions("", "1.0.0"), None);
    }

    #[test]
    fn versions_differ_numeric_cases() {
        assert!(versions_differ("1.0.9", "1.0.16"));
        assert!(!versions_differ("1.0.16", "1.0.16"));
        assert!(!versions_differ("1.0.16", "1.0.16.0"));
    }

    #[test]
    fn versions_differ_falls_back_to_string_inequality_on_garbage() {
        assert!(versions_differ("garbage", "1.0.0"));
        assert!(!versions_differ("garbage", "garbage"));
    }
}

use std::cmp::Ordering;
use std::path::{Path, PathBuf};

use tauri::State;

use crate::error::LauncherError;
use crate::AppState;

#[derive(serde::Serialize, Clone)]
pub struct UpdateInfo {
    pub current: String,
    pub latest: String,
    pub changelog: String,
    pub download_url: String,
}

/// Coarse AmongApi installation state surfaced to the frontend (`get_mod_status`)
/// and used to gate `launch_game`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "snake_case")]
pub enum ModStatusKind {
    /// The DLL is absent from the modded install.
    Missing,
    /// The installed DLL is older than the latest `mod/` release.
    Outdated,
    /// The installed DLL is the latest release (or newer).
    Current,
    /// The in-game mod reported a protocol version this launcher does not
    /// expect (learned from the live `game_ready` IPC message).
    Incompatible,
    /// The status could not be determined (offline / no `mod/` release).
    Unknown,
}

/// Snapshot of the installed AmongApi mod. Serialized camelCase to match the
/// frozen frontend contract:
/// `{status, installedVersion, latestVersion, downloadUrl, notes}`.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ModStatus {
    pub status: ModStatusKind,
    pub installed_version: Option<String>,
    pub latest_version: Option<String>,
    pub download_url: Option<String>,
    pub notes: Option<String>,
}

/// The pieces of a `mod/` release the launcher cares about. Parsed from the
/// GitHub release JSON so both the update checker and `get_mod_status` share
/// one implementation.
#[derive(Debug, Clone, PartialEq, Eq)]
struct ModRelease {
    version: String,
    download_url: Option<String>,
    notes: Option<String>,
}

/// Extract version/download/notes from a GitHub release JSON value. Pure so it
/// can be unit-tested without the network.
fn parse_mod_release(release: &serde_json::Value) -> ModRelease {
    let tag = release["tag_name"].as_str().unwrap_or("v0.0.0");
    let version = tag
        .strip_prefix("mod/")
        .unwrap_or(tag)
        .trim_start_matches('v')
        .to_string();

    // Prefer the AmongApi.dll asset; fall back to the first asset.
    let download_url = release["assets"]
        .as_array()
        .and_then(|assets| {
            assets
                .iter()
                .find(|x| x["name"] == "AmongApi.dll")
                .or_else(|| assets.first())
        })
        .and_then(|asset| asset["browser_download_url"].as_str())
        .map(|url| url.to_string());

    let notes = release["body"]
        .as_str()
        .map(str::trim)
        .filter(|body| !body.is_empty())
        .map(|body| body.to_string());

    ModRelease {
        version,
        download_url,
        notes,
    }
}

/// Path to the installed AmongApi DLL under the effective modded install.
fn mod_dll_path(game_path: &str) -> PathBuf {
    Path::new(game_path)
        .join("BepInEx")
        .join("Plugins")
        .join("AmongApi.dll")
}

/// Installed AmongApi FileVersion at `game_path`, or `None` when the DLL is
/// absent. A present DLL whose version cannot be parsed is reported as
/// `"0.0.0"` so it is treated as outdated rather than silently ignored.
pub(crate) fn read_installed_version(game_path: &str) -> Option<String> {
    let dll_path = mod_dll_path(game_path);
    if !dll_path.exists() {
        return None;
    }
    Some(extract_file_version(&dll_path).unwrap_or_else(|| "0.0.0".to_string()))
}

/// Read the cached mod status (`None` until the first `get_mod_status` or a
/// live protocol verdict).
pub(crate) fn cached_mod_status(state: &AppState) -> Option<ModStatus> {
    state.mod_status.lock().ok().and_then(|guard| guard.clone())
}

pub(crate) fn store_mod_status(state: &AppState, status: ModStatus) {
    if let Ok(mut guard) = state.mod_status.lock() {
        *guard = Some(status);
    }
}

/// Record a protocol-mismatch verdict from the live game. Preserves the
/// installed/latest/download fields from the last computed status so the
/// forced-update prompt still has a download URL; falls back to the
/// `installed_version` read from disk when there is no cached status.
pub(crate) fn mark_mod_incompatible(
    state: &AppState,
    installed_version: Option<String>,
    reason: &str,
) {
    if let Ok(mut guard) = state.mod_status.lock() {
        match guard.as_mut() {
            Some(status) => {
                if status.installed_version.is_none() {
                    status.installed_version = installed_version;
                }
                status.status = ModStatusKind::Incompatible;
                status.notes = Some(reason.to_string());
            }
            None => {
                *guard = Some(ModStatus {
                    status: ModStatusKind::Incompatible,
                    installed_version,
                    latest_version: None,
                    download_url: None,
                    notes: Some(reason.to_string()),
                });
            }
        }
    }
}

/// Clear an `incompatible` verdict after the in-game mod reports a protocol
/// version we expect. The protocol verdict is about the *running* game; the
/// version verdict is re-derived from the cached versions so an outdated
/// install still gates the next launch.
pub(crate) fn clear_mod_incompatible(state: &AppState) {
    if let Ok(mut guard) = state.mod_status.lock() {
        if let Some(status) = guard.as_mut() {
            if status.status != ModStatusKind::Incompatible {
                return;
            }
            let outdated = match (
                status.installed_version.as_deref(),
                status.latest_version.as_deref(),
            ) {
                (Some(installed), Some(latest)) => is_outdated(installed, latest),
                // Unknown versions: the protocol contract is satisfied, so
                // treat as current. A later `get_mod_status` re-derives the
                // version verdict anyway.
                _ => false,
            };
            status.status = if outdated {
                ModStatusKind::Outdated
            } else {
                ModStatusKind::Current
            };
            status.notes = None;
        }
    }
}

/// Derive the status from the installed (on-disk) version and the latest
/// release version. `None` for `installed` means the DLL is absent; `None`
/// for `latest` means the release could not be resolved (offline / no
/// `mod/` release) → `unknown`, never an error.
pub(crate) fn derive_mod_status(
    installed: Option<&str>,
    latest: Option<&str>,
) -> ModStatusKind {
    match (installed, latest) {
        (None, _) => ModStatusKind::Missing,
        (Some(_), None) => ModStatusKind::Unknown,
        (Some(installed), Some(latest)) => {
            if is_outdated(installed, latest) {
                ModStatusKind::Outdated
            } else {
                // installed >= latest (including a newer local build) is
                // current — never prompt a downgrade.
                ModStatusKind::Current
            }
        }
    }
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

    let Some(installed_version) = read_installed_version(&game_path) else {
        // DLL not installed — legitimately no update to offer.
        return Ok(None);
    };

    // Network/API failures stay `Err` here (unlike `get_mod_status`) so the
    // frontend can still distinguish "check failed" from "up to date".
    let release: serde_json::Value = crate::github::latest_mod_release().await?;
    let info = parse_mod_release(&release);

    if derive_mod_status(Some(&installed_version), Some(&info.version))
        == ModStatusKind::Outdated
    {
        Ok(Some(UpdateInfo {
            current: installed_version,
            latest: info.version,
            changelog: info.notes.unwrap_or_default(),
            download_url: info.download_url.unwrap_or_default(),
        }))
    } else {
        Ok(None)
    }
}

/// Compute and cache the current AmongApi status. Never returns `Err` for the
/// expected failure modes: a missing DLL → `missing`; an unreachable GitHub /
/// no `mod/` release → `unknown` (the frontend must not block on `unknown`).
#[tauri::command]
pub async fn get_mod_status(state: State<'_, AppState>) -> Result<ModStatus, LauncherError> {
    let game_path = {
        let config = state.config.read().await;
        config.effective_modded_path()
    };

    let installed = read_installed_version(&game_path);

    // DLL absent → missing; no network call needed.
    if installed.is_none() {
        let status = ModStatus {
            status: ModStatusKind::Missing,
            installed_version: None,
            latest_version: None,
            download_url: None,
            notes: None,
        };
        store_mod_status(&state, status.clone());
        return Ok(status);
    }

    let release = match crate::github::latest_mod_release().await {
        Ok(value) => Some(parse_mod_release(&value)),
        // Network failure OR no `mod/` release → unknown, never an error.
        Err(_) => None,
    };

    let kind = derive_mod_status(
        installed.as_deref(),
        release.as_ref().map(|r| r.version.as_str()),
    );
    let mut status = ModStatus {
        status: kind,
        installed_version: installed,
        latest_version: release.as_ref().map(|r| r.version.clone()),
        download_url: release.as_ref().and_then(|r| r.download_url.clone()),
        notes: release.as_ref().and_then(|r| r.notes.clone()),
    };

    // A live protocol verdict wins over the version-derived status while the
    // same on-disk mod is still installed; once the DLL changes (i.e. it was
    // updated) the stale verdict is dropped and the fresh status is reported.
    if let Some(cached) = cached_mod_status(&state) {
        if cached.status == ModStatusKind::Incompatible
            && cached.installed_version.is_some()
            && cached.installed_version == status.installed_version
        {
            status.status = ModStatusKind::Incompatible;
            status.notes = cached.notes.or(status.notes);
        }
    }

    store_mod_status(&state, status.clone());
    Ok(status)
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
pub(crate) fn compare_versions(a: &str, b: &str) -> Option<Ordering> {
    let a = parse_version_tuple(a)?;
    let b = parse_version_tuple(b)?;
    let len = a.len().max(b.len());
    for i in 0..len {
        let ord = a
            .get(i)
            .copied()
            .unwrap_or(0)
            .cmp(&b.get(i).copied().unwrap_or(0));
        if ord != Ordering::Equal {
            return Some(ord);
        }
    }
    Some(Ordering::Equal)
}

/// True when `installed` is strictly older than `latest`. Uses numeric tuple
/// comparison (so `1.0.16` equals `1.0.16.0`) and falls back to plain string
/// inequality when either side is unparsable — never panics, never silently
/// treats a different version as current.
pub(crate) fn is_outdated(installed: &str, latest: &str) -> bool {
    match compare_versions(installed, latest) {
        Some(Ordering::Less) => true,
        Some(_) => false,
        None => installed != latest,
    }
}

pub(crate) fn extract_file_version(dll_path: &Path) -> Option<String> {
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
    fn is_outdated_numeric_cases() {
        assert!(is_outdated("1.0.9", "1.0.16"));
        assert!(!is_outdated("1.0.16", "1.0.16"));
        assert!(!is_outdated("1.0.16", "1.0.16.0"));
        // A newer installed build is NOT outdated (no downgrade prompt).
        assert!(!is_outdated("1.0.17", "1.0.16"));
    }

    #[test]
    fn is_outdated_falls_back_to_string_inequality_on_garbage() {
        assert!(is_outdated("garbage", "1.0.0"));
        assert!(!is_outdated("garbage", "garbage"));
    }

    // ---- status derivation ----

    #[test]
    fn derive_missing_when_dll_absent() {
        assert_eq!(derive_mod_status(None, Some("1.0.17")), ModStatusKind::Missing);
        // Missing wins even when the release is unknown.
        assert_eq!(derive_mod_status(None, None), ModStatusKind::Missing);
    }

    #[test]
    fn derive_unknown_when_release_unavailable() {
        assert_eq!(derive_mod_status(Some("1.0.16"), None), ModStatusKind::Unknown);
    }

    #[test]
    fn derive_outdated_when_installed_older() {
        assert_eq!(
            derive_mod_status(Some("1.0.9"), Some("1.0.16")),
            ModStatusKind::Outdated
        );
    }

    #[test]
    fn derive_current_when_equal() {
        assert_eq!(
            derive_mod_status(Some("1.0.16"), Some("1.0.16")),
            ModStatusKind::Current
        );
        // Trailing-zero padding still compares equal.
        assert_eq!(
            derive_mod_status(Some("1.0.16"), Some("1.0.16.0")),
            ModStatusKind::Current
        );
    }

    #[test]
    fn derive_current_when_installed_newer() {
        // The bug this guards: a locally newer build must never be flagged as
        // needing a (downgrade) update.
        assert_eq!(
            derive_mod_status(Some("1.0.18"), Some("1.0.17")),
            ModStatusKind::Current
        );
    }

    #[test]
    fn derive_unparsable_falls_back_to_string_compare() {
        // Same garbage strings → treated as current; different → outdated.
        assert_eq!(
            derive_mod_status(Some("nightly"), Some("nightly")),
            ModStatusKind::Current
        );
        assert_eq!(
            derive_mod_status(Some("nightly"), Some("1.0.0")),
            ModStatusKind::Outdated
        );
    }

    // ---- release JSON parsing ----

    #[test]
    fn parse_mod_release_prefers_among_api_asset() {
        let release = serde_json::json!({
            "tag_name": "mod/v1.0.17",
            "body": "  Fixes things.  ",
            "assets": [
                {"name": "other.dll", "browser_download_url": "https://x/other.dll"},
                {"name": "AmongApi.dll", "browser_download_url": "https://x/AmongApi.dll"}
            ]
        });
        assert_eq!(
            parse_mod_release(&release),
            ModRelease {
                version: "1.0.17".to_string(),
                download_url: Some("https://x/AmongApi.dll".to_string()),
                notes: Some("Fixes things.".to_string()),
            }
        );
    }

    #[test]
    fn parse_mod_release_falls_back_to_first_asset() {
        let release = serde_json::json!({
            "tag_name": "mod/v1.0.17",
            "assets": [
                {"name": "renamed.dll", "browser_download_url": "https://x/renamed.dll"}
            ]
        });
        assert_eq!(
            parse_mod_release(&release).download_url,
            Some("https://x/renamed.dll".to_string())
        );
    }

    #[test]
    fn parse_mod_release_notes_none_when_empty_or_missing() {
        assert_eq!(
            parse_mod_release(&serde_json::json!({"tag_name": "mod/v1.0.17", "body": "   "})).notes,
            None
        );
        assert_eq!(
            parse_mod_release(&serde_json::json!({"tag_name": "mod/v1.0.17"})).notes,
            None
        );
        assert_eq!(
            parse_mod_release(&serde_json::json!({"tag_name": "mod/v1.0.17"})).download_url,
            None
        );
    }

    #[test]
    fn parse_mod_release_handles_bare_tag() {
        // Defensive: a tag without the `mod/` prefix (and without `v`) still
        // parses rather than panicking.
        let release = serde_json::json!({"tag_name": "1.0.17"});
        assert_eq!(parse_mod_release(&release).version, "1.0.17");
    }

    // ---- frozen JSON contract ----

    #[test]
    fn mod_status_serializes_camel_case_contract() {
        let status = ModStatus {
            status: ModStatusKind::Outdated,
            installed_version: Some("1.0.16".to_string()),
            latest_version: Some("1.0.17".to_string()),
            download_url: Some("https://x/AmongApi.dll".to_string()),
            notes: Some("notes".to_string()),
        };
        assert_eq!(
            serde_json::to_value(&status).unwrap(),
            serde_json::json!({
                "status": "outdated",
                "installedVersion": "1.0.16",
                "latestVersion": "1.0.17",
                "downloadUrl": "https://x/AmongApi.dll",
                "notes": "notes",
            })
        );
    }

    #[test]
    fn mod_status_missing_serializes_null_fields() {
        let status = ModStatus {
            status: ModStatusKind::Missing,
            installed_version: None,
            latest_version: None,
            download_url: None,
            notes: None,
        };
        assert_eq!(
            serde_json::to_value(&status).unwrap(),
            serde_json::json!({
                "status": "missing",
                "installedVersion": null,
                "latestVersion": null,
                "downloadUrl": null,
                "notes": null,
            })
        );
    }

    #[test]
    fn mod_status_kind_serializes_all_snake_case_codes() {
        let cases = [
            (ModStatusKind::Missing, "missing"),
            (ModStatusKind::Outdated, "outdated"),
            (ModStatusKind::Current, "current"),
            (ModStatusKind::Incompatible, "incompatible"),
            (ModStatusKind::Unknown, "unknown"),
        ];
        for (kind, expected) in cases {
            assert_eq!(
                serde_json::to_value(kind).unwrap(),
                serde_json::Value::String(expected.to_string()),
                "kind {:?}",
                kind
            );
        }
    }

}

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

/// Returns the JSON of the newest release whose tag starts with "launcher/".
///
/// Uses the list endpoint (`per_page=30`) because launcher releases and mod
/// releases interleave and mod releases are published with `make_latest:
/// false`, which makes the `/releases/latest` endpoint unusable.
///
/// Error contract: network failure, HTTP error status, or an unparsable
/// response body is always `Err` — NEVER a silent `Ok(None)`. `Ok(None)`
/// means the API responded successfully and genuinely contains no launcher
/// release at all (true first release).
pub async fn latest_launcher_release() -> Result<Option<serde_json::Value>, LauncherError> {
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
    // GitHub returns releases newest-first, so `find` gets the newest
    // launcher release.
    Ok(releases
        .into_iter()
        .find(|r| {
            r["tag_name"]
                .as_str()
                .map(|t| t.starts_with("launcher/v"))
                .unwrap_or(false)
        }))
}

/// Parses a launcher release tag into its bare version string.
/// `launcher/v1.2.10` → `Some("1.2.10")` (also accepts an uppercase `V`).
/// Returns `None` unless the tag starts with `launcher/` AND the remainder
/// starts with `v`/`V` and is non-empty — `strip_prefix` chains are required
/// because the old `TrimStart('v')` pattern cannot handle the `launcher/`
/// prefix.
pub(crate) fn parse_launcher_tag_version(tag: &str) -> Option<&str> {
    let rest = tag.strip_prefix("launcher/")?;
    let rest = rest.strip_prefix('v').or_else(|| rest.strip_prefix('V'))?;
    if rest.is_empty() {
        None
    } else {
        Some(rest)
    }
}

/// `browser_download_url` of the first asset whose name ends in `.exe`
/// (real asset: `Among.Launcher_1.2.10_x64-setup.exe`), or `None` when the
/// release has no `.exe` asset.
pub(crate) fn launcher_exe_asset_url(release: &serde_json::Value) -> Option<String> {
    let assets = release["assets"].as_array()?;
    let exe = assets.iter().find(|a| {
        a["name"]
            .as_str()
            .map(|n| n.ends_with(".exe"))
            .unwrap_or(false)
    })?;
    exe["browser_download_url"].as_str().map(|s| s.to_string())
}

/// Release notes for the launcher update modal: `None` when the body is
/// null/empty/whitespace, otherwise the body trimmed and truncated to at
/// most 4000 **chars** (char-boundary safe, never splits UTF-8).
pub(crate) fn launcher_release_notes(release: &serde_json::Value) -> Option<String> {
    let body = release["body"].as_str().unwrap_or("").trim();
    if body.is_empty() {
        return None;
    }
    let truncated: String = body.chars().take(4000).collect();
    Some(truncated)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cmp::Ordering;

    // --- launcher tag parsing ---

    #[test]
    fn parse_launcher_tag_strips_prefix_and_v() {
        assert_eq!(parse_launcher_tag_version("launcher/v1.2.10"), Some("1.2.10"));
        assert_eq!(parse_launcher_tag_version("launcher/V2.0.1"), Some("2.0.1"));
        assert_eq!(parse_launcher_tag_version("launcher/v0.0.1"), Some("0.0.1"));
    }

    #[test]
    fn parse_launcher_tag_rejects_non_launcher_tags() {
        // The old TrimStart('v') pattern could not handle the prefix; these
        // must all be rejected rather than mis-parsed.
        assert_eq!(parse_launcher_tag_version("mod/v1.2.10"), None);
        assert_eq!(parse_launcher_tag_version("v1.2.10"), None);
        assert_eq!(parse_launcher_tag_version("1.2.10"), None);
        assert_eq!(parse_launcher_tag_version("launcher/1.2.10"), None); // missing v
        assert_eq!(parse_launcher_tag_version("launcher/v"), None); // empty version
        assert_eq!(parse_launcher_tag_version(""), None);
    }

    // --- version comparison vs current app version (reuses Rust-B1 helpers) ---

    #[test]
    fn launcher_version_tuple_vs_current() {
        use crate::version_checker::compare_versions;
        // newer release than installed → update available
        assert_eq!(compare_versions("1.2.11", "1.2.10"), Some(Ordering::Greater));
        // equal → up to date
        assert_eq!(compare_versions("1.2.10", "1.2.10"), Some(Ordering::Equal));
        // older release than installed (rollback) → up to date, no downgrade prompt
        assert_eq!(compare_versions("1.2.9", "1.2.10"), Some(Ordering::Less));
        // numeric, not lexical: 1.2.10 > 1.2.9 despite string ordering
        assert_eq!(compare_versions("1.2.10", "1.2.9"), Some(Ordering::Greater));
    }

    // --- .exe asset selection ---

    #[test]
    fn exe_asset_selection_picks_first_exe() {
        let release = serde_json::json!({
            "tag_name": "launcher/v1.2.10",
            "assets": [
                {"name": "AmongApi.dll", "browser_download_url": "https://x/AmongApi.dll"},
                {"name": "Among.Launcher_1.2.10_x64-setup.exe", "browser_download_url": "https://x/setup.exe"},
                {"name": "other.exe", "browser_download_url": "https://x/other.exe"}
            ]
        });
        assert_eq!(
            launcher_exe_asset_url(&release),
            Some("https://x/setup.exe".to_string())
        );
    }

    #[test]
    fn exe_asset_selection_returns_none_without_exe() {
        let release = serde_json::json!({
            "tag_name": "launcher/v1.2.10",
            "assets": [
                {"name": "AmongApi.dll", "browser_download_url": "https://x/AmongApi.dll"}
            ]
        });
        assert_eq!(launcher_exe_asset_url(&release), None);

        let no_assets = serde_json::json!({"tag_name": "launcher/v1.2.10"});
        assert_eq!(launcher_exe_asset_url(&no_assets), None);

        // ".exea" must not match ends_with(".exe")
        let near_miss = serde_json::json!({
            "assets": [
                {"name": "installer.exea", "browser_download_url": "https://x/near"}
            ]
        });
        assert_eq!(launcher_exe_asset_url(&near_miss), None);
    }

    // --- notes truncation ---

    #[test]
    fn notes_none_when_null_empty_or_whitespace() {
        assert_eq!(launcher_release_notes(&serde_json::json!({"body": null})), None);
        assert_eq!(launcher_release_notes(&serde_json::json!({"body": ""})), None);
        assert_eq!(launcher_release_notes(&serde_json::json!({"body": "  \n\t "})), None);
        assert_eq!(launcher_release_notes(&serde_json::json!({})), None);
    }

    #[test]
    fn notes_short_body_passes_through_trimmed() {
        let release = serde_json::json!({"body": "  Bug fixes.  "});
        assert_eq!(
            launcher_release_notes(&release),
            Some("Bug fixes.".to_string())
        );
    }

    #[test]
    fn notes_truncated_to_4000_chars() {
        let long_body = "a".repeat(5000);
        let release = serde_json::json!({ "body": long_body });
        let notes = launcher_release_notes(&release).expect("non-empty body yields notes");
        assert_eq!(notes.chars().count(), 4000);

        // exactly 4000 → untouched
        let exact = "b".repeat(4000);
        let release = serde_json::json!({ "body": exact });
        assert_eq!(launcher_release_notes(&release).map(|n| n.chars().count()), Some(4000));

        // multibyte: 4000 chars of a 2-byte char (8000 bytes) must not panic
        // or split a UTF-8 boundary
        let multibody = "é".repeat(5000);
        let release = serde_json::json!({ "body": multibody });
        let notes = launcher_release_notes(&release).expect("multibyte notes");
        assert_eq!(notes.chars().count(), 4000);
        assert_eq!(notes.as_bytes().len(), 8000);
    }
}

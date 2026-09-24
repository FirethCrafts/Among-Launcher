use crate::error::LauncherError;
use futures_util::StreamExt;
use std::path::Path;
use std::time::Duration;
use tauri::{AppHandle, Emitter};

pub async fn copy_game(source: &str, dest: &str, app: AppHandle) -> Result<(), LauncherError> {
    let source = source.to_string();
    let dest = dest.to_string();

    tokio::task::spawn_blocking(move || {
        let source_path = Path::new(&source);
        let dest_path = Path::new(&dest);

        let source_canonical = std::fs::canonicalize(source_path)
            .map_err(|e| LauncherError::Filesystem(format!("Failed to resolve source '{}': {}", source, e)))?;
        let dest_canonical = std::fs::canonicalize(dest_path)
            .or_else(|_| {
                std::fs::create_dir_all(dest_path)
                    .and_then(|_| std::fs::canonicalize(dest_path))
            })
            .map_err(|e| LauncherError::Filesystem(format!("Failed to resolve dest '{}': {}", dest, e)))?;

        if dest_canonical.starts_with(&source_canonical) {
            return Err(LauncherError::Filesystem(
                "Cannot copy game into its own directory".into(),
            ));
        }

        let entries: Vec<_> = walkdir::WalkDir::new(source_path)
            .into_iter()
            .collect::<Result<Vec<_>, _>>()
            .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
        let total = entries.len();
        let mut copied = 0u32;

        for entry in &entries {
            let entry = entry;
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

            copied += 1;
            if copied % 10 == 0 || copied as usize == total {
                let _ = app.emit(
                    "install-progress",
                    serde_json::json!({
                        "stage": "copying",
                        "progress": copied,
                        "total": total,
                    }),
                );
            }
        }

        Ok(())
    })
    .await
    .map_err(|e| LauncherError::InstallFailed(format!("Task join error: {}", e)))?
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
    let url = crate::github::latest_mod_asset_url(zip_name).await?;
    let zip_path = std::path::Path::new(dest).join(zip_name);

    let client = reqwest::Client::new();
    let resp = client
        .get(&url)
        .send()
        .await
        .map_err(|e| LauncherError::Network(e.to_string()))?
        .error_for_status()
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
                "progress": downloaded,
                "total": total,
            }),
        );
    }

    let dest_clone = dest.to_string();
    tokio::task::spawn_blocking(move || {
        std::fs::write(&zip_path, &bytes)
            .map_err(|e| LauncherError::Filesystem(e.to_string()))?;

        let dest_path = std::path::Path::new(&dest_clone);
        let file = std::fs::File::open(&zip_path)
            .map_err(|e| LauncherError::Filesystem(e.to_string()))?;
        let mut archive =
            zip::ZipArchive::new(file).map_err(|e| LauncherError::InstallFailed(e.to_string()))?;
        archive
            .extract(dest_path)
            .map_err(|e| LauncherError::InstallFailed(e.to_string()))?;

        let _ = std::fs::remove_file(&zip_path);
        Ok(())
    })
    .await
    .map_err(|e| LauncherError::InstallFailed(format!("Task join error: {}", e)))?
}

/// Why a single install attempt failed, for the bounded retry policy.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum AttemptFailure {
    /// The release or its asset is not published / not the expected build yet
    /// (HTTP 404, no `mod/` release, missing asset, digest/version mismatch).
    /// Retried; exhausting the budget means "still publishing".
    NotReady,
    /// Transport/DNS/HTTP-status failure. Retried; exhausting the budget means
    /// a genuine network error (reported distinctly from NotReady).
    Transient,
    /// Not retryable (disk/permission). Fails immediately.
    Fatal,
}

/// A failed attempt: its retry classification plus the underlying error.
#[derive(Debug)]
struct AttemptError {
    kind: AttemptFailure,
    error: LauncherError,
}

impl AttemptError {
    fn not_ready(msg: impl Into<String>) -> Self {
        Self {
            kind: AttemptFailure::NotReady,
            error: LauncherError::InstallFailed(msg.into()),
        }
    }

    fn transient(error: LauncherError) -> Self {
        Self {
            kind: AttemptFailure::Transient,
            error,
        }
    }

    fn fatal(error: LauncherError) -> Self {
        Self {
            kind: AttemptFailure::Fatal,
            error,
        }
    }

    /// The bare message, without the `LauncherError` variant prefix.
    fn detail(&self) -> &str {
        match &self.error {
            LauncherError::Config(m)
            | LauncherError::InstallFailed(m)
            | LauncherError::Ipc(m)
            | LauncherError::Lobby(m)
            | LauncherError::Auth(m)
            | LauncherError::Network(m)
            | LauncherError::Filesystem(m) => m,
            LauncherError::GameNotFound | LauncherError::NotInstalled => "unknown error",
        }
    }
}

/// `latest_mod_release` returns `Network("No mod release found")` when the repo
/// has no `mod/` release yet — a "not ready" condition, not a transport
/// failure. Everything else is treated as transient.
fn classify_release_error(err: LauncherError) -> AttemptError {
    if let LauncherError::Network(ref msg) = err {
        if msg.contains("No mod release found") {
            return AttemptError::not_ready(msg.clone());
        }
    }
    AttemptError::transient(err)
}

/// Bounded retry budget for "wait for the release to be published": 10
/// attempts spaced 30 s apart, so the whole resolve→download→verify loop gives
/// up after ~5 minutes instead of retrying forever.
const MOD_UPDATE_ATTEMPTS: u32 = 10;
const MOD_UPDATE_RETRY_DELAY: Duration = Duration::from_secs(30);

/// One resolve → download → verify → rename attempt for AmongApi.dll.
///
/// Verification order (all before the atomic `rename`):
/// 1. **Length** — `Content-Length` when the server sent one, else the release
///    asset's `size`.
/// 2. **Hash** — SHA-256 against the release asset's `digest` (skipped when
///    absent/malformed: cannot verify, never fail).
/// 3. **PE version** — the downloaded DLL's `FileVersion` must equal the
///    release version.
///
/// `passed_url` is the URL the frontend supplied (advisory only); the freshly
/// resolved asset URL is authoritative.
async fn attempt_install(
    dest: &Path,
    tmp_path: &Path,
    app: &AppHandle,
    progress_event: &str,
    passed_url: Option<&str>,
) -> Result<String, AttemptError> {
    // 1. Resolve the release — the authoritative version/digest/size/URL.
    let release = crate::version_checker::fetch_latest_mod_release()
        .await
        .map_err(classify_release_error)?;

    let url = match release.download_url.as_deref() {
        Some(url) => url.to_string(),
        None => {
            return Err(AttemptError::not_ready(
                "Asset AmongApi.dll not found in the latest mod release",
            ))
        }
    };
    if let Some(passed) = passed_url {
        if passed != url {
            // Never trust a frontend-supplied URL as the source of truth.
            eprintln!(
                "[among_api] resolved asset URL differs from the supplied one; using the resolved URL"
            );
        }
    }

    // 2. Download to the `.tmp` sibling (never write the live DLL directly).
    let _ = app.emit(
        progress_event,
        serde_json::json!({ "stage": "downloading", "progress": 0, "total": 0 }),
    );
    let client = reqwest::Client::builder()
        .user_agent("among-launcher")
        .build()
        .map_err(|e| AttemptError::transient(LauncherError::Network(e.to_string())))?;
    let resp = client
        .get(&url)
        .send()
        .await
        .map_err(|e| AttemptError::transient(LauncherError::Network(e.to_string())))?;
    let resp = resp.error_for_status().map_err(|e| {
        if e.status() == Some(reqwest::StatusCode::NOT_FOUND) {
            AttemptError::not_ready("AmongApi.dll asset returned HTTP 404")
        } else {
            AttemptError::transient(LauncherError::Network(e.to_string()))
        }
    })?;

    let content_length = resp.content_length().unwrap_or(0);
    let mut downloaded: u64 = 0;
    let mut bytes = Vec::new();
    let mut stream = resp.bytes_stream();
    while let Some(chunk) = stream.next().await {
        let chunk =
            chunk.map_err(|e| AttemptError::transient(LauncherError::Network(e.to_string())))?;
        downloaded += chunk.len() as u64;
        bytes.extend_from_slice(&chunk);
        let _ = app.emit(
            progress_event,
            serde_json::json!({
                "stage": "downloading",
                "progress": downloaded,
                "total": content_length,
            }),
        );
    }

    if let Some(parent) = dest.parent() {
        std::fs::create_dir_all(parent)
            .map_err(|e| AttemptError::fatal(LauncherError::Filesystem(e.to_string())))?;
    }
    if let Err(e) = std::fs::write(tmp_path, &bytes) {
        return Err(AttemptError::fatal(LauncherError::Filesystem(format!(
            "Close the game and try again: {}",
            e
        ))));
    }

    // 3a. Length.
    if !crate::version_checker::download_length_ok(content_length, release.size, downloaded) {
        let _ = std::fs::remove_file(tmp_path);
        let expected = if content_length > 0 {
            content_length
        } else {
            release.size.unwrap_or(0)
        };
        return Err(AttemptError::not_ready(format!(
            "Downloaded AmongApi.dll is {} bytes; expected {}",
            downloaded, expected
        )));
    }

    // 3b. Hash (only when the release advertises a usable digest).
    if let Some(expected) = release.digest.as_deref() {
        let actual = crate::mod_sync::compute_hash(tmp_path)
            .await
            .map_err(AttemptError::fatal)?;
        if crate::version_checker::digest_verdict(Some(expected), &actual) == Some(false) {
            let _ = std::fs::remove_file(tmp_path);
            return Err(AttemptError::not_ready(
                "Downloaded AmongApi.dll SHA-256 does not match the release digest",
            ));
        }
    }

    // 3c. PE version.
    let actual_version = crate::version_checker::extract_file_version(tmp_path);
    if !crate::version_checker::versions_equal(
        &release.version,
        actual_version.as_deref().unwrap_or(""),
    ) {
        let _ = std::fs::remove_file(tmp_path);
        return Err(AttemptError::not_ready(format!(
            "Downloaded AmongApi.dll reports version {} but the release is {}",
            actual_version.as_deref().unwrap_or("<none>"),
            release.version
        )));
    }

    // 4. All checks passed — atomically replace the installed DLL.
    let _ = app.emit(
        progress_event,
        serde_json::json!({
            "stage": "installing",
            "progress": downloaded,
            "total": content_length,
        }),
    );
    if let Err(e) = std::fs::rename(tmp_path, dest) {
        return Err(AttemptError::fatal(LauncherError::Filesystem(format!(
            "Close the game and try again: {}",
            e
        ))));
    }

    Ok(release.version)
}

/// Resolve the newest `mod/` release, download its AmongApi.dll, verify it
/// (length → SHA-256 → PE version) and only then atomically install it into
/// `<dest_dir>/BepInEx/Plugins/AmongApi.dll`.
///
/// Retries "not ready" conditions (404 / no release / missing asset / digest
/// or version mismatch) and transient transport failures with a bounded budget
/// (see [`MOD_UPDATE_ATTEMPTS`]). Between attempts a `waiting` progress event
/// is emitted so the UI can say it is waiting for the release to publish:
/// `{ "stage": "waiting", "attempt": N, "of": 10, "progress": 0, "total": 0 }`.
///
/// `passed_url` is retained for the update command's compatibility; the
/// resolved asset URL wins when they differ.
pub(crate) async fn install_verified_among_api(
    dest_dir: &str,
    app: &AppHandle,
    progress_event: &str,
    passed_url: Option<&str>,
) -> Result<String, LauncherError> {
    let plugins_dir = Path::new(dest_dir).join("BepInEx").join("Plugins");
    let dest = plugins_dir.join("AmongApi.dll");
    let tmp_path = dest.with_extension("tmp");

    let mut last: Option<AttemptError> = None;
    for attempt in 1..=MOD_UPDATE_ATTEMPTS {
        if attempt > 1 {
            let _ = app.emit(
                progress_event,
                serde_json::json!({
                    "stage": "waiting",
                    "attempt": attempt,
                    "of": MOD_UPDATE_ATTEMPTS,
                    "progress": 0,
                    "total": 0,
                }),
            );
            tokio::time::sleep(MOD_UPDATE_RETRY_DELAY).await;
        }

        match attempt_install(&dest, &tmp_path, app, progress_event, passed_url).await {
            Ok(version) => return Ok(version),
            Err(e) if e.kind == AttemptFailure::Fatal => {
                let _ = std::fs::remove_file(&tmp_path);
                return Err(e.error);
            }
            Err(e) => last = Some(e),
        }
    }

    let _ = std::fs::remove_file(&tmp_path);
    match last {
        Some(e) if e.kind == AttemptFailure::Transient => Err(LauncherError::Network(format!(
            "Could not download AmongApi after {} attempts: {}",
            MOD_UPDATE_ATTEMPTS,
            e.detail()
        ))),
        Some(e) => Err(LauncherError::InstallFailed(format!(
            "The AmongApi release is still being published — try again in a few minutes: {}",
            e.detail()
        ))),
        None => Err(LauncherError::InstallFailed(
            "AmongApi update failed without an attempt error".into(),
        )),
    }
}

pub async fn download_among_api(dest: &str, app: &AppHandle) -> Result<(), LauncherError> {
    // Resolve, download, verify (length → SHA-256 → PE version), then install.
    install_verified_among_api(dest, app, "install-progress", None).await?;

    let _ = app.emit(
        "install-progress",
        serde_json::json!({
            "stage": "among_api",
            "progress": 1,
            "total": 1,
        }),
    );
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    // The resolve/download/verify loop is network-bound and cannot be
    // unit-tested; these cover the pure policy/classification pieces.

    #[test]
    fn classify_no_mod_release_is_not_ready() {
        let err = classify_release_error(LauncherError::Network("No mod release found".into()));
        assert_eq!(err.kind, AttemptFailure::NotReady);
    }

    #[test]
    fn classify_transport_error_is_transient() {
        let err = classify_release_error(LauncherError::Network("dns lookup failed".into()));
        assert_eq!(err.kind, AttemptFailure::Transient);
        let err = classify_release_error(LauncherError::Filesystem("boom".into()));
        assert_eq!(err.kind, AttemptFailure::Transient);
    }

    #[test]
    fn attempt_error_constructors_tag_kinds() {
        assert_eq!(AttemptError::not_ready("x").kind, AttemptFailure::NotReady);
        assert_eq!(
            AttemptError::transient(LauncherError::Network("x".into())).kind,
            AttemptFailure::Transient
        );
        assert_eq!(
            AttemptError::fatal(LauncherError::Filesystem("x".into())).kind,
            AttemptFailure::Fatal
        );
    }

    #[test]
    fn attempt_error_detail_strips_variant_prefix() {
        assert_eq!(AttemptError::not_ready("still publishing").detail(), "still publishing");
        assert_eq!(
            AttemptError::transient(LauncherError::Network("net".into())).detail(),
            "net"
        );
        assert_eq!(
            AttemptError::fatal(LauncherError::Filesystem("disk".into())).detail(),
            "disk"
        );
    }

    #[test]
    fn retry_budget_is_bounded() {
        assert_eq!(MOD_UPDATE_ATTEMPTS, 10);
        assert_eq!(MOD_UPDATE_RETRY_DELAY, Duration::from_secs(30));
        // ~5 minutes total (9 gaps between 10 attempts).
        assert_eq!(
            MOD_UPDATE_RETRY_DELAY * (MOD_UPDATE_ATTEMPTS - 1),
            Duration::from_secs(270)
        );
    }
}

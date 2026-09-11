use futures_util::{SinkExt, StreamExt};
use tauri::{AppHandle, Emitter};
use tokio_tungstenite::{connect_async, tungstenite::Message};
use tokio_util::sync::CancellationToken;

pub async fn connect_ws(app: AppHandle, code: String, token: String, cancel: CancellationToken) {
    let url = format!("wss://among-us.mel-homes.com/ws?code={}", code);

    let mut delay = std::time::Duration::from_secs(2);
    loop {
        if cancel.is_cancelled() {
            break;
        }

        match connect_async(&url).await {
            Ok((ws_stream, _)) => {
                delay = std::time::Duration::from_secs(2); // Reset backoff on success
                let (mut write, mut read) = ws_stream.split();

                // Send auth
                let auth_msg = serde_json::json!({
                    "type": "auth",
                    "token": token,
                });
                let _ = write
                    .send(Message::Text(auth_msg.to_string().into()))
                    .await;

                let app = app.clone();
                let cancel = cancel.clone();
                loop {
                    tokio::select! {
                        msg = read.next() => {
                            match msg {
                                Some(Ok(Message::Text(text))) => {
                                    if let Ok(event) = serde_json::from_str::<serde_json::Value>(&text) {
                                        match event["type"].as_str() {
                                            Some("kick") => { let _ = app.emit("ws-kick", ()); }
                                            Some("rejoin") => { let _ = app.emit("ws-rejoin", event["payload"].clone()); }
                                            _ => {}
                                        }
                                    }
                                }
                                Some(Ok(Message::Close(_))) => break,
                                None => break,
                                Some(Err(_)) => break,
                                _ => {}
                            }
                        }
                        _ = cancel.cancelled() => {
                            let _ = write.close().await;
                            return;
                        }
                    }
                }
            }
            Err(e) => {
                eprintln!("WebSocket connection failed: {}", e);
            }
        }

        if cancel.is_cancelled() {
            break;
        }
        // Exponential backoff with jitter
        let jitter =
            std::time::Duration::from_millis(rand::random::<u64>() % 1000);
        tokio::select! {
            _ = tokio::time::sleep(delay + jitter) => {}
            _ = cancel.cancelled() => break,
        }
        delay = std::cmp::min(delay * 2, std::time::Duration::from_secs(30));
    }
}

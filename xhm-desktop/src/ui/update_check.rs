//! Minimal release metadata check for the About window.
//!
//! This module performs one GET request and returns UI state only. It does not
//! persist release data or invoke any external executable.

use serde::Deserialize;

pub const LATEST_RELEASE_URL: &str =
    "https://gitee.com/api/v5/repos/zhaowuyan/xhMonitor/releases/tags/latest";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReleaseAsset {
    pub name: String,
    pub url: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReleaseMetadata {
    pub tag_name: String,
    pub name: Option<String>,
    pub body: Option<String>,
    pub assets: Vec<ReleaseAsset>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum UpdateStatus {
    SourceUnavailable,
    UpToDate(ReleaseMetadata),
    UpdateAvailable(ReleaseMetadata),
    Error(String),
}

#[derive(Debug, Deserialize)]
struct ReleaseResponse {
    #[serde(default)]
    tag_name: String,
    #[serde(default)]
    name: Option<String>,
    #[serde(default)]
    body: Option<String>,
    #[serde(default)]
    assets: Vec<ReleaseAssetResponse>,
}

#[derive(Debug, Deserialize)]
struct ReleaseAssetResponse {
    #[serde(default)]
    name: String,
    #[serde(default)]
    browser_download_url: Option<String>,
    #[serde(default)]
    download_url: Option<String>,
    #[serde(default)]
    url: Option<String>,
}

pub async fn check_latest(client: &reqwest::Client, current_version: &str) -> UpdateStatus {
    check_at(client, LATEST_RELEASE_URL, current_version).await
}

pub async fn check_at(client: &reqwest::Client, url: &str, current_version: &str) -> UpdateStatus {
    let response = match client.get(url).send().await {
        Ok(response) => response,
        Err(error) => return UpdateStatus::Error(error.to_string()),
    };
    if response.status().as_u16() == 404 {
        return UpdateStatus::SourceUnavailable;
    }
    if !response.status().is_success() {
        return UpdateStatus::Error(format!("release source returned HTTP {}", response.status()));
    }
    let release = match response.json::<ReleaseResponse>().await {
        Ok(release) if !release.tag_name.trim().is_empty() => release,
        Ok(_) => return UpdateStatus::Error("release source omitted tag_name".into()),
        Err(error) => return UpdateStatus::Error(format!("invalid release metadata: {error}")),
    };
    let metadata = ReleaseMetadata {
        tag_name: release.tag_name,
        name: release.name,
        body: release.body,
        assets: release
            .assets
            .into_iter()
            .map(|asset| ReleaseAsset {
                name: asset.name,
                url: asset
                    .browser_download_url
                    .or(asset.download_url)
                    .or(asset.url),
            })
            .collect(),
    };
    if version_is_newer(&metadata.tag_name, current_version) {
        UpdateStatus::UpdateAvailable(metadata)
    } else {
        UpdateStatus::UpToDate(metadata)
    }
}

pub fn version_is_newer(candidate: &str, current: &str) -> bool {
    let candidate = parse_version(candidate);
    let current = parse_version(current);
    for index in 0..candidate.len().max(current.len()) {
        let candidate_part = candidate.get(index).copied().unwrap_or(0);
        let current_part = current.get(index).copied().unwrap_or(0);
        if candidate_part != current_part {
            return candidate_part > current_part;
        }
    }
    false
}

fn parse_version(value: &str) -> Vec<u64> {
    let value = value.trim().trim_start_matches(['v', 'V']);
    value
        .split(['.', '-', '+'])
        .take_while(|part| !part.is_empty())
        .map(|part| {
            part.chars()
                .take_while(|character| character.is_ascii_digit())
                .collect::<String>()
                .parse::<u64>()
                .unwrap_or(0)
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use wiremock::matchers::{method, path};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    fn client() -> reqwest::Client {
        reqwest::Client::builder().build().unwrap()
    }

    #[tokio::test]
    async fn check_is_single_get_and_reports_newer_release() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/latest"))
            .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
                "tag_name": "v0.4.0",
                "name": "0.4.0",
                "body": "release notes",
                "assets": [{"name": "xhm.zip", "browser_download_url": "https://example.invalid/xhm.zip"}]
            })))
            .mount(&server)
            .await;
        let status = check_at(&client(), &format!("{}/latest", server.uri()), "0.3.0").await;
        assert!(matches!(status, UpdateStatus::UpdateAvailable(_)));
        assert_eq!(server.received_requests().await.unwrap().len(), 1);
    }

    #[tokio::test]
    async fn check_maps_404_to_source_unavailable() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .respond_with(ResponseTemplate::new(404))
            .mount(&server)
            .await;
        assert_eq!(
            check_at(&client(), &server.uri(), "0.3.0").await,
            UpdateStatus::SourceUnavailable
        );
    }

    #[tokio::test]
    async fn check_maps_invalid_metadata_to_error() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .respond_with(ResponseTemplate::new(200).set_body_string("not-json"))
            .mount(&server)
            .await;
        assert!(matches!(
            check_at(&client(), &server.uri(), "0.3.0").await,
            UpdateStatus::Error(_)
        ));
    }

    #[tokio::test]
    async fn check_reports_up_to_date_for_same_or_older_release() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
                "tag_name": "v0.3.0", "assets": []
            })))
            .mount(&server)
            .await;
        assert!(matches!(
            check_at(&client(), &server.uri(), "0.3.0").await,
            UpdateStatus::UpToDate(_)
        ));
    }

    #[tokio::test]
    async fn check_maps_server_failure_to_error() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .respond_with(ResponseTemplate::new(503))
            .mount(&server)
            .await;
        assert!(matches!(
            check_at(&client(), &server.uri(), "0.3.0").await,
            UpdateStatus::Error(_)
        ));
    }

    #[test]
    fn semantic_numeric_comparison_handles_v_prefix() {
        assert!(version_is_newer("v1.10.0", "1.9.9"));
        assert!(!version_is_newer("v1.2.0", "1.2.0"));
        assert!(!version_is_newer("v1.1.9", "1.2.0"));
    }
}

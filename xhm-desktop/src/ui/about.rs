//! About-window runtime and update-state presentation.

use crate::ui::update_check::{check_latest, ReleaseMetadata, UpdateStatus};
use crate::AboutWindow;

pub fn apply_update_status(app: &AboutWindow, status: UpdateStatus) {
    match status {
        UpdateStatus::SourceUnavailable => {
            app.set_update_state("SourceUnavailable".into());
            app.set_update_message("The Gitee release source is unavailable.".into());
            app.set_release_summary("Source release: unavailable".into());
        }
        UpdateStatus::UpToDate(release) => {
            app.set_update_state("UpToDate".into());
            app.set_update_message("This Rust desktop version is current.".into());
            app.set_release_summary(release_summary(&release).into());
        }
        UpdateStatus::UpdateAvailable(release) => {
            app.set_update_state("UpdateAvailable".into());
            app.set_update_message("A newer release is available. Review its details on Gitee.".into());
            app.set_release_summary(release_summary(&release).into());
        }
        UpdateStatus::Error(error) => {
            app.set_update_state("Error".into());
            app.set_update_message(format!("Update check failed: {error}").into());
            app.set_release_summary("Source release: no result".into());
        }
    }
}

fn release_summary(release: &ReleaseMetadata) -> String {
    let title = release.name.as_deref().unwrap_or(&release.tag_name);
    let asset_count = release.assets.len();
    let body = release
        .body
        .as_deref()
        .map(str::trim)
        .filter(|body| !body.is_empty())
        .map(|body| format!(" - {}", body.replace('\n', " ")))
        .unwrap_or_default();
    format!(
        "Source release: {} ({title}, {asset_count} assets){body}",
        release.tag_name
    )
}

#[cfg(windows)]
enum AboutAsyncResult {
    Checked(UpdateStatus),
}

#[cfg(windows)]
pub struct AboutUiRuntime {
    _timer: slint::Timer,
}

#[cfg(windows)]
pub fn install_runtime(app: &AboutWindow) -> AboutUiRuntime {
    use std::cell::RefCell;
    use std::rc::Rc;

    use slint::ComponentHandle;

    let (sender, receiver) = std::sync::mpsc::channel();
    let receiver = Rc::new(RefCell::new(receiver));
    let weak = app.as_weak();
    app.set_app_version(format!("v{}", env!("CARGO_PKG_VERSION")).into());

    {
        let sender = sender.clone();
        let weak = weak.clone();
        app.on_check_update(move || {
            if let Some(app) = weak.upgrade() {
                app.set_update_state("Checking".into());
                app.set_update_message("Checking the Gitee release source...".into());
                app.set_release_summary("".into());
            }
            spawn_update_check(sender.clone());
        });
    }

    {
        let weak = weak.clone();
        app.on_close_window(move || {
            if let Some(app) = weak.upgrade() {
                let _ = app.hide();
            }
        });
    }

    let timer = slint::Timer::default();
    {
        let receiver = Rc::clone(&receiver);
        let weak = weak.clone();
        timer.start(
            slint::TimerMode::Repeated,
            std::time::Duration::from_millis(100),
            move || loop {
                let result = receiver.borrow().try_recv();
                let Ok(AboutAsyncResult::Checked(status)) = result else {
                    break;
                };
                if let Some(app) = weak.upgrade() {
                    apply_update_status(&app, status);
                }
            },
        );
    }

    AboutUiRuntime { _timer: timer }
}

#[cfg(windows)]
fn spawn_update_check(sender: std::sync::mpsc::Sender<AboutAsyncResult>) {
    let _ = std::thread::Builder::new()
        .name("xhm-update-check".into())
        .spawn(move || {
            let status = tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
                .map_err(|error| error.to_string())
                .and_then(|runtime| {
                    runtime.block_on(async {
                        let client = reqwest::Client::builder()
                            .build()
                            .map_err(|error| error.to_string())?;
                        Ok::<_, String>(check_latest(&client, env!("CARGO_PKG_VERSION")).await)
                    })
                })
                .unwrap_or_else(UpdateStatus::Error);
            let _ = sender.send(AboutAsyncResult::Checked(status));
        });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn release_summary_is_metadata_only() {
        let release = ReleaseMetadata {
            tag_name: "v0.4.0".into(),
            name: Some("Spring release".into()),
            body: Some("New metrics\nBug fixes".into()),
            assets: vec![],
        };
        let summary = release_summary(&release);
        assert!(summary.contains("v0.4.0"));
        assert!(summary.contains("New metrics Bug fixes"));
    }
}

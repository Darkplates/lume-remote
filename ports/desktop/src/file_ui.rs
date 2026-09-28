use super::*;
use lume_core::files::FileCommand;
pub(super) fn render_files(ctx: &egui::Context, remote: &mut Remote) {
    let (files, folders) = {
        let view = remote.viewer.state.lock().unwrap();
        (view.files.clone(), view.capabilities & 16 != 0)
    };
    let state = files.lock().unwrap().clone();
    let mut action = None;
    let mut open = remote.files;
    egui::Window::new("Files")
        .open(&mut open)
        .default_width(620.0)
        .show(ctx, |ui| {
            ui.horizontal(|ui| {
                if ui
                    .add_enabled(!state.listing, egui::Button::new("Drives / shares"))
                    .clicked()
                {
                    action = Some(FileCommand::List {
                        path: String::new(),
                        page: 0,
                    });
                }
                if ui
                    .add_enabled(
                        !state.listing && !state.path.is_empty(),
                        egui::Button::new("Up"),
                    )
                    .clicked()
                {
                    action = Some(FileCommand::List {
                        path: parent(&state.path),
                        page: 0,
                    });
                }
                if ui
                    .add_enabled(!state.listing, egui::Button::new("Refresh"))
                    .clicked()
                {
                    action = Some(FileCommand::List {
                        path: state.path.clone(),
                        page: state.page,
                    });
                }
            });
            ui.label(if state.path.is_empty() {
                "Choose a remote drive or share"
            } else {
                &state.path
            });
            egui::ScrollArea::vertical()
                .max_height(260.0)
                .show(ui, |ui| {
                    for entry in &state.entries {
                        ui.horizontal(|ui| {
                            if entry.directory {
                                if ui
                                    .add_enabled(
                                        !state.listing,
                                        egui::Button::new(format!("{} /", entry.name)),
                                    )
                                    .clicked()
                                {
                                    action = Some(FileCommand::List {
                                        path: child(&state.path, &entry.name),
                                        page: 0,
                                    });
                                }
                                if !state.path.is_empty()
                                    && ui
                                        .add_enabled(
                                            !state.active && folders,
                                            egui::Button::new("Download folder"),
                                        )
                                        .clicked()
                                {
                                    action = Some(FileCommand::DownloadFolder {
                                        remote: child(&state.path, &entry.name),
                                        folder: remote.download.clone().into(),
                                        name: entry.name.clone(),
                                    });
                                }
                            } else {
                                ui.label(&entry.name);
                                ui.small(format!("{} bytes", entry.length));
                                if ui
                                    .add_enabled(!state.active, egui::Button::new("Download"))
                                    .clicked()
                                {
                                    action = Some(FileCommand::Download {
                                        remote: child(&state.path, &entry.name),
                                        folder: remote.download.clone().into(),
                                        name: entry.name.clone(),
                                    });
                                }
                            }
                        });
                    }
                });
            ui.horizontal(|ui| {
                if ui
                    .add_enabled(
                        !state.listing && state.page > 0,
                        egui::Button::new("Previous page"),
                    )
                    .clicked()
                {
                    action = Some(FileCommand::List {
                        path: state.path.clone(),
                        page: state.page - 1,
                    });
                }
                ui.small(format!("Page {}", state.page + 1));
                if ui
                    .add_enabled(!state.listing && state.more, egui::Button::new("Next page"))
                    .clicked()
                {
                    action = Some(FileCommand::List {
                        path: state.path.clone(),
                        page: state.page + 1,
                    });
                }
            });
            ui.separator();
            ui.label("Download folder");
            ui.add(egui::TextEdit::singleline(&mut remote.download).desired_width(f32::INFINITY));
            ui.label("Upload a file or folder — paste a local path or drop it here");
            ui.add(egui::TextEdit::singleline(&mut remote.upload).desired_width(f32::INFINITY));
            for file in ctx.input(|i| i.raw.dropped_files.clone()) {
                if let Some(path) = file.path {
                    remote.upload = path.to_string_lossy().into();
                }
            }
            let allows_upload =
                !state.active && !state.path.is_empty() && !remote.upload.is_empty();
            let mut upload_folder = None;
            ui.horizontal(|ui| {
                if ui
                    .add_enabled(allows_upload, egui::Button::new("Upload file"))
                    .clicked()
                {
                    upload_folder = Some(false);
                }
                if ui
                    .add_enabled(allows_upload && folders, egui::Button::new("Upload folder"))
                    .clicked()
                {
                    upload_folder = Some(true);
                }
            });
            if !folders {
                ui.small("This host supports individual files only.");
            }
            if let Some(folder) = upload_folder {
                let local = std::path::PathBuf::from(&remote.upload);
                let name = local
                    .file_name()
                    .map(|v| v.to_string_lossy().into_owned())
                    .unwrap_or_default();
                action = Some(if folder {
                    FileCommand::UploadFolder {
                        local,
                        folder: state.path.clone(),
                        name,
                    }
                } else {
                    FileCommand::Upload {
                        local,
                        folder: state.path.clone(),
                        name,
                    }
                });
            }
            if state.active {
                ui.label(format!("{} {}", state.direction, state.name));
                if state.folder_job {
                    ui.small(format!(
                        "{} items complete · {} bytes verified",
                        state.items_done, state.bytes_done
                    ));
                }
                ui.add(
                    egui::ProgressBar::new(if state.total > 0 {
                        state.bytes as f32 / state.total as f32
                    } else {
                        0.0
                    })
                    .show_percentage(),
                );
                if ui.button("Cancel transfer").clicked() {
                    action = Some(FileCommand::Cancel);
                }
            }
            if state.resumable { ui.small("Paired file resume enabled. Retry the same file and destination after an interruption."); }
            if state.resumed_bytes > 0 { ui.small(format!("{} bytes reused after verification", state.resumed_bytes)); }
            if !state.status.is_empty() {
                ui.label(&state.status);
            }
            if let Some(path) = &state.completed {
                ui.label(format!("Saved: {path}"));
                if !state.completed_directory && std::path::Path::new(path).extension().is_some_and(|e| e.eq_ignore_ascii_case("pdf")) && ui.button("Print PDF…").clicked() {
                    remote.printer.start(path.into());
                }

            }
        });
    remote.files = open;
    if let Some(action) = action {
        if let Err(e) = remote.viewer.command(Command::Files(action)) {
            remote
                .viewer
                .state
                .lock()
                .unwrap()
                .files
                .lock()
                .unwrap()
                .status = e.to_string();
        }
    }
}
pub(super) fn download_folder() -> String {
    let home = std::env::var_os(if cfg!(windows) { "USERPROFILE" } else { "HOME" })
        .map(std::path::PathBuf::from)
        .unwrap_or_default();
    let downloads = home.join("Downloads");
    if downloads.is_dir() {
        downloads.to_string_lossy().into()
    } else {
        home.to_string_lossy().into()
    }
}
fn child(path: &str, name: &str) -> String {
    if path.is_empty() {
        name.into()
    } else {
        format!(
            "{}{}{}",
            path.trim_end_matches(['\\', '/']),
            if path.contains('\\') { '\\' } else { '/' },
            name
        )
    }
}
fn parent(path: &str) -> String {
    let value = path.trim_end_matches(['\\', '/']);
    match value.rfind(['\\', '/']) {
        Some(2) if value.as_bytes().get(1) == Some(&b':') => value[..3].into(),
        Some(0) => "/".into(),
        Some(index) => value[..index].into(),
        None => String::new(),
    }
}

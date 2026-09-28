use crate::host_runtime;
use eframe::egui;
use lume_core::host_store::{HostConfig, HostStore};
use std::{path::Path, sync::Arc, thread};
use zeroize::Zeroizing;

enum Action {
    Refresh,
    Enable(String, bool, Option<String>),
    Disable,
    Pair,
    Revoke(String),
    CancelPair,
}
struct Loaded {
    store: Arc<HostStore>,
    config: HostConfig,
    code: Option<Zeroizing<String>>,
}
#[derive(Default)]
pub struct HostPanel {
    showing: bool,
    store: Option<Arc<HostStore>>,
    pending: Option<thread::JoinHandle<Result<Loaded, String>>>,
    name: String,
    enabled: bool,
    control: bool,
    share: bool,
    folder: String,
    computers: Vec<(String, String)>,
    code: Option<Zeroizing<String>>,
    error: String,
    heartbeat: String,
    heartbeat_at: Option<std::time::Instant>,
}
impl HostPanel {
    fn action(&mut self, action: Action) {
        if self.pending.is_some() {
            return;
        }
        let existing = self.store.clone();
        self.error.clear();
        self.pending = Some(thread::spawn(move || {
            (|| -> anyhow::Result<Loaded> {
                let store = match existing {
                    Some(store) => store,
                    None => host_runtime::open(matches!(&action, Action::Enable(..)))?,
                };
                let mut code = None;
                match action {
                    Action::Refresh => {}
                    Action::Enable(name, control, folder) => {
                        #[cfg(target_os = "linux")]
                        {
                            anyhow::ensure!(
                                std::env::var_os("DISPLAY").is_some(),
                                "Enable access from your graphical desktop session"
                            );
                            anyhow::ensure!(
                                !control || std::env::var_os("WAYLAND_DISPLAY").is_none(),
                                "Wayland control requires the portal adapter; use an X11 session"
                            );
                        }
                        if let Some(folder) = &folder {
                            anyhow::ensure!(
                                Path::new(folder).is_absolute() && Path::new(folder).is_dir(),
                                "Choose an existing absolute folder"
                            );
                        }
                        crate::peer_library()?;
                        #[cfg(target_os = "macos")]
                        crate::native::permission_preflight(control)?;
                        let previous = store.load()?;
                        store.change(|c| {
                            c.enabled = true;
                            c.name = name;
                            c.control = control;
                            c.folder = folder;
                            Ok(())
                        })?;
                        if let Err(error) =
                            host_runtime::startup(true).and_then(|_| host_runtime::spawn())
                        {
                            let _ = store.change(|c| {
                                *c = previous;
                                Ok(())
                            });
                            return Err(error);
                        }
                    }
                    Action::Disable => {
                        store.change(|c| {
                            c.enabled = false;
                            c.pending = None;
                            Ok(())
                        })?;
                        host_runtime::startup(false)?;
                    }
                    Action::Pair => {
                        code = Some(Zeroizing::new(store.change(|c| c.pairing())?));
                    }
                    Action::Revoke(id) => {
                        store.change(|c| {
                            c.controllers.retain(|v| v.id != id);
                            Ok(())
                        })?;
                    }
                    Action::CancelPair => {
                        store.change(|c| {
                            c.pending = None;
                            Ok(())
                        })?;
                    }
                }
                let config = store.load()?;
                Ok(Loaded {
                    store,
                    config,
                    code,
                })
            })()
            .map_err(|e| e.to_string())
        }));
    }
    pub fn render(&mut self, ui: &mut egui::Ui) {
        if !host_runtime::supported() {
            return;
        }
        if ui.button("Permanent access…").clicked() {
            self.showing = true;
            if self.name.is_empty() {
                self.name = "My computer".into();
            }
            if self.store.is_some()
                || host_runtime::directory().is_ok_and(|p| p.join("host.encrypted").exists())
            {
                self.action(Action::Refresh);
            }
        }
        if self.pending.as_ref().is_some_and(|w| w.is_finished()) {
            match self.pending.take().unwrap().join() {
                Ok(Ok(result)) => {
                    self.store = Some(result.store);
                    self.enabled = result.config.enabled;
                    self.name = result.config.name.clone();
                    self.control = result.config.control;
                    self.folder = result.config.folder.clone().unwrap_or_default();
                    self.share = result.config.folder.is_some();
                    self.computers = result
                        .config
                        .controllers
                        .iter()
                        .map(|c| (c.id.clone(), c.name.clone()))
                        .collect();
                    self.code = result.code;
                }
                Ok(Err(error)) => self.error = error,
                Err(_) => self.error = "Host settings operation failed".into(),
            }
        }
        if !self.showing {
            return;
        }
        if self.heartbeat_at.is_none_or(|v| v.elapsed().as_secs() >= 1) {
            self.heartbeat = host_runtime::heartbeat();
            self.heartbeat_at = Some(std::time::Instant::now());
        }
        let mut open = true;
        egui::Window::new("Permanent access").open(&mut open).resizable(true).default_width(460.0).show(ui.ctx(), |ui| {
            ui.label("Paired computers can connect after you sign in, even when this window is closed.");
            ui.small("The operating system still controls login, screen access and sleep.");
            ui.label(&self.heartbeat);
            if self.pending.is_some() { ui.spinner(); ui.label("Working… The desktop keyring may request permission."); }
            ui.add_enabled_ui(self.pending.is_none(), |ui| {
                ui.label("Computer name"); ui.text_edit_singleline(&mut self.name);
                ui.checkbox(&mut self.control, "Allow paired computers to control keyboard and mouse");
                ui.add_enabled_ui(self.control, |ui| { ui.checkbox(&mut self.share, "Share one folder with paired computers"); });
                if self.share && self.control { ui.text_edit_singleline(&mut self.folder); }
                ui.horizontal(|ui| {
                    if ui.button(if self.enabled { "Save settings / restart host" } else { "Enable access at sign-in" }).clicked() {
                        self.action(Action::Enable(self.name.clone(), self.control, if self.share && self.control { Some(self.folder.clone()) } else { None }));
                    }
                    if self.enabled && ui.button("Disable access").clicked() { self.action(Action::Disable); }
                });
                if self.enabled {
                    ui.separator();
                    ui.horizontal(|ui| {
                        if ui.button("Pair another computer").clicked() { self.action(Action::Pair); }
                        if ui.button("Refresh").clicked() { self.action(Action::Refresh); }
                    });
                    if let Some(code) = &self.code {
                        ui.label("One-time code. Expires in ten minutes; keep it private.");
                        if ui.button("Copy pairing code").clicked() { ui.ctx().copy_text(code.to_string()); }
                        if ui.button("Cancel pairing code").clicked() { self.action(Action::CancelPair); }
                    }
                    let mut revoke = None;
                    for (id, name) in &self.computers { ui.horizontal(|ui| { ui.label(name); if ui.button("Revoke").clicked() { revoke = Some(id.clone()); } }); }
                    if let Some(id) = revoke { self.action(Action::Revoke(id)); }
                }
            });
            if !self.error.is_empty() { ui.colored_label(egui::Color32::LIGHT_RED, &self.error); }
        });
        if !open {
            self.showing = false;
            self.code = None;
        }
    }
}

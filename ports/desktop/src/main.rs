mod audio;
mod file_ui;
mod host_runtime;
mod host_ui;
mod native;
mod printing;
use eframe::egui::{
    self, Color32, TextureHandle, TextureOptions, Vec2, ViewportBuilder, ViewportCommand,
    ViewportId,
};
use file_ui::{download_folder, render_files};
use lume_core::{
    session::{Command, Host, HostRequest, Viewer},
    wire::Quality,
};
use std::{sync::Arc, time::Duration};

fn main() -> eframe::Result {
    if std::env::args().nth(1).as_deref() == Some("--microphone-consent") {
        return audio::consent_window();
    }
    if std::env::args().nth(1).as_deref() == Some("--host-daemon") {
        if host_runtime::daemon().is_err() {
            std::process::exit(1);
        }
        return Ok(());
    }
    eframe::run_native(
        "Lume",
        eframe::NativeOptions {
            viewport: ViewportBuilder::default()
                .with_inner_size([740.0, 550.0])
                .with_min_inner_size([580.0, 420.0]),
            ..Default::default()
        },
        Box::new(|cc| {
            cc.egui_ctx.set_visuals(egui::Visuals::dark());
            let mut style = (*cc.egui_ctx.style()).clone();
            style.visuals.selection.bg_fill = Color32::from_rgb(42, 147, 127);
            style.spacing.item_spacing = Vec2::new(10.0, 12.0);
            cc.egui_ctx.set_style(style);
            let mut app = App::default();
            app.use_p2p = true;
            Ok(Box::new(app))
        }),
    )
}
struct Remote {
    _audio: audio::Player,
    voice_consent: bool,
    id: u64,
    viewer: Viewer,
    texture: Option<TextureHandle>,
    sequence: i32,
    wake_ready: bool,
    keyboard: bool,
    modifiers: egui::Modifiers,
    quality: Quality,
    settings: bool,
    files: bool,
    monitors: bool,
    upload: String,
    download: String,
    printer: printing::Printing,
    recording_fps: u32,
    drawing: bool,
    stroke: Vec<[i32; 2]>,
    stroke_epoch: i32,
}
#[derive(Default)]
struct App {
    permanent: host_ui::HostPanel,
    vault: Option<lume_core::vault::Vault>,
    vault_password: String,
    vault_dialog: bool,
    pending_vault: Option<std::thread::JoinHandle<anyhow::Result<lume_core::vault::Vault>>>,
    pairing: Option<Viewer>,
    pairing_save_failed: bool,
    invitation: String,
    address: String,
    status: String,
    viewers: Vec<Remote>,
    next_id: u64,
    host: Option<Host>,
    approval: Option<HostRequest>,
    allow_control: bool,
    share_files: bool,
    shared_folder: String,
    confirm_exit: bool,
    use_p2p: bool,
    reply: String,
    pending_host: Option<std::thread::JoinHandle<Result<Host, String>>>,
    pending_stop: Arc<std::sync::atomic::AtomicBool>,
    closing_hosts: Vec<Host>,
    closing_viewers: Vec<Viewer>,
}
impl App {
    fn connect(&mut self) {
        let quality = Quality::default();
        let text = self.invitation.trim();
        let pairing = text.starts_with("lume-pair://");
        if pairing && self.vault.is_none() {
            self.vault_dialog = true;
            self.status = "Unlock saved computers before using a pairing code.".into();
            return;
        }
        if pairing && self.pairing.is_some() {
            self.status = "Wait for the current pairing request.".into();
            return;
        }
        let connection = (|| -> anyhow::Result<Viewer> {
            let library = if text.starts_with("lume-p2p://") || text.starts_with("lume-saved://") {
                peer_library()?
            } else {
                Default::default()
            };
            Viewer::open(text, &library, quality)
        })();
        match connection {
            Ok(viewer) => {
                if pairing {
                    self.pairing = Some(viewer);
                    self.pairing_save_failed = false;
                    self.invitation.clear();
                    self.status.clear();
                    return;
                }
                self.next_id += 1;
                let quality = Quality::default();
                let media_state = viewer.state.lock().unwrap().media.clone();
                self.viewers.push(Remote {
                    _audio: audio::Player::new(media_state),
                    voice_consent: false,
                    id: self.next_id,
                    viewer,
                    texture: None,
                    sequence: 0,
                    wake_ready: false,
                    keyboard: false,
                    modifiers: Default::default(),
                    quality,
                    settings: false,
                    files: false,
                    monitors: false,
                    upload: String::new(),
                    printer: Default::default(),
                    recording_fps: 0,
                    drawing: false,
                    stroke: Vec::new(),
                    stroke_epoch: 0,
                    download: download_folder(),
                });
                self.invitation.clear();
                self.status.clear();
            }
            Err(e) => self.status = e.to_string(),
        }
    }
    fn share(&mut self) {
        let folder = if self.share_files && self.allow_control {
            let folder = std::path::PathBuf::from(&self.shared_folder);
            if !folder.is_absolute() || !folder.is_dir() {
                self.status = "Choose an existing absolute folder to share.".into();
                return;
            }
            Some(folder)
        } else {
            None
        };
        if self.use_p2p {
            let library = match peer_library() {
                Ok(path) => path,
                Err(e) => {
                    self.status = e.to_string();
                    return;
                }
            };
            let control = self.allow_control;
            self.pending_stop = Arc::new(std::sync::atomic::AtomicBool::new(false));
            let stop = self.pending_stop.clone();
            self.status = "Preparing your invitation…".into();
            self.pending_host = Some(std::thread::spawn(move || {
                Host::peer_cancellable(
                    &library,
                    control,
                    Arc::new(move || native::NativeDesktop::open_shared(control, folder.clone())),
                    true,
                    stop,
                )
                .map_err(|e| e.to_string())
            }));
            return;
        }
        let address = if self.address.trim().is_empty() {
            "127.0.0.1"
        } else {
            self.address.trim()
        };
        let control = self.allow_control;
        match Host::listen(
            "0.0.0.0:0",
            address,
            control,
            Arc::new(move || native::NativeDesktop::open_shared(control, folder.clone())),
        ) {
            Ok(host) => {
                self.host = Some(host);
                self.status.clear();
            }
            Err(e) => self.status = e.to_string(),
        }
    }
    fn stop_sharing(&mut self) {
        self.approval.take();
        if let Some(host) = self.host.take() {
            host.stop();
            self.closing_hosts.push(host);
        }
    }
}
impl Drop for App {
    fn drop(&mut self) {
        // Cancel all work first, then join through the handles' RAII teardown.
        self.pending_stop
            .store(true, std::sync::atomic::Ordering::Release);
        if let Some(host) = &self.host {
            host.stop();
        }
        for remote in &self.viewers {
            remote.viewer.disconnect();
        }
        if let Some(worker) = self.pending_host.take() {
            let _ = worker.join();
        }
    }
}
impl eframe::App for App {
    fn update(&mut self, ctx: &egui::Context, _: &mut eframe::Frame) {
        if self.pending_vault.as_ref().is_some_and(|w| w.is_finished()) {
            match self.pending_vault.take().unwrap().join() {
                Ok(Ok(vault)) => {
                    self.vault = Some(vault);
                    self.vault_dialog = false;
                    self.status.clear();
                }
                Ok(Err(e)) => self.status = e.to_string(),
                Err(_) => self.status = "Vault worker failed.".into(),
            }
        }
        if !self.pairing_save_failed && self.pairing.as_ref().is_some_and(|p| p.is_finished()) {
            let pair = self.pairing.take().unwrap();
            let saved = pair.state.lock().unwrap().paired.take();
            if let Some(saved) = saved {
                match self
                    .vault
                    .as_mut()
                    .ok_or_else(|| anyhow::anyhow!("Unlock the vault to save this computer"))
                    .and_then(|v| v.save(saved.clone()))
                {
                    Ok(()) => self.status = "Computer saved. Use Connect below.".into(),
                    Err(e) => {
                        self.status = e.to_string();
                        pair.state.lock().unwrap().paired = Some(saved);
                        self.pairing = Some(pair);
                        self.pairing_save_failed = true;
                    }
                }
            } else {
                self.status = pair.state.lock().unwrap().status.clone();
            }
        }
        self.closing_hosts.retain(|host| !host.is_finished());
        self.closing_viewers.retain(|viewer| !viewer.is_finished());
        if self
            .pending_host
            .as_ref()
            .is_some_and(|worker| worker.is_finished())
        {
            if let Some(worker) = self.pending_host.take() {
                let result = worker
                    .join()
                    .unwrap_or_else(|_| Err("Sharing worker ended unexpectedly".into()));
                match result {
                    Ok(host) => {
                        self.host = Some(host);
                        self.status.clear();
                    }
                    Err(error) => self.status = error,
                }
            }
        }
        // Status/consent polling is independent of frame-driven presentation.
        ctx.request_repaint_after(Duration::from_millis(100));
        if ctx.input(|i| i.viewport().close_requested())
            && (self.host.is_some() || !self.viewers.is_empty())
        {
            ctx.send_viewport_cmd(ViewportCommand::CancelClose);
            if !self.viewers.is_empty() {
                ctx.send_viewport_cmd(ViewportCommand::Visible(false));
            } else {
                self.confirm_exit = true;
            }
        }
        egui::CentralPanel::default().show(ctx,|ui|{egui::ScrollArea::vertical().show(ui,|ui|{
            ui.add_space(10.0);ui.heading(egui::RichText::new("Lume").size(34.0).color(Color32::from_rgb(105,221,195)));ui.label("Development build · 0.12");ui.add_space(12.0);
            ui.heading("Connect to a computer");ui.add(egui::TextEdit::multiline(&mut self.invitation).hint_text("Paste a Lume invitation").desired_rows(2).desired_width(f32::INFINITY));
            if ui.add_enabled(!self.invitation.trim().is_empty(),egui::Button::new("Connect")).clicked(){self.connect();}
            ui.small("Use an invitation for a guest session, or a one-time pairing code to save a computer.");
            if let Some(pair)=&self.pairing {ui.label(pair.state.lock().unwrap().status.clone());if self.pairing_save_failed && ui.button("Retry saving").clicked(){self.pairing_save_failed=false;}if ui.button("Cancel pairing").clicked(){pair.disconnect();pair.state.lock().unwrap().paired=None;self.pairing_save_failed=false;}}
            let mut saved_connection=None;let mut forget=None;
            if let Some(vault)=&self.vault {
                if !vault.computers.is_empty(){ui.label("Saved computers");}
                for computer in &vault.computers {ui.horizontal(|ui|{ui.label(&computer.name);if ui.button("Connect").clicked(){saved_connection=computer.connection().ok();}if ui.small_button("Forget").on_hover_text("Remove this local credential. Revoke access on the host to invalidate it.").clicked(){forget=Some(computer.id.clone());}});}
                if self.pairing.is_none() && ui.small_button("Lock saved computers").clicked(){self.vault=None;}
            } else if ui.button("Unlock saved computers").clicked(){self.vault_dialog=true;}
            if let Some(id)=forget {if let Some(vault)=&mut self.vault {if let Err(e)=vault.remove(&id){self.status=e.to_string();}}}
            if let Some(connection)=saved_connection{let previous=std::mem::take(&mut self.invitation);self.invitation=connection;self.connect();use zeroize::Zeroize;self.invitation.zeroize();self.invitation=previous;}
            ui.add_space(10.0);ui.separator();ui.heading("Share this computer");
            if let Some(host)=&self.host {
                ui.label(host.status.lock().unwrap().clone());ui.horizontal(|ui|{if ui.button("Copy invitation").clicked(){ui.ctx().copy_text(host.code.clone());}});
                if host.is_peer() { ui.add(egui::TextEdit::multiline(&mut self.reply).hint_text("Paste the other computer's reply").desired_rows(2).desired_width(f32::INFINITY)); if ui.add_enabled(!self.reply.trim().is_empty(),egui::Button::new("Use reply")).clicked(){match host.accept_reply(self.reply.clone()){Ok(())=>self.reply.clear(),Err(e)=>self.status=e.to_string()}} }
                if self.approval.is_none(){self.approval=host.requests.try_recv().ok();}
                if ui.button("Stop sharing").clicked(){self.stop_sharing();}
            }else{
                ui.checkbox(&mut self.use_p2p,"Internet / P2P");
                if !self.use_p2p {ui.horizontal(|ui|{ui.label("Reachable address");ui.add(egui::TextEdit::singleline(&mut self.address).hint_text("IP or hostname").desired_width(280.0));});}
                ui.checkbox(&mut self.allow_control,"Allow keyboard and mouse after my approval");
                ui.add_enabled(self.allow_control,egui::Checkbox::new(&mut self.share_files,"Share a folder after my approval"));
                if self.share_files && self.allow_control {ui.add(egui::TextEdit::singleline(&mut self.shared_folder).hint_text("Absolute folder path").desired_width(f32::INFINITY));ui.small("Only this folder is exposed, as the shared R: drive.");}
                if ui.add_enabled(self.pending_host.is_none(),egui::Button::new("Start sharing")).clicked(){self.share();}
                ui.small("Share the invitation privately. You approve the connection before capture begins.");
            }
            self.permanent.render(ui);
            if !self.status.is_empty(){ui.colored_label(Color32::LIGHT_RED,&self.status);}
            if !self.viewers.is_empty(){ui.add_space(8.0);if ui.button("Hide dashboard").clicked(){ctx.send_viewport_cmd(ViewportCommand::Visible(false));}ui.small("Each remote computer has its own window.");}
        });});
        if self.vault_dialog {
            egui::Window::new("Saved computers").collapsible(false).resizable(false).show(ctx,|ui|{
                ui.label("Unlock your encrypted computer list. For a new vault, choose a passphrase of at least 12 characters.");
                ui.add(egui::TextEdit::singleline(&mut self.vault_password).password(true).hint_text("Vault passphrase"));
                if ui.add_enabled(self.pending_vault.is_none(),egui::Button::new("Unlock / create vault")).clicked(){
                    let password=zeroize::Zeroizing::new(std::mem::take(&mut self.vault_password));
                    self.pending_vault=Some(std::thread::spawn(move||lume_core::vault::Vault::unlock(&vault_path()?,&password)));
                }
                if ui.button("Close").clicked(){use zeroize::Zeroize;self.vault_password.zeroize();self.vault_dialog=false;}
            });
        }
        if let Some(request) = &self.approval {
            let mut decision = None;
            egui::Window::new("Connection request")
                .collapsible(false)
                .resizable(false)
                .show(ctx, |ui| {
                    ui.label(format!(
                        "{} wants to {} this desktop.",
                        request.name,
                        if request.control {
                            "view and control"
                        } else {
                            "view"
                        }
                    ));
                    ui.label("Screen capture starts only after Allow. Stop sharing ends access.");
                    if self.share_files && self.allow_control {
                        ui.label(format!(
                            "File access is enabled for: {}",
                            self.shared_folder
                        ));
                    }
                    ui.horizontal(|ui| {
                        if ui.button("Allow").clicked() {
                            decision = Some(true)
                        }
                        if ui.button("Decline").clicked() {
                            decision = Some(false)
                        }
                    });
                });
            if let Some(value) = decision {
                if let Some(r) = self.approval.take() {
                    let _ = r.answer.send(value);
                }
            }
        }
        if self.confirm_exit {
            egui::Window::new("Stop sharing and exit?")
                .collapsible(false)
                .show(ctx, |ui| {
                    ui.horizontal(|ui| {
                        if ui.button("Keep sharing").clicked() {
                            self.confirm_exit = false
                        }
                        if ui.button("Stop and exit").clicked() {
                            self.stop_sharing();
                            self.confirm_exit = false;
                            ctx.send_viewport_cmd(ViewportCommand::Close)
                        }
                    });
                });
        }
        let mut closed = Vec::new();
        for remote in &mut self.viewers {
            if !remote.wake_ready {
                let wake = ctx.clone();
                let viewport = ViewportId::from_hash_of(remote.id);
                remote
                    .viewer
                    .set_frame_wakeup(move || wake.request_repaint_of(viewport));
                remote.wake_ready = true;
            }
            let title = {
                let s = remote.viewer.state.lock().unwrap();
                if s.peer.is_empty() {
                    "Lume — Connecting".into()
                } else {
                    format!("Lume — {}", s.peer)
                }
            };
            ctx.show_viewport_immediate(
                ViewportId::from_hash_of(remote.id),
                ViewportBuilder::default()
                    .with_title(title)
                    .with_inner_size([1100.0, 760.0])
                    .with_min_inner_size([480.0, 320.0]),
                |ctx, _| {
                    if ctx.input(|i| i.viewport().close_requested()) {
                        remote.viewer.disconnect();
                        closed.push(remote.id);
                        return;
                    }
                    render_remote(ctx, remote);
                },
            );
        }
        if !closed.is_empty() {
            let mut remaining = Vec::new();
            for remote in self.viewers.drain(..) {
                if closed.contains(&remote.id) {
                    self.closing_viewers.push(remote.viewer);
                } else {
                    remaining.push(remote);
                }
            }
            self.viewers = remaining;
            if self.viewers.is_empty() {
                ctx.send_viewport_cmd(ViewportCommand::Visible(true));
                ctx.send_viewport_cmd(ViewportCommand::Focus);
            }
        }
    }
}
fn render_remote(ctx: &egui::Context, remote: &mut Remote) {
    remote.printer.render(ctx);
    let (status, connected, control, input_ready, epoch, seq, image, caps, clipboard) = {
        let mut s = remote.viewer.state.lock().unwrap();
        (
            s.status.clone(),
            s.connected,
            s.control,
            s.connected && s.control && s.frame.is_some() && !s.input_suspended,
            s.epoch,
            s.sequence,
            s.frame.clone(),
            s.capabilities,
            s.clipboard.take(),
        )
    };
    if let Some(text) = clipboard {
        ctx.copy_text(text)
    }
    if image.is_none() {
        remote.texture = None;
        remote.keyboard = false;
        remote.modifiers = Default::default();
    }
    if seq != remote.sequence {
        if let Some(frame) = image {
            let image = egui::ColorImage::from_rgba_unmultiplied(
                [frame.width() as usize, frame.height() as usize],
                frame.as_raw(),
            );
            if let Some(t) = &mut remote.texture {
                t.set(image, TextureOptions::LINEAR)
            } else {
                remote.texture = Some(ctx.load_texture(
                    format!("desktop-{}", remote.id),
                    image,
                    TextureOptions::LINEAR,
                ))
            }
            remote.sequence = seq;
        }
    }
    if let Some(reply) = remote.viewer.state.lock().unwrap().reply.clone() {
        egui::TopBottomPanel::bottom("peer-reply").show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.label("Copy this reply to the sharing computer.");
                if ui.button("Copy reply").clicked() {
                    ctx.copy_text(reply.clone());
                }
            });
        });
    }
    egui::TopBottomPanel::top("session-toolbar").show(ctx, |ui| {
        ui.horizontal_wrapped(|ui| {
            if ui.button("Dashboard").clicked() {
                ctx.send_viewport_cmd_to(ViewportId::ROOT, ViewportCommand::Visible(true));
                ctx.send_viewport_cmd_to(ViewportId::ROOT, ViewportCommand::Focus);
            }
            if ui.button("Quality").clicked() {
                let _ = remote.viewer.command(Command::Release);
                remote.keyboard = false;
                remote.settings = !remote.settings;
            }
            if ui
                .add_enabled(
                    connected && caps & lume_core::monitors::CAPABILITY != 0,
                    egui::Button::new("Displays"),
                )
                .clicked()
            {
                let _ = remote.viewer.command(Command::Release);
                remote.keyboard = false;
                remote.monitors = !remote.monitors;
                if remote.monitors {
                    let _ = remote.viewer.command(Command::ListMonitors);
                }
            }
            if ui
                .add_enabled(
                    input_ready && caps & 64 != 0,
                    egui::Button::new(if remote.drawing {
                        "Finish drawing"
                    } else {
                        "Draw"
                    }),
                )
                .clicked()
            {
                let _ = remote.viewer.command(Command::Release);
                remote.keyboard = false;
                remote.modifiers = Default::default();
                remote.drawing = !remote.drawing;
                remote.stroke.clear();
            }
            if remote.drawing && ui.button("Clear marks").clicked() {
                remote.stroke.clear();
                let _ = remote.viewer.command(Command::Annotation(epoch, vec![]));
            }
            let files_allowed = remote.viewer.state.lock().unwrap().files_allowed;
            if ui
                .add_enabled(connected && files_allowed, egui::Button::new("Files"))
                .clicked()
            {
                let _ = remote.viewer.command(Command::Release);
                remote.keyboard = false;
                remote.files = !remote.files;
                if remote.files {
                    let _ = remote.viewer.command(Command::Files(
                        lume_core::files::FileCommand::List {
                            path: String::new(),
                            page: 0,
                        },
                    ));
                }
            }
            if ui
                .add_enabled(connected && control, egui::Button::new("Send text"))
                .clicked()
            {
                match arboard::Clipboard::new().and_then(|mut c| c.get_text()) {
                    Ok(text) => {
                        if let Err(e) = remote.viewer.command(Command::Clipboard(text)) {
                            remote.viewer.state.lock().unwrap().status = e.to_string();
                        }
                    }
                    Err(_) => {
                        remote.viewer.state.lock().unwrap().status =
                            "Clipboard text is unavailable".into()
                    }
                }
            }
            if ui
                .add_enabled(
                    connected && control && caps & 1 != 0,
                    egui::Button::new("Get text"),
                )
                .clicked()
            {
                let _ = remote.viewer.command(Command::ReadClipboard);
            }
            if ui.button("Disconnect").clicked() {
                remote.viewer.disconnect();
                ctx.send_viewport_cmd(ViewportCommand::Close);
            }
            let recording = remote.viewer.recording.status.lock().unwrap().active;
            if ui
                .add_enabled(
                    connected || recording,
                    egui::Button::new(if recording {
                        "Stop recording"
                    } else {
                        "Record"
                    }),
                )
                .clicked()
            {
                if recording {
                    remote.viewer.recording.stop();
                } else {
                    let folder = std::path::PathBuf::from(download_folder());
                    let stamp = std::time::SystemTime::now()
                        .duration_since(std::time::UNIX_EPOCH)
                        .unwrap_or_default()
                        .as_millis();
                    let path = folder.join(format!("Lume-{}-{stamp}.mkv", remote.id));
                    if let Err(e) = remote.viewer.recording.start_with_fps(
                        remote.viewer.state.clone(),
                        &path,
                        remote.recording_fps,
                    ) {
                        remote.viewer.state.lock().unwrap().status = e.to_string();
                    }
                }
            }
            let media = remote.viewer.state.lock().unwrap().media.clone();
            let (a, v, ap, vp, message) = {
                let s = media.lock().unwrap();
                (
                    s.audio,
                    s.voice,
                    s.audio_pending,
                    s.voice_pending,
                    s.status.clone(),
                )
            };
            if ui
                .add_enabled(
                    connected && audio::available() && caps & 32 != 0,
                    egui::Button::new(if a || ap { "Sound off" } else { "Sound" }),
                )
                .clicked()
            {
                let _ = remote.viewer.command(Command::Audio(!(a || ap)));
            }
            if ui
                .add_enabled(
                    connected && audio::available() && caps & 1024 != 0,
                    egui::Button::new(if v || vp { "End call" } else { "Voice" }),
                )
                .clicked()
            {
                if v || vp {
                    let _ = remote.viewer.command(Command::Voice(false));
                } else {
                    remote.voice_consent = true;
                    remote.keyboard = false;
                    let _ = remote.viewer.command(Command::Release);
                }
            }
            if !message.is_empty() {
                ui.small(message);
            }
            ui.label(&status);
        });
        let recorded = remote.viewer.recording.status.lock().unwrap();
        if !recorded.path.is_empty() {
            ui.small(format!("{} · {}", recorded.message, recorded.path));
        }
    });
    if remote.voice_consent {
        egui::Window::new("Start a voice call").collapsible(false).resizable(false).show(ctx,|ui|{
            ui.label("Allow your microphone for this call? The host must also accept. Use headphones to reduce echo.");
            if ui.button("Allow microphone and request call").clicked(){let _=remote.viewer.command(Command::Voice(true));remote.voice_consent=false;}
            if ui.button("Cancel").clicked(){remote.voice_consent=false;}
        });
    }
    if remote.settings {
        egui::Window::new("Quality").resizable(false).show(ctx, |ui| {
            ui.horizontal(|ui| {
                if ui.button("Source").clicked() { remote.quality = Quality { height: 0, fps: 0, jpeg: 100, lossless: true }; }
                if ui.button("Save data").clicked() { remote.quality = Quality { height: 360, fps: 10, jpeg: 65, lossless: false }; }
            });
            ui.horizontal(|ui| { ui.label("Height (0 = source)"); ui.add(egui::DragValue::new(&mut remote.quality.height).range(0..=16384)); });
            ui.horizontal(|ui| { ui.label("FPS (0 = source)"); ui.add(egui::DragValue::new(&mut remote.quality.fps).range(0..=1000)); });
            ui.add_enabled(caps & lume_core::wire::PORTABLE_IMAGES != 0, egui::Checkbox::new(&mut remote.quality.lossless, "Lossless pixels"));
            ui.horizontal(|ui| { ui.label("Recording FPS (0 = source)"); ui.add_enabled(!remote.viewer.recording.status.lock().unwrap().active, egui::DragValue::new(&mut remote.recording_fps).range(0..=1000)); });
            if caps & lume_core::wire::PORTABLE_IMAGES == 0 { ui.small("This host supports JPEG for portable viewers. Source keeps its resolution."); }
            if ui.button("Apply").clicked() { match remote.viewer.command(Command::Quality(remote.quality)) { Ok(()) => remote.settings = false, Err(e) => remote.viewer.state.lock().unwrap().status = e.to_string() } }
        });
    }
    if remote.files {
        render_files(ctx, remote);
    }
    if remote.monitors {
        let (displays, pending, message) = {
            let s = remote.viewer.state.lock().unwrap();
            (
                s.monitors.clone(),
                s.monitor_pending,
                s.monitor_status.clone(),
            )
        };
        egui::Window::new("Displays")
            .resizable(false)
            .open(&mut remote.monitors)
            .show(ctx, |ui| {
                if pending {
                    ui.spinner();
                }
                if !message.is_empty() {
                    ui.label(message);
                }
                for display in displays {
                    let label = format!(
                        "{}{} · {} × {} · {} Hz",
                        if display.selected { "✓ " } else { "" },
                        display.name,
                        display.width,
                        display.height,
                        display.refresh
                    );
                    if ui
                        .add_enabled(connected && !pending, egui::Button::new(label))
                        .clicked()
                    {
                        if let Err(e) = remote.viewer.command(Command::SelectMonitor(display.id)) {
                            remote.viewer.state.lock().unwrap().monitor_status = e.to_string();
                        }
                    }
                }
                if ui
                    .add_enabled(connected && !pending, egui::Button::new("Refresh"))
                    .clicked()
                {
                    let _ = remote.viewer.command(Command::ListMonitors);
                }
            });
    }
    egui::CentralPanel::default()
        .frame(egui::Frame::NONE.fill(Color32::BLACK))
        .show(ctx, |ui| {
            if let Some(texture) = &remote.texture {
                let available = ui.available_size();
                let original = texture.size_vec2();
                let scale = (available.x / original.x)
                    .min(available.y / original.y)
                    .max(0.01);
                let response = ui.add(
                    egui::Image::new((texture.id(), original * scale))
                        .sense(egui::Sense::click_and_drag()),
                );
                if response.clicked() {
                    remote.keyboard = true;
                }
                let focused = ctx.input(|i| i.viewport().focused.unwrap_or(true));
                if !focused && remote.keyboard {
                    let _ = remote.viewer.command(Command::Release);
                    remote.keyboard = false;
                    remote.modifiers = Default::default();
                }
                if !input_ready || remote.stroke_epoch != epoch || !focused {
                    remote.stroke.clear();
                }
                if remote.drawing
                    && input_ready
                    && focused
                    && !remote.files
                    && !remote.settings
                    && !remote.monitors
                {
                    if response.drag_started() {
                        remote.stroke.clear();
                        remote.stroke_epoch = epoch;
                    }
                    if response.dragged() {
                        if let Some(pos) = response.interact_pointer_pos() {
                            let point = [
                                ((pos.x - response.rect.left()) / response.rect.width() * 65535.0)
                                    .clamp(0.0, 65535.0) as i32,
                                ((pos.y - response.rect.top()) / response.rect.height() * 65535.0)
                                    .clamp(0.0, 65535.0) as i32,
                            ];
                            if remote.stroke.last() != Some(&point) {
                                if remote.stroke.len() == 128 {
                                    remote.stroke.remove(126);
                                }
                                remote.stroke.push(point);
                            }
                        }
                    }
                    if remote.stroke.len() > 1 {
                        let points = remote
                            .stroke
                            .iter()
                            .map(|p| {
                                egui::pos2(
                                    response.rect.left()
                                        + p[0] as f32 / 65535.0 * response.rect.width(),
                                    response.rect.top()
                                        + p[1] as f32 / 65535.0 * response.rect.height(),
                                )
                            })
                            .collect();
                        ui.painter().add(egui::Shape::line(
                            points,
                            egui::Stroke::new(3.0, Color32::from_rgb(80, 245, 181)),
                        ));
                    }
                    if response.drag_stopped() {
                        let points = std::mem::take(&mut remote.stroke);
                        if points.len() >= 2 {
                            if let Err(e) = remote
                                .viewer
                                .command(Command::Annotation(remote.stroke_epoch, points))
                            {
                                remote.viewer.state.lock().unwrap().status = e.to_string();
                            }
                        }
                    }
                }
                if !remote.drawing
                    && input_ready
                    && focused
                    && !remote.files
                    && !remote.settings
                    && !remote.monitors
                {
                    let events = ctx.input(|i| i.events.clone());
                    for e in events {
                        match e {
                            egui::Event::PointerMoved(pos) if response.rect.contains(pos) => {
                                let x = ((pos.x - response.rect.left()) / response.rect.width()
                                    * 65535.0)
                                    .clamp(0.0, 65535.0)
                                    as i32;
                                let y = ((pos.y - response.rect.top()) / response.rect.height()
                                    * 65535.0)
                                    .clamp(0.0, 65535.0)
                                    as i32;
                                let _ = remote.viewer.command(Command::Input(0, x, y, epoch));
                            }
                            egui::Event::PointerButton {
                                pos,
                                button,
                                pressed,
                                ..
                            } => {
                                if response.rect.contains(pos) {
                                    let b = match button {
                                        egui::PointerButton::Primary => 0,
                                        egui::PointerButton::Secondary => 1,
                                        egui::PointerButton::Middle => 2,
                                        _ => continue,
                                    };
                                    let _ = remote.viewer.command(Command::Input(
                                        if pressed { 1 } else { 2 },
                                        b,
                                        0,
                                        epoch,
                                    ));
                                    if pressed {
                                        remote.keyboard = true;
                                    }
                                } else if !pressed {
                                    let _ = remote.viewer.command(Command::Release);
                                    remote.keyboard = false;
                                    remote.modifiers = Default::default();
                                }
                            }
                            egui::Event::Key {
                                key,
                                pressed,
                                modifiers,
                                ..
                            } if remote.keyboard && !ctx.wants_keyboard_input() => {
                                sync_modifiers(remote, modifiers, epoch);
                                if let Some(vk) = vk(key) {
                                    let _ = remote.viewer.command(Command::Input(
                                        if pressed { 4 } else { 5 },
                                        vk,
                                        0,
                                        epoch,
                                    ));
                                }
                            }
                            egui::Event::MouseWheel { delta, .. } if response.hovered() => {
                                let value = if delta.y > 0.0 {
                                    120
                                } else if delta.y < 0.0 {
                                    -120
                                } else {
                                    0
                                };
                                let _ = remote.viewer.command(Command::Input(3, value, 0, epoch));
                            }
                            _ => {}
                        }
                    }
                }
            } else {
                ui.centered_and_justified(|ui| ui.label(&status));
            }
        });
}
fn sync_modifiers(remote: &mut Remote, m: egui::Modifiers, epoch: i32) {
    let old = remote.modifiers;
    for (vk, a, b) in [
        (16, old.shift, m.shift),
        (17, old.ctrl, m.ctrl),
        (18, old.alt, m.alt),
        (91, old.mac_cmd, m.mac_cmd),
    ] {
        if a != b {
            let _ = remote
                .viewer
                .command(Command::Input(if b { 4 } else { 5 }, vk, 0, epoch));
        }
    }
    remote.modifiers = m;
}
fn vk(key: egui::Key) -> Option<i32> {
    use egui::Key::*;
    Some(match key {
        ArrowLeft => 37,
        ArrowUp => 38,
        ArrowRight => 39,
        ArrowDown => 40,
        Escape => 27,
        Tab => 9,
        Backspace => 8,
        Enter => 13,
        Space => 32,
        Insert => 45,
        Delete => 46,
        Home => 36,
        End => 35,
        PageUp => 33,
        PageDown => 34,
        Num0 => 48,
        Num1 => 49,
        Num2 => 50,
        Num3 => 51,
        Num4 => 52,
        Num5 => 53,
        Num6 => 54,
        Num7 => 55,
        Num8 => 56,
        Num9 => 57,
        A => 65,
        B => 66,
        C => 67,
        D => 68,
        E => 69,
        F => 70,
        G => 71,
        H => 72,
        I => 73,
        J => 74,
        K => 75,
        L => 76,
        M => 77,
        N => 78,
        O => 79,
        P => 80,
        Q => 81,
        R => 82,
        S => 83,
        T => 84,
        U => 85,
        V => 86,
        W => 87,
        X => 88,
        Y => 89,
        Z => 90,
        F1 => 112,
        F2 => 113,
        F3 => 114,
        F4 => 115,
        F5 => 116,
        F6 => 117,
        F7 => 118,
        F8 => 119,
        F9 => 120,
        F10 => 121,
        F11 => 122,
        F12 => 123,
        _ => return None,
    })
}

fn peer_library() -> anyhow::Result<std::path::PathBuf> {
    let path = std::env::current_exe()?
        .parent()
        .ok_or_else(|| anyhow::anyhow!("Application folder is unavailable"))?
        .join(if cfg!(target_os = "windows") {
            "datachannel.dll"
        } else if cfg!(target_os = "macos") {
            "libdatachannel.dylib"
        } else {
            "libdatachannel.so"
        });
    anyhow::ensure!(
        path.is_file(),
        "The WebRTC library is missing. Extract the whole Lume package."
    );
    Ok(path)
}
fn vault_path() -> anyhow::Result<std::path::PathBuf> {
    let path = if cfg!(target_os = "windows") {
        std::env::var_os("LOCALAPPDATA").map(std::path::PathBuf::from)
    } else if cfg!(target_os = "macos") {
        std::env::var_os("HOME")
            .map(|v| std::path::PathBuf::from(v).join("Library/Application Support"))
    } else {
        std::env::var_os("XDG_CONFIG_HOME")
            .map(std::path::PathBuf::from)
            .or_else(|| {
                std::env::var_os("HOME").map(|v| std::path::PathBuf::from(v).join(".config"))
            })
    };
    Ok(path
        .ok_or_else(|| anyhow::anyhow!("User configuration folder is unavailable"))?
        .join("LumeRemotePortable")
        .join("computers.vault"))
}

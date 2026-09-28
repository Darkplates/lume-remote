//! Explicit CUPS selection on Linux; the packaged PDFKit print panel on macOS.
use anyhow::{Context, Result, ensure};
use eframe::egui;
use lume_core::printing::{PdfSnapshot, snapshot};
use std::{
    io::Read,
    path::PathBuf,
    process::{Command, Stdio},
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
#[derive(Default)]
pub struct Printing {
    open: bool,
    status: String,
    printers: Vec<String>,
    selected: String,
    pdf: Option<PdfSnapshot>,
    prepare: Option<JoinHandle<Result<(PdfSnapshot, Vec<String>)>>>,
    submit: Option<JoinHandle<Result<()>>>,
}
fn destinations(text: &str) -> Vec<String> {
    text.lines()
        .take(256)
        .filter_map(|line| line.split_whitespace().next())
        .filter(|s| {
            !s.is_empty()
                && !s.starts_with('-')
                && s.len() <= 127
                && s.bytes()
                    .all(|b| b.is_ascii_alphanumeric() || b"._-".contains(&b))
        })
        .map(str::to_owned)
        .collect()
}
fn run(command: &mut Command) -> Result<String> {
    let mut child = command
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()?;
    let stdout = child.stdout.take().context("Missing printer response")?;
    let reader = thread::spawn(move || {
        let mut value = String::new();
        stdout.take(32769).read_to_string(&mut value).map(|_| value)
    });
    let started = Instant::now();
    let result = loop {
        if let Some(status) = child.try_wait()? {
            break Ok(status);
        }
        if started.elapsed() > Duration::from_secs(20) {
            let _ = child.kill();
            let _ = child.wait();
            break Err(anyhow::anyhow!("Printer did not respond"));
        }
        thread::sleep(Duration::from_millis(25));
    };
    let text = reader
        .join()
        .map_err(|_| anyhow::anyhow!("Printer response failed"))??;
    ensure!(text.len() <= 32768, "Printer response exceeds its bound");
    ensure!(result?.success(), "Printing service rejected the request");
    Ok(text)
}
impl Printing {
    pub fn start(&mut self, path: PathBuf) {
        if self.prepare.is_some() || self.submit.is_some() {
            return;
        }
        self.open = true;
        self.status = "Preparing PDF…".into();
        self.pdf = None;
        self.prepare = Some(thread::spawn(move || {
            let pdf = snapshot(&path)?;
            let printers = if cfg!(target_os = "linux") {
                destinations(&run(Command::new("/usr/bin/lpstat")
                    .arg("-a")
                    .env("LC_ALL", "C"))?)
            } else {
                vec![]
            };
            Ok((pdf, printers))
        }));
    }
    pub fn render(&mut self, ctx: &egui::Context) {
        if self.prepare.as_ref().is_some_and(|w| w.is_finished()) {
            match self
                .prepare
                .take()
                .unwrap()
                .join()
                .unwrap_or_else(|_| Err(anyhow::anyhow!("PDF preparation failed")))
            {
                Ok((pdf, printers)) => {
                    self.pdf = Some(pdf);
                    self.printers = printers;
                    self.selected = self.printers.first().cloned().unwrap_or_default();
                    self.status = if cfg!(target_os = "linux") && self.printers.is_empty() {
                        "No accepting printers. Configure a printer in the system settings.".into()
                    } else {
                        "Choose where to print this PDF.".into()
                    };
                }
                Err(e) => self.status = format!("Cannot prepare printing: {e}"),
            }
        }
        if self.submit.as_ref().is_some_and(|w| w.is_finished()) {
            self.status = match self.submit.take().unwrap().join() {
                Ok(Ok(())) => {
                    "Print request handed to the local system. Check its queue for the outcome."
                        .into()
                }
                Ok(Err(e)) => format!("Print request failed: {e}"),
                Err(_) => "Print worker failed.".into(),
            };
        }
        if !self.open {
            self.pdf = None;
            return;
        }
        let mut open = self.open;
        egui::Window::new("Print downloaded PDF")
            .open(&mut open)
            .show(ctx, |ui| {
                ui.label(&self.status);
                if cfg!(target_os = "linux") {
                    egui::ComboBox::from_label("Printer")
                        .selected_text(&self.selected)
                        .show_ui(ui, |ui| {
                            for printer in &self.printers {
                                ui.selectable_value(&mut self.selected, printer.clone(), printer);
                            }
                        });
                }
                let supported = cfg!(any(target_os = "linux", target_os = "macos"));
                if !supported {
                    ui.label("Use the native Windows app to print.");
                }
                if ui
                    .add_enabled(
                        supported
                            && self.pdf.is_some()
                            && self.submit.is_none()
                            && (!cfg!(target_os = "linux")
                                || self.printers.contains(&self.selected)),
                        egui::Button::new(if cfg!(target_os = "macos") {
                            "Open system print panel"
                        } else {
                            "Print one copy"
                        }),
                    )
                    .clicked()
                {
                    let pdf = self.pdf.take().unwrap();
                    let printer = self.selected.clone();
                    self.submit = Some(thread::spawn(move || {
                        if cfg!(target_os = "macos") {
                            let helper = std::env::current_exe()?
                                .parent()
                                .context("Application folder unavailable")?
                                .join("lume-print");
                            ensure!(
                                helper.is_file(),
                                "The packaged PDFKit print helper is missing"
                            );
                            // This child owns the snapshot while the local user operates the native panel.
                            let status = Command::new(helper).arg(&pdf.path).status()?;
                            ensure!(
                                status.success(),
                                "Print panel closed without a completed request"
                            );
                        } else {
                            run(Command::new("/usr/bin/lp")
                                .args(["-d", &printer, "-n", "1", "--"])
                                .arg(&pdf.path))?;
                        }
                        Ok(())
                    }));
                    self.status = "Waiting for the local printing system…".into();
                }
            });
        self.open = open;
        if !open {
            self.pdf = None;
        }
        if self.prepare.is_some() || self.submit.is_some() {
            ctx.request_repaint_after(Duration::from_millis(100));
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn printer_names_cannot_become_options_or_shell_commands() {
        assert_eq!(
            destinations(
                "Office_Printer accepting requests\n-evil bad\nx;touch bad\nRoom.2 accepting\n"
            ),
            vec!["Office_Printer", "Room.2"]
        );
        // Names are separate arguments after -d, never command text or an option position.
        assert_eq!(destinations(&"a".repeat(128)), Vec::<String>::new());
    }
}

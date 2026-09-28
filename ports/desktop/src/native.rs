use anyhow::Result;
use enigo::{Axis, Button, Coordinate, Direction, Enigo, Key, Keyboard, Mouse, Settings};
use image::RgbaImage;
use lume_core::monitors::Monitor;
use lume_core::session::Desktop;
use std::collections::HashSet;

#[cfg(target_os = "macos")]
pub fn permission_preflight(control: bool) -> Result<()> {
    unsafe {
        if !CGPreflightScreenCaptureAccess() && !CGRequestScreenCaptureAccess() {
            anyhow::bail!(
                "Allow Screen Recording for Lume in macOS System Settings before enabling access"
            );
        }
        #[link(name = "ApplicationServices", kind = "framework")]
        unsafe extern "C" {
            fn AXIsProcessTrusted() -> bool;
        }
        anyhow::ensure!(
            !control || AXIsProcessTrusted(),
            "Allow Accessibility for Lume in macOS System Settings before enabling keyboard and mouse control"
        );
    }
    Ok(())
}

pub struct NativeDesktop {
    monitor: xcap::Monitor,
    input: Option<Enigo>,
    x: i32,
    y: i32,
    width: u32,
    height: u32,
    refresh: u32,
    keys: HashSet<i32>,
    buttons: HashSet<i32>,
    clipboard: Option<arboard::Clipboard>,
    shared_folder: Option<std::path::PathBuf>,
}
impl NativeDesktop {
    pub fn open(control: bool) -> Result<Box<dyn Desktop>> {
        Self::open_shared(control, None)
    }
    pub fn open_shared(
        control: bool,
        folder: Option<std::path::PathBuf>,
    ) -> Result<Box<dyn Desktop>> {
        #[cfg(target_os = "macos")]
        permission_preflight(control)?;
        #[cfg(target_os = "linux")]
        anyhow::ensure!(
            !control || std::env::var_os("WAYLAND_DISPLAY").is_none(),
            "Wayland control needs a RemoteDesktop portal session; use view-only until that adapter is commissioned"
        );
        let monitors = xcap::Monitor::all()?;
        let monitor = monitors
            .iter()
            .find(|m| m.is_primary().unwrap_or(false))
            .or(monitors.first())
            .ok_or_else(|| anyhow::anyhow!("No accessible monitor"))?
            .clone();
        let display = metadata(&monitor, monitor.id()?)?;
        lume_core::frame::dimensions(display.width as i32, display.height as i32)?;
        let (x, y, width, height, refresh) = (
            display.x,
            display.y,
            display.width,
            display.height,
            display.refresh,
        );
        let input = if control {
            Some(Enigo::new(&Settings::default())?)
        } else {
            None
        };
        Ok(Box::new(Self {
            monitor,
            input,
            x,
            y,
            width,
            height,
            refresh,
            shared_folder: folder,
            keys: HashSet::new(),
            buttons: HashSet::new(),
            clipboard: if control {
                arboard::Clipboard::new().ok()
            } else {
                None
            },
        }))
    }
}
impl Desktop for NativeDesktop {
    fn supports_monitors(&self) -> bool {
        true
    }
    fn monitors(&mut self) -> Result<Vec<Monitor>> {
        let selected = self.monitor.id()?;
        let list = xcap::Monitor::all()?
            .iter()
            .map(|monitor| metadata(monitor, selected))
            .collect::<Result<Vec<_>>>()?;
        lume_core::monitors::validate_list(&list)?;
        Ok(list)
    }
    fn select_monitor(&mut self, id: &str) -> Result<()> {
        let monitor = xcap::Monitor::all()?
            .into_iter()
            .find(|m| m.id().is_ok_and(|value| value.to_string() == id))
            .ok_or_else(|| anyhow::anyhow!("The display is no longer available"))?;
        let display = metadata(&monitor, monitor.id()?)?;
        lume_core::frame::dimensions(display.width as i32, display.height as i32)?;
        // Complete every fallible check before replacing capture/input coordinates.
        self.release();
        self.monitor = monitor;
        (self.x, self.y, self.width, self.height, self.refresh) = (
            display.x,
            display.y,
            display.width,
            display.height,
            display.refresh,
        );
        Ok(())
    }
    fn media(&self) -> Option<Box<dyn lume_core::media::Backend>> {
        crate::audio::available().then(|| {
            Box::new(crate::audio::HostAudio::default()) as Box<dyn lume_core::media::Backend>
        })
    }
    fn shared_folder(&self) -> Option<std::path::PathBuf> {
        self.shared_folder.clone()
    }
    fn size(&self) -> (u32, u32, u32) {
        (self.width, self.height, self.refresh)
    }
    fn capture(&mut self) -> Result<RgbaImage> {
        Ok(self.monitor.capture_image()?)
    }
    fn input(&mut self, action: u8, a: i32, b: i32) -> Result<()> {
        lume_core::session::validate_input(action, a, b)?;
        let Some(input) = self.input.as_mut() else {
            return Ok(());
        };
        match action {
            0 => input.move_mouse(
                self.x + (a as i64 * (self.width - 1) as i64 / 65535) as i32,
                self.y + (b as i64 * (self.height - 1) as i64 / 65535) as i32,
                Coordinate::Abs,
            )?,
            1 | 2 => {
                let button = match a {
                    0 => Button::Left,
                    1 => Button::Right,
                    _ => Button::Middle,
                };
                if action == 1 {
                    input.button(button, Direction::Press)?;
                    self.buttons.insert(a);
                } else if self.buttons.remove(&a) {
                    input.button(button, Direction::Release)?;
                }
            }
            3 => input.scroll(-a / 120, Axis::Vertical)?,
            4 | 5 => {
                if let Some(key) = key(a) {
                    if action == 4 {
                        input.key(key, Direction::Press)?;
                        self.keys.insert(a);
                    } else if self.keys.remove(&a) {
                        input.key(key, Direction::Release)?;
                    }
                }
            }
            _ => unreachable!(),
        }
        Ok(())
    }
    fn release(&mut self) {
        if let Some(input) = self.input.as_mut() {
            for vk in self.keys.drain() {
                if let Some(key) = key(vk) {
                    let _ = input.key(key, Direction::Release);
                }
            }
            for b in self.buttons.drain() {
                let _ = input.button(
                    match b {
                        0 => Button::Left,
                        1 => Button::Right,
                        _ => Button::Middle,
                    },
                    Direction::Release,
                );
            }
        }
    }
    fn clipboard_available(&self) -> bool {
        self.clipboard.is_some()
    }
    fn clipboard_read(&mut self) -> Result<String> {
        Ok(self
            .clipboard
            .as_mut()
            .ok_or_else(|| anyhow::anyhow!("Clipboard is unavailable"))?
            .get_text()?)
    }
    fn clipboard_write(&mut self, text: String) -> Result<()> {
        self.clipboard
            .as_mut()
            .ok_or_else(|| anyhow::anyhow!("Clipboard is unavailable"))?
            .set_text(text)?;
        Ok(())
    }
}
impl Drop for NativeDesktop {
    fn drop(&mut self) {
        self.release();
    }
}
fn metadata(monitor: &xcap::Monitor, selected: u32) -> Result<Monitor> {
    let id = monitor.id()?;
    let frequency = monitor.frequency()?;
    let name = monitor
        .friendly_name()
        .or_else(|_| monitor.name())
        .ok()
        .filter(|name| !name.is_empty())
        .unwrap_or_else(|| format!("Display {id}"));
    let display = Monitor {
        id: id.to_string(),
        name,
        x: monitor.x()?,
        y: monitor.y()?,
        width: monitor.width()?,
        height: monitor.height()?,
        refresh: if frequency.is_finite() && frequency >= 1.0 {
            (frequency.round() as u32).clamp(1, 1000)
        } else {
            60
        },
        selected: selected == id,
    };
    display.validate()?;
    Ok(display)
}
fn key(v: i32) -> Option<Key> {
    Some(match v {
        8 => Key::Backspace,
        9 => Key::Tab,
        13 => Key::Return,
        16 => Key::Shift,
        17 => Key::Control,
        18 => Key::Alt,
        20 => Key::CapsLock,
        27 => Key::Escape,
        32 => Key::Space,
        33 => Key::PageUp,
        34 => Key::PageDown,
        35 => Key::End,
        36 => Key::Home,
        37 => Key::LeftArrow,
        38 => Key::UpArrow,
        39 => Key::RightArrow,
        40 => Key::DownArrow,
        45 => Key::Insert,
        46 => Key::Delete,
        91 | 92 => Key::Meta,
        112 => Key::F1,
        113 => Key::F2,
        114 => Key::F3,
        115 => Key::F4,
        116 => Key::F5,
        117 => Key::F6,
        118 => Key::F7,
        119 => Key::F8,
        120 => Key::F9,
        121 => Key::F10,
        122 => Key::F11,
        123 => Key::F12,
        48..=57 => Key::Unicode(char::from_u32(v as u32)?),
        65..=90 => Key::Unicode(char::from_u32(v as u32 + 32)?),
        _ => return None,
    })
}
#[cfg(target_os = "macos")]
#[link(name = "CoreGraphics", kind = "framework")]
unsafe extern "C" {
    fn CGPreflightScreenCaptureAccess() -> bool;
    fn CGRequestScreenCaptureAccess() -> bool;
}

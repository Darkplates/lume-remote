#![cfg(all(target_os = "linux", target_pointer_width = "64"))]
#[path = "../src/audio.rs"]
mod audio;
#[path = "../src/native.rs"]
mod native;
use std::{
    ffi::{c_char, c_int, c_long, c_uint, c_ulong, c_void},
    sync::Mutex,
    time::{Duration, Instant},
};
static OWNED_DISPLAY: Mutex<()> = Mutex::new(());
#[repr(C, align(8))]
struct Event([u8; 192]);
#[link(name = "X11")]
unsafe extern "C" {
    fn XOpenDisplay(name: *const c_char) -> *mut c_void;
    fn XDefaultRootWindow(display: *mut c_void) -> c_ulong;
    fn XCreateSimpleWindow(
        display: *mut c_void,
        parent: c_ulong,
        x: c_int,
        y: c_int,
        w: c_uint,
        h: c_uint,
        border: c_uint,
        border_colour: c_ulong,
        background: c_ulong,
    ) -> c_ulong;
    fn XSelectInput(display: *mut c_void, window: c_ulong, mask: c_long) -> c_int;
    fn XMapRaised(display: *mut c_void, window: c_ulong) -> c_int;
    fn XSetInputFocus(display: *mut c_void, window: c_ulong, revert: c_int, time: c_ulong)
    -> c_int;
    fn XSync(display: *mut c_void, discard: c_int) -> c_int;
    fn XPending(display: *mut c_void) -> c_int;
    fn XNextEvent(display: *mut c_void, event: *mut Event) -> c_int;
    fn XDestroyWindow(display: *mut c_void, window: c_ulong) -> c_int;
    fn XCloseDisplay(display: *mut c_void) -> c_int;
    fn XQueryTree(
        display: *mut c_void,
        window: c_ulong,
        root: *mut c_ulong,
        parent: *mut c_ulong,
        children: *mut *mut c_ulong,
        count: *mut c_uint,
    ) -> c_int;
    fn XFetchName(display: *mut c_void, window: c_ulong, name: *mut *mut c_char) -> c_int;
    fn XFree(data: *mut c_void) -> c_int;
}
struct Window(*mut c_void, c_ulong);
impl Drop for Window {
    fn drop(&mut self) {
        unsafe {
            XDestroyWindow(self.0, self.1);
            XCloseDisplay(self.0);
        }
    }
}
#[test]
#[ignore = "requires an isolated Xvfb display and LUME_OWNED_X11=1"]
fn x11_owned_capture_input_release_and_clipboard() {
    assert_eq!(std::env::var("LUME_OWNED_X11").unwrap(), "1");
    let _guard = OWNED_DISPLAY.lock().unwrap();
    unsafe {
        let display = XOpenDisplay(std::ptr::null());
        assert!(!display.is_null());
        let window = Window(
            display,
            XCreateSimpleWindow(
                display,
                XDefaultRootWindow(display),
                100,
                100,
                400,
                300,
                0,
                0,
                0x4678be,
            ),
        );
        XSelectInput(display, window.1, 1 | 2 | 4 | 8);
        XMapRaised(display, window.1);
        XSync(display, 0);
        XSetInputFocus(display, window.1, 1, 0);
        XSync(display, 0);
        let mut desktop = native::NativeDesktop::open(true).unwrap();
        let monitors = desktop.monitors().unwrap();
        assert!(desktop.supports_monitors());
        let current = monitors.iter().find(|m| m.selected).unwrap();
        let original_size = desktop.size();
        assert!(desktop.select_monitor("missing-owned-display").is_err());
        assert_eq!(desktop.size(), original_size);
        desktop.select_monitor(&current.id).unwrap();
        assert_eq!(desktop.size(), original_size);
        let frame = desktop.capture().unwrap();
        assert_eq!(frame.get_pixel(110, 110).0, [70, 120, 190, 255]);
        let (w, h, _) = desktop.size();
        desktop
            .input(
                0,
                (150u64 * 65535 / (w - 1) as u64) as i32,
                (150u64 * 65535 / (h - 1) as u64) as i32,
            )
            .unwrap();
        desktop.input(1, 0, 0).unwrap();
        desktop.input(4, 65, 0).unwrap();
        // Switching also releases held native keys/buttons, including same-display selection.
        desktop.select_monitor(&current.id).unwrap();
        let mut seen = [false; 4];
        let start = Instant::now();
        while !seen.iter().all(|v| *v) {
            assert!(
                start.elapsed() < Duration::from_secs(5),
                "Owned X11 events missing: {seen:?}"
            );
            while XPending(display) > 0 {
                let mut event = Event([0; 192]);
                XNextEvent(display, &mut event);
                let kind = i32::from_ne_bytes(event.0[..4].try_into().unwrap());
                let target = u64::from_ne_bytes(event.0[32..40].try_into().unwrap());
                if target == window.1 && (2..=5).contains(&kind) {
                    seen[(kind - 2) as usize] = true;
                }
            }
            std::thread::sleep(Duration::from_millis(10));
        }
        assert!(desktop.clipboard_available());
        desktop
            .clipboard_write("Lume owned X11 clipboard — café 👋".into())
            .unwrap();
        let mut second = arboard::Clipboard::new().unwrap();
        assert_eq!(
            second.get_text().unwrap(),
            "Lume owned X11 clipboard — café 👋"
        );
        second.set_text("Reverse clipboard".to_string()).unwrap();
        assert_eq!(desktop.clipboard_read().unwrap(), "Reverse clipboard");
        println!(
            "PASS Native Linux X11 capture, owned keyboard/mouse events, held-input release and bidirectional Unicode clipboard. Isolated Xvfb only."
        );
    }
}

#[test]
#[ignore = "requires an isolated Xvfb display and LUME_OWNED_X11=1"]
fn native_desktop_window_renders_on_owned_x11() {
    assert_eq!(std::env::var("LUME_OWNED_X11").unwrap(), "1");
    let _guard = OWNED_DISPLAY.lock().unwrap();
    struct Child(std::process::Child);
    impl Drop for Child {
        fn drop(&mut self) {
            let _ = self.0.kill();
            let _ = self.0.wait();
        }
    }
    struct Display(*mut c_void);
    impl Drop for Display {
        fn drop(&mut self) {
            unsafe {
                XCloseDisplay(self.0);
            }
        }
    }
    let binary = std::env::var_os("LUME_DESKTOP_TEST_BINARY")
        .unwrap_or_else(|| env!("CARGO_BIN_EXE_lume-desktop").into());
    let mut child = Child(std::process::Command::new(binary).spawn().unwrap());
    let display = Display(unsafe { XOpenDisplay(std::ptr::null()) });
    assert!(!display.0.is_null());
    let mut desktop = native::NativeDesktop::open(false).unwrap();
    let started = Instant::now();
    let mut complete_frames = 0;
    loop {
        assert!(
            child.0.try_wait().unwrap().is_none(),
            "Native desktop exited before rendering"
        );
        assert!(
            started.elapsed() < Duration::from_secs(45),
            "Native desktop did not render its owned X11 window"
        );
        let found = unsafe {
            let (mut root, mut parent, mut children, mut count) = (0, 0, std::ptr::null_mut(), 0);
            let mut found = false;
            if XQueryTree(
                display.0,
                XDefaultRootWindow(display.0),
                &mut root,
                &mut parent,
                &mut children,
                &mut count,
            ) != 0
            {
                if !children.is_null() {
                    for window in std::slice::from_raw_parts(children, count as usize) {
                        let mut name = std::ptr::null_mut();
                        if XFetchName(display.0, *window, &mut name) != 0 && !name.is_null() {
                            found |= std::ffi::CStr::from_ptr(name).to_bytes() == b"Lume";
                            XFree(name.cast());
                        }
                    }
                    XFree(children.cast());
                }
            }
            found
        };
        if found {
            let image = desktop.capture().unwrap();
            let brand_pixels = image
                .pixels()
                .filter(|p| p[0] >= 70 && p[0] <= 140 && p[1] >= 180 && p[2] >= 150)
                .count();
            // A title alone does not establish that the controls are visible.
            // Require both action sections on consecutive captures.
            let mut upper_text = 0;
            let mut lower_text = 0;
            for (x, y, pixel) in image.enumerate_pixels() {
                let neutral_text = x > 8
                    && x < 680
                    && pixel[0] >= 90
                    && pixel[0] <= 230
                    && pixel[0].abs_diff(pixel[1]) < 8
                    && pixel[1].abs_diff(pixel[2]) < 8;
                if neutral_text && (100..220).contains(&y) {
                    upper_text += 1;
                }
                if neutral_text && (250..400).contains(&y) {
                    lower_text += 1;
                }
            }
            if brand_pixels > 20 && upper_text > 200 && lower_text > 200 {
                complete_frames += 1;
            } else {
                complete_frames = 0;
            }
            if complete_frames >= 2 {
                if let Some(path) = std::env::var_os("LUME_X11_CAPTURE_PATH") {
                    assert!(
                        !std::path::Path::new(&path).exists(),
                        "Preserve previous UI evidence"
                    );
                    image.save(path).unwrap();
                }
                println!(
                    "PASS Native Linux desktop window, brand and both action sections rendered on consecutive captures. Owned Xvfb display only."
                );
                break;
            }
        }
        std::thread::sleep(Duration::from_millis(100));
    }
}

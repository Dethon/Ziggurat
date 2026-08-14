//! The tray icon, its menu, and the balloon the person is told things through.
//!
//! No visible window is ever created. A window that can take focus can break injection into the
//! window underneath, which is why the tray is the entire interface; the message-only window this
//! needs is never shown, never painted and cannot be focused.

use windows::core::{w, HSTRING, PCWSTR};
use windows::Win32::Foundation::{HWND, POINT};
use windows::Win32::Graphics::Gdi::{CreateBitmap, DeleteObject, HBITMAP, HGDIOBJ};
use windows::Win32::UI::Shell::{
    Shell_NotifyIconW, NIF_ICON, NIF_INFO, NIF_MESSAGE, NIF_TIP, NIIF_INFO, NIIF_WARNING,
    NIM_ADD, NIM_DELETE, NIM_MODIFY, NOTIFYICONDATAW,
};
use windows::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CreateIconIndirect, CreatePopupMenu, DestroyIcon, DestroyMenu, GetCursorPos,
    SetForegroundWindow, TrackPopupMenu, HICON, ICONINFO, MF_CHECKED, MF_GRAYED,
    MF_POPUP, MF_SEPARATOR, MF_STRING, MF_UNCHECKED, TPM_BOTTOMALIGN, TPM_RIGHTALIGN,
};

use crate::host::TrayState;

/// The message the shell posts back to the window for mouse activity on the icon.
pub const WM_TRAY: u32 = windows::Win32::UI::WindowsAndMessaging::WM_APP + 1;

pub const ID_QUIT: usize = 3_000;
pub const ID_AUTOSTART: usize = 2_000;
/// One per binding, offset by its index.
pub const ID_LEARN_FIRST: usize = 1_000;
/// One per capture device, offset by its index. They do nothing: the list is there to be read.
pub const ID_DEVICE_FIRST: usize = 4_000;

pub struct Tray {
    hwnd: HWND,
    icons: [HICON; 4],
    state: TrayState,
}

impl Tray {
    pub fn new(hwnd: HWND) -> windows::core::Result<Self> {
        let icons = [
            disc((0x90, 0x90, 0x90), false)?, // idle
            disc((0xE0, 0x30, 0x30), false)?, // recording
            disc((0xE0, 0xA0, 0x20), false)?, // transcribing
            disc((0xC0, 0x10, 0x10), true)?,  // error: a ring, so it differs by shape as well
        ];
        let tray = Self { hwnd, icons, state: TrayState::Idle };
        let mut data = tray.data();
        data.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
        data.uCallbackMessage = WM_TRAY;
        data.hIcon = tray.icons[0];
        write(&mut data.szTip, "speech typist — idle");
        unsafe { Shell_NotifyIconW(NIM_ADD, &data) }.ok()?;
        Ok(tray)
    }

    pub fn set_state(&mut self, state: TrayState) {
        if state == self.state {
            return;
        }
        self.state = state;
        let (index, tip) = match state {
            TrayState::Idle => (0, "speech typist — idle"),
            TrayState::Recording => (1, "speech typist — recording"),
            TrayState::Transcribing => (2, "speech typist — transcribing"),
            TrayState::Error => (3, "speech typist — Lemonade is not answering"),
        };
        let mut data = self.data();
        data.uFlags = NIF_ICON | NIF_TIP;
        data.hIcon = self.icons[index];
        write(&mut data.szTip, tip);
        unsafe {
            let _ = Shell_NotifyIconW(NIM_MODIFY, &data);
        }
    }

    pub fn notify(&self, message: &str) {
        let mut data = self.data();
        data.uFlags = NIF_INFO;
        data.dwInfoFlags = if self.state == TrayState::Error { NIIF_WARNING } else { NIIF_INFO };
        write(&mut data.szInfoTitle, "speech typist");
        write(&mut data.szInfo, message);
        unsafe {
            let _ = Shell_NotifyIconW(NIM_MODIFY, &data);
        }
    }

    fn data(&self) -> NOTIFYICONDATAW {
        NOTIFYICONDATAW {
            cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: self.hwnd,
            uID: 1,
            ..Default::default()
        }
    }
}

impl Drop for Tray {
    fn drop(&mut self) {
        unsafe {
            let _ = Shell_NotifyIconW(NIM_DELETE, &self.data());
            for icon in self.icons {
                let _ = DestroyIcon(icon);
            }
        }
    }
}

/// The menu, rebuilt on every right-click so the autostart tick and the device list are whatever
/// they are right now rather than whatever they were at startup.
pub fn show_menu(hwnd: HWND, languages: &[String], devices: &[String], autostart: bool) {
    unsafe {
        let Ok(menu) = CreatePopupMenu() else {
            return;
        };
        let Ok(bindings) = CreatePopupMenu() else {
            let _ = DestroyMenu(menu);
            return;
        };
        for (index, language) in languages.iter().enumerate() {
            let label = HSTRING::from(format!("{language} — press a key"));
            let _ = AppendMenuW(bindings, MF_STRING, ID_LEARN_FIRST + index, &label);
        }
        let _ = AppendMenuW(menu, MF_POPUP, bindings.0 as usize, w!("Set binding"));

        if !devices.is_empty() {
            let Ok(list) = CreatePopupMenu() else {
                let _ = DestroyMenu(menu);
                return;
            };
            for (index, device) in devices.iter().enumerate() {
                let label = HSTRING::from(device.as_str());
                // Greyed on purpose: this is a list to read, not a switch. The name goes into
                // audio.device_name in the config, which is what actually chooses.
                let _ = AppendMenuW(list, MF_STRING | MF_GRAYED, ID_DEVICE_FIRST + index, &label);
            }
            let _ = AppendMenuW(menu, MF_POPUP, list.0 as usize, w!("Microphones"));
        }

        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
        let tick = if autostart { MF_CHECKED } else { MF_UNCHECKED };
        let _ = AppendMenuW(menu, MF_STRING | tick, ID_AUTOSTART, w!("Start with Windows"));
        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
        let _ = AppendMenuW(menu, MF_STRING, ID_QUIT, w!("Quit"));

        let mut at = POINT::default();
        let _ = GetCursorPos(&mut at);
        // Without this the menu does not dismiss when the person clicks elsewhere — a documented
        // quirk of tray menus, and the reason this call looks gratuitous.
        let _ = SetForegroundWindow(hwnd);
        let _ = TrackPopupMenu(menu, TPM_RIGHTALIGN | TPM_BOTTOMALIGN, at.x, at.y, 0, hwnd, None);
        let _ = DestroyMenu(menu);
    }
}

/// A 16x16 icon: a filled disc, or a ring when the state should differ by shape and not only by
/// colour.
fn disc(rgb: (u8, u8, u8), ring: bool) -> windows::core::Result<HICON> {
    const SIZE: i32 = 16;
    let (r, g, b) = rgb;
    let centre = (SIZE - 1) as f32 / 2.0;
    let pixels: Vec<u8> = (0..SIZE)
        .flat_map(|y| (0..SIZE).map(move |x| (x, y)))
        .flat_map(|(x, y)| {
            let distance = (((x as f32 - centre).powi(2)) + ((y as f32 - centre).powi(2))).sqrt();
            let inside = distance <= 7.0 && (!ring || distance >= 4.0);
            // Pre-multiplied BGRA, which is what a 32-bit icon bitmap is read as.
            if inside {
                [b, g, r, 0xFF]
            } else {
                [0, 0, 0, 0]
            }
        })
        .collect();

    unsafe {
        let colour: HBITMAP =
            CreateBitmap(SIZE, SIZE, 1, 32, Some(pixels.as_ptr() as *const std::ffi::c_void));
        // An all-zero mask means "take the transparency from the colour bitmap's alpha".
        let mask: HBITMAP = CreateBitmap(SIZE, SIZE, 1, 1, None);
        let info = ICONINFO {
            fIcon: true.into(),
            xHotspot: 0,
            yHotspot: 0,
            hbmMask: mask,
            hbmColor: colour,
        };
        let icon = CreateIconIndirect(&info);
        let _ = DeleteObject(HGDIOBJ(colour.0));
        let _ = DeleteObject(HGDIOBJ(mask.0));
        icon
    }
}

/// The shell's fields are fixed-size wide arrays, and an over-long message is truncated rather
/// than refused.
fn write(field: &mut [u16], text: &str) {
    let units: Vec<u16> = text.encode_utf16().take(field.len() - 1).collect();
    field.fill(0);
    field[..units.len()].copy_from_slice(&units);
}

//! The tray icon, its menu, and the balloon the person is told things through.
//!
//! No visible window is ever created. A window that can take focus can break injection into the
//! window underneath, which is why the tray is the entire interface; the message-only window this
//! needs is never shown, never painted and cannot be focused.

use windows::core::{w, HSTRING, PCWSTR};
use windows::Win32::Foundation::{HWND, POINT};
use windows::Win32::UI::Shell::{
    Shell_NotifyIconW, NIF_ICON, NIF_INFO, NIF_MESSAGE, NIF_TIP, NIIF_INFO, NIIF_WARNING,
    NIM_ADD, NIM_DELETE, NIM_MODIFY, NOTIFYICONDATAW,
};
use windows::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CreateIconFromResourceEx, CreatePopupMenu, DestroyIcon, DestroyMenu,
    GetCursorPos, GetSystemMetrics, SetForegroundWindow, TrackPopupMenu, HICON, LR_DEFAULTCOLOR,
    MF_CHECKED, MF_GRAYED, MF_POPUP, MF_SEPARATOR, MF_STRING, MF_UNCHECKED, SM_CXSMICON,
    TPM_BOTTOMALIGN, TPM_RIGHTALIGN,
};

use crate::host::TrayState;
use crate::icon::{self, Art};

/// The message the shell posts back to the window for mouse activity on the icon.
pub const WM_TRAY: u32 = windows::Win32::UI::WindowsAndMessaging::WM_APP + 1;

pub const ID_QUIT: usize = 3_000;
pub const ID_AUTOSTART: usize = 2_000;
pub const ID_LATCH: usize = 2_001;
/// One per binding, offset by its index. Bounded by [`ID_LEARN_LAST`] rather than by wherever the
/// next block of ids happens to start.
pub const ID_LEARN_FIRST: usize = 1_000;
pub const ID_LEARN_LAST: usize = 1_999;
/// One per capture device, offset by its index. They do nothing: the list is there to be read.
pub const ID_DEVICE_FIRST: usize = 4_000;

pub struct Tray {
    hwnd: HWND,
    icons: [HICON; 4],
    state: TrayState,
}

impl Tray {
    pub fn new(hwnd: HWND) -> windows::core::Result<Self> {
        let tray = Self { hwnd, icons: draw()?, state: TrayState::Idle };
        tray.add()?;
        Ok(tray)
    }

    /// Puts the icon back after the taskbar was recreated — Explorer restarting, which also
    /// happens on some display changes. The shell forgets every icon it had and only says so by
    /// broadcast; without this the speech typist keeps running with no icon and no way to quit it.
    /// The icons are drawn again too, because the size the shell wants may be what changed.
    pub fn restore(&mut self) {
        if let Ok(icons) = draw() {
            destroy(std::mem::replace(&mut self.icons, icons));
        }
        let _ = self.add();
    }

    fn add(&self) -> windows::core::Result<()> {
        let mut data = self.data();
        data.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
        data.uCallbackMessage = WM_TRAY;
        data.hIcon = self.icons[index(self.state)];
        write(&mut data.szTip, tip(self.state));
        unsafe { Shell_NotifyIconW(NIM_ADD, &data) }.ok()
    }

    pub fn set_state(&mut self, state: TrayState) {
        if state == self.state {
            return;
        }
        self.state = state;
        let mut data = self.data();
        data.uFlags = NIF_ICON | NIF_TIP;
        data.hIcon = self.icons[index(state)];
        write(&mut data.szTip, tip(state));
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
        }
        destroy(self.icons);
    }
}

/// The menu, rebuilt on every right-click so the autostart tick and the device list are whatever
/// they are right now rather than whatever they were at startup.
pub fn show_menu(
    hwnd: HWND,
    languages: &[String],
    devices: &[String],
    autostart: bool,
    latched: bool,
) {
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
        // Ticked means a press starts the dictation and another ends it; unticked means the key
        // is held throughout. Both are the same dictation, so this is one item and not two.
        let latch_tick = if latched { MF_CHECKED } else { MF_UNCHECKED };
        let _ = AppendMenuW(menu, MF_STRING | latch_tick, ID_LATCH, w!("Latched dictation"));
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

const STATES: [TrayState; 4] =
    [TrayState::Idle, TrayState::Recording, TrayState::Transcribing, TrayState::Error];

fn index(state: TrayState) -> usize {
    STATES.iter().position(|&s| s == state).expect("every state is listed")
}

fn tip(state: TrayState) -> &'static str {
    match state {
        TrayState::Idle => "speech typist — idle",
        TrayState::Recording => "speech typist — recording",
        TrayState::Transcribing => "speech typist — transcribing",
        TrayState::Error => "speech typist — Lemonade is not answering",
    }
}

/// One icon per state, at the size the shell shows tray icons at on this screen. The process is
/// DPI-aware, so that is 20 or 24 pixels at 125 or 150 per cent rather than a 16-pixel icon the
/// shell stretches.
fn draw() -> windows::core::Result<[HICON; 4]> {
    let size = (unsafe { GetSystemMetrics(SM_CXSMICON) } as u32).clamp(16, 256);
    let mut icons = [HICON::default(); 4];
    for (slot, state) in icons.iter_mut().zip(STATES) {
        let art = match state {
            TrayState::Idle => Art::Idle,
            TrayState::Recording => Art::Recording,
            TrayState::Transcribing => Art::Transcribing,
            TrayState::Error => Art::Failing,
        };
        let image = icon::icon_image(art, size);
        // 0x00030000 is the only version the call accepts: the 32-bit icon format.
        match unsafe {
            CreateIconFromResourceEx(&image, true, 0x0003_0000, size as i32, size as i32, LR_DEFAULTCOLOR)
        } {
            Ok(handle) => *slot = handle,
            Err(error) => {
                destroy(icons);
                return Err(error);
            }
        }
    }
    Ok(icons)
}

fn destroy(icons: [HICON; 4]) {
    for icon in icons.into_iter().filter(|icon| !icon.is_invalid()) {
        unsafe {
            let _ = DestroyIcon(icon);
        }
    }
}

/// The shell's fields are fixed-size wide arrays, and an over-long message is truncated rather
/// than refused.
fn write(field: &mut [u16], text: &str) {
    let units: Vec<u16> = text.encode_utf16().take(field.len() - 1).collect();
    field.fill(0);
    field[..units.len()].copy_from_slice(&units);
}

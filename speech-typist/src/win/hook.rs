//! The low-level keyboard hook.
//!
//! `RegisterHotKey` reports only key-down and therefore cannot express holding, which is the whole
//! interaction — so this is `WH_KEYBOARD_LL` instead. What it does with each key is
//! [`KeySwitch`], which is platform-free and tested; this file is the plumbing around it and is
//! verified by hand.

use std::sync::{Mutex, OnceLock};

use tokio::sync::mpsc::Sender;
use windows::Win32::Foundation::{HINSTANCE, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, SetWindowsHookExW, HHOOK, KBDLLHOOKSTRUCT, WH_KEYBOARD_LL, WM_KEYDOWN,
    WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP,
};

use crate::host::{HostEvent, KeyCode};
use crate::keys::{Decision, KeySwitch};

struct Installed {
    switch: KeySwitch,
    events: Option<Sender<HostEvent>>,
}

static STATE: OnceLock<Mutex<Installed>> = OnceLock::new();

fn state() -> &'static Mutex<Installed> {
    STATE.get_or_init(|| Mutex::new(Installed { switch: KeySwitch::default(), events: None }))
}

/// Installs the hook on the calling thread, which must be the one running the message loop.
pub fn install(events: Sender<HostEvent>) -> windows::core::Result<HHOOK> {
    state().lock().unwrap().events = Some(events);
    let module = unsafe { GetModuleHandleW(None) }?;
    unsafe { SetWindowsHookExW(WH_KEYBOARD_LL, Some(callback), HINSTANCE(module.0), 0) }
}

pub fn set_bindings(keys: &[KeyCode]) {
    state().lock().unwrap().switch.set_bindings(keys);
}

pub fn learn(binding: usize) {
    state().lock().unwrap().switch.learn(binding);
}

unsafe extern "system" fn callback(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if code < 0 {
        return unsafe { CallNextHookEx(None, code, wparam, lparam) };
    }
    let key = KeyCode(unsafe { *(lparam.0 as *const KBDLLHOOKSTRUCT) }.vkCode);
    let message = wparam.0 as u32;
    let down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
    let up = message == WM_KEYUP || message == WM_SYSKEYUP;

    let (decision, events) = {
        let mut state = state().lock().unwrap();
        (state.switch.on_key(key, down, up), state.events.clone())
    };

    match decision {
        Decision::Pass => unsafe { CallNextHookEx(None, code, wparam, lparam) },
        Decision::Swallow(event) => {
            // The callback must return promptly — Windows silently removes a hook that takes too
            // long — so a full channel drops the event rather than blocking the whole keyboard.
            if let (Some(event), Some(events)) = (event, events) {
                let _ = events.try_send(event);
            }
            // Non-zero: the key never reaches the window in front.
            LRESULT(1)
        }
    }
}

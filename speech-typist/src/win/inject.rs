//! Making the words arrive as though they had been typed.

use windows::Win32::Foundation::{HANDLE, HWND};
use windows::Win32::System::DataExchange::{
    CloseClipboard, EmptyClipboard, GetClipboardData, OpenClipboard, SetClipboardData,
};
use windows::Win32::System::Memory::{GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE};
use windows::Win32::System::Ole::CF_UNICODETEXT;
use windows::Win32::UI::Input::KeyboardAndMouse::{
    SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_KEYUP, KEYEVENTF_UNICODE,
    VIRTUAL_KEY, VK_CONTROL, VK_LCONTROL, VK_LMENU, VK_LSHIFT, VK_LWIN, VK_MENU, VK_RCONTROL,
    VK_RMENU, VK_RSHIFT, VK_RWIN, VK_SHIFT, VK_V,
};

use crate::host::{HostError, Injection, InjectionMethod, KeyCode};

/// Types one transcript, releasing the binding's key first if that key is itself a modifier.
pub fn inject(injection: Injection<'_>) -> Result<(), HostError> {
    let modifier = injection.held.and_then(as_modifier);
    if let Some(modifier) = modifier {
        send(&[key_event(modifier, true)])?;
    }
    let result = match injection.method {
        InjectionMethod::Keys => type_unicode(injection.text),
        InjectionMethod::ClipboardPaste => paste(injection.text),
    };
    // Restored whatever happened above: leaving a modifier logically up while the person is still
    // physically holding it is worse than the failure that got us here.
    if let Some(modifier) = modifier {
        let _ = send(&[key_event(modifier, false)]);
    }
    result
}

/// A binding that is itself a modifier would chord every character it typed. Which keys those are
/// is Windows' answer, which is why the core hands over the key rather than the judgement.
fn as_modifier(key: KeyCode) -> Option<VIRTUAL_KEY> {
    let key = VIRTUAL_KEY(key.0 as u16);
    let modifiers = [
        VK_SHIFT, VK_LSHIFT, VK_RSHIFT, VK_CONTROL, VK_LCONTROL, VK_RCONTROL, VK_MENU, VK_LMENU,
        VK_RMENU, VK_LWIN, VK_RWIN,
    ];
    modifiers.contains(&key).then_some(key)
}

/// One `SendInput` call for the whole transcript: batching is what keeps a paragraph from
/// arriving character by character in an application that redraws on every keystroke.
fn type_unicode(text: &str) -> Result<(), HostError> {
    let events: Vec<INPUT> = text
        .encode_utf16()
        .flat_map(|unit| [unicode_event(unit, false), unicode_event(unit, true)])
        .collect();
    if events.is_empty() {
        return Ok(());
    }
    send(&events)
}

fn unicode_event(unit: u16, up: bool) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: VIRTUAL_KEY(0),
                wScan: unit,
                dwFlags: if up {
                    KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                } else {
                    KEYEVENTF_UNICODE
                },
                time: 0,
                dwExtraInfo: 0,
            },
        },
    }
}

fn key_event(key: VIRTUAL_KEY, up: bool) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: key,
                wScan: 0,
                dwFlags: if up { KEYEVENTF_KEYUP } else { Default::default() },
                time: 0,
                dwExtraInfo: 0,
            },
        },
    }
}

fn send(events: &[INPUT]) -> Result<(), HostError> {
    let sent = unsafe { SendInput(events, std::mem::size_of::<INPUT>() as i32) };
    if sent as usize == events.len() {
        Ok(())
    } else {
        Err(HostError(format!("only {sent} of {} key events were accepted", events.len())))
    }
}

/// The escape hatch for applications that mishandle synthetic key events. The previous clipboard
/// contents are put back, because a dictation must not cost someone what they had copied.
fn paste(text: &str) -> Result<(), HostError> {
    let previous = read_clipboard();
    write_clipboard(text)?;
    let result = send(&[
        key_event(VK_CONTROL, false),
        key_event(VK_V, false),
        key_event(VK_V, true),
        key_event(VK_CONTROL, true),
    ]);
    if let Some(previous) = previous {
        let _ = write_clipboard(&previous);
    }
    result
}

fn read_clipboard() -> Option<String> {
    unsafe {
        OpenClipboard(HWND::default()).ok()?;
        let handle = GetClipboardData(CF_UNICODETEXT.0 as u32).ok();
        let text = handle.and_then(|handle| {
            let pointer = GlobalLock(windows::Win32::Foundation::HGLOBAL(handle.0)) as *const u16;
            if pointer.is_null() {
                return None;
            }
            let mut units = Vec::new();
            let mut at = 0;
            while *pointer.add(at) != 0 {
                units.push(*pointer.add(at));
                at += 1;
            }
            let _ = GlobalUnlock(windows::Win32::Foundation::HGLOBAL(handle.0));
            String::from_utf16(&units).ok()
        });
        let _ = CloseClipboard();
        text
    }
}

fn write_clipboard(text: &str) -> Result<(), HostError> {
    let mut units: Vec<u16> = text.encode_utf16().collect();
    units.push(0);
    unsafe {
        OpenClipboard(HWND::default()).map_err(|e| HostError(format!("clipboard: {e}")))?;
        let result = (|| {
            EmptyClipboard().map_err(|e| HostError(format!("clipboard: {e}")))?;
            let bytes = units.len() * 2;
            let memory = GlobalAlloc(GMEM_MOVEABLE, bytes)
                .map_err(|e| HostError(format!("clipboard: {e}")))?;
            let pointer = GlobalLock(memory) as *mut u16;
            if pointer.is_null() {
                return Err(HostError("clipboard: could not lock the buffer".into()));
            }
            std::ptr::copy_nonoverlapping(units.as_ptr(), pointer, units.len());
            let _ = GlobalUnlock(memory);
            SetClipboardData(CF_UNICODETEXT.0 as u32, HANDLE(memory.0))
                .map_err(|e| HostError(format!("clipboard: {e}")))?;
            Ok(())
        })();
        let _ = CloseClipboard();
        result
    }
}

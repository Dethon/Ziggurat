//! Starting with Windows, through the current user's Run key.
//!
//! Off by default: installing this must change nothing about the machine until it is asked to,
//! and turning it off removes exactly what turning it on added.

use windows::core::{w, HSTRING, PCWSTR};
use windows::Win32::System::Registry::{
    RegCloseKey, RegDeleteValueW, RegOpenKeyExW, RegQueryValueExW, RegSetValueExW, HKEY,
    HKEY_CURRENT_USER, KEY_READ, KEY_WRITE, REG_SZ,
};

const RUN_KEY: PCWSTR = w!("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
const VALUE: PCWSTR = w!("SpeechTypist");

pub fn is_on() -> bool {
    let Ok(key) = open(KEY_READ) else {
        return false;
    };
    let mut size = 0u32;
    let present =
        unsafe { RegQueryValueExW(key, VALUE, None, None, None, Some(&mut size)) }.is_ok();
    unsafe { let _ = RegCloseKey(key); }
    present
}

pub fn set(on: bool) -> windows::core::Result<()> {
    let key = open(KEY_WRITE)?;
    let result = if on {
        let exe = std::env::current_exe().unwrap_or_default();
        // Quoted, because a path under "Program Files" is otherwise read as two arguments.
        let command = HSTRING::from(format!("\"{}\"", exe.display()));
        let bytes = command_bytes(&command);
        unsafe { RegSetValueExW(key, VALUE, 0, REG_SZ, Some(&bytes)) }.ok()
    } else {
        // A value that was never there is not an error: turning it off has to be idempotent.
        let _ = unsafe { RegDeleteValueW(key, VALUE) };
        Ok(())
    };
    unsafe { let _ = RegCloseKey(key); }
    result
}

fn open(access: windows::Win32::System::Registry::REG_SAM_FLAGS) -> windows::core::Result<HKEY> {
    let mut key = HKEY::default();
    unsafe { RegOpenKeyExW(HKEY_CURRENT_USER, RUN_KEY, 0, access, &mut key) }.ok()?;
    Ok(key)
}

/// REG_SZ wants the bytes of a null-terminated wide string.
fn command_bytes(command: &HSTRING) -> Vec<u8> {
    command
        .as_wide()
        .iter()
        .copied()
        .chain(std::iter::once(0))
        .flat_map(u16::to_le_bytes)
        .collect()
}

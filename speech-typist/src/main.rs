//! The speech typist. Windows only: see `CLAUDE.md` for why the port trait is where a second
//! platform would go, and why none is written.
//!
//! No console subsystem, so nothing appears on screen at any point. The tray is the entire
//! interface, and a window that can take focus can break injection into the window underneath.
#![cfg_attr(windows, windows_subsystem = "windows")]

#[cfg(windows)]
fn main() -> anyhow::Result<()> {
    speech_typist::win::main()
}

#[cfg(not(windows))]
fn main() {
    eprintln!(
        "speech-typist runs on Windows. Everything but the host implementation is testable here: \
         run `cargo test`."
    );
    std::process::exit(1);
}

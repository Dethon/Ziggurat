//! The speech typist. Windows only: see `CLAUDE.md` for why the port trait is where a second
//! platform would go, and why none is written.

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

//! The speech typist: hold a key, talk, and the words appear in whatever window you were already
//! using.
//!
//! Everything outside `win` is platform-free and tested in WSL. `docs/adr/0026` records why this
//! posts at Lemonade directly rather than routing through the .NET stack, and `CLAUDE.md` holds
//! the invariants.

pub mod config;
pub mod core;
pub mod detector;
pub mod host;
pub mod lemonade;
pub mod wav;

#[cfg(test)]
mod testing;

#[cfg(windows)]
pub mod win;

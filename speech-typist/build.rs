//! Links the executable's icon in, drawn by the same code that draws the tray's.
//!
//! The icon goes in as a compiled resource file the linker reads directly, so the cross-compile
//! needs no resource compiler. Nothing happens for any other target: `cargo test` in WSL builds
//! for Linux and has no executable icon to give.

#[allow(dead_code)]
#[path = "src/icon.rs"]
mod icon;

fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-changed=src/icon.rs");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }
    let out = std::path::PathBuf::from(std::env::var("OUT_DIR").expect("cargo sets OUT_DIR"));
    let resources = out.join("icon.res");
    std::fs::write(
        &resources,
        icon::resource_file(icon::Art::App, &icon::APP_SIZES),
    )
    .expect("the icon resource file could not be written");
    println!("cargo:rustc-link-arg-bins={}", resources.display());
}

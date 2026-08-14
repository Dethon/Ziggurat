#!/usr/bin/env bash
set -euo pipefail
# One Windows executable, cross-compiled from WSL. Mirrors satellite/scripts/build-release.sh.
#
# cargo-xwin downloads the MSVC headers and import libraries on first use and caches them, so the
# first run is slow and every one after it is not. --locked because the committed Cargo.lock is
# what makes this reproducible; a silent lockfile rewrite must fail the release build rather than
# change what ships.
cd "$(dirname "$0")/.."
TARGET=x86_64-pc-windows-msvc

command -v cargo-xwin >/dev/null 2>&1 || {
    echo "error: cargo-xwin not on PATH (install: cargo install cargo-xwin --locked)" >&2
    exit 1
}
rustup target list --installed | grep -qx "$TARGET" || {
    echo "error: the $TARGET std is missing (install: rustup target add $TARGET)" >&2
    exit 1
}

cargo xwin build --locked --release --target "$TARGET"

EXE="target/$TARGET/release/speech-typist.exe"
ls -lh "$EXE"
# Every import must be a Windows system DLL. The static CRT in .cargo/config.toml is what keeps
# vcruntime140.dll off this list, and that DLL is not present on a clean machine.
echo "imports:"
strings -a "$EXE" | grep -io '[a-z0-9_-]*\.dll' | sort -u | sed 's/^/  /'

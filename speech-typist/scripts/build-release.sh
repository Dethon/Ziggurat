#!/usr/bin/env bash
set -euo pipefail
# One Windows executable, cross-compiled from WSL. Mirrors satellite/scripts/build-release.sh.
#
# cargo-xwin downloads the MSVC headers and import libraries on first use and caches them, so the
# first run is slow and every one after it is not. --locked because the committed Cargo.lock is
# what makes this reproducible; a silent lockfile rewrite must fail the release build rather than
# change what ships.

# The crate is found from this script's own location, resolved through any symlinks, so it can be
# run from anywhere and symlinked onto PATH.
CRATE=$(cd -- "$(dirname -- "$(readlink -f -- "${BASH_SOURCE[0]}")")/.." && pwd)
TARGET=x86_64-pc-windows-msvc
EXE="$CRATE/target/$TARGET/release/speech-typist.exe"

command -v cargo-xwin >/dev/null 2>&1 || {
    echo "error: cargo-xwin not on PATH (install: cargo install cargo-xwin --locked)" >&2
    exit 1
}
rustup target list --installed | grep -qx "$TARGET" || {
    echo "error: the $TARGET std is missing (install: rustup target add $TARGET)" >&2
    exit 1
}

# Must build from inside the crate rather than with --manifest-path: cargo reads
# .cargo/config.toml and rust-toolchain.toml from the working directory upward, never from the
# manifest's directory, and the static CRT that makes this one file lives in the former.
cd "$CRATE"
cargo xwin build --locked --release --target "$TARGET"

ls -lh "$EXE"
# Every import must be a Windows system DLL. The static CRT in .cargo/config.toml is what keeps
# vcruntime140.dll off this list, and that DLL is not present on a clean machine.
if command -v objdump >/dev/null 2>&1; then
    echo "imports:"
    objdump -p "$EXE" | sed -n 's/^\tDLL Name: /  /p' | sort
else
    echo "imports: not checked (no objdump on PATH)"
fi

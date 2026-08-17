#!/usr/bin/env bash
#
# Publish the outpost binary and run it against the hub, or install it as a service that runs it
# with those same flags at every boot.
#
# The outpost is the one server with no Dockerfile and no compose service, so there is
# nothing for run-local.sh to start: it is a self-contained single-file linux-x64 binary
# somebody copies onto a machine and runs with flags. This publishes it out of the
# checkout and runs it in one step, which is what you want on a machine that has the
# repo. A machine that does not have the repo gets the published file copied to it and
# runs it by hand — no script needed, which is the point of the binary.
#
#   scripts/run-outpost.sh                                  this machine, this directory
#   scripts/run-outpost.sh --jailed                          confined to it
#   scripts/run-outpost.sh --name laptop --dir ~/project --jailed --exec
#   scripts/run-outpost.sh --hub http://192.168.5.43:5000 --port 8100
#   scripts/run-outpost.sh --jailed --install-service        the same, at every boot
#   scripts/run-outpost.sh --uninstall-service               take it back off
#
# Every flag the binary takes is passed straight through. The script only fills in
# --name, --dir and --hub when you did not type them, so anything you type wins.
#
# Defaults: --name is this machine's hostname, --dir is the directory you ran this from,
# --hub is $DEFAULT_HUB below. Jailing and exec are off unless asked for, as they are in
# the binary — this script does not make either easier to switch on by accident.
#
# SHAREDSECRET is the one value that is not a flag, because a command line is visible to
# every process on the machine. It is taken from the environment, or from
# OUTPOSTS__SHAREDSECRET in DockerCompose/.env when the environment does not have it. An
# empty secret refuses everything at both ends, so an unset one stops the script here
# rather than starting an outpost the hub will not accept and nothing can dial.
#
# Ctrl-C reaches the binary directly (it is exec'd, not backgrounded), so stopping it
# deregisters the machine at once instead of leaving the mount to lapse 90 seconds later.
#
# --install-service and --uninstall-service
#
# --install-service writes a systemd USER unit that starts the binary with exactly the flags this
# run resolved — the ones you typed plus the defaults filled in for the ones you did not — enables
# it, and starts it. A user unit rather than a system one because an outpost publishes a person's
# own filesystem: the service must reach precisely the files its operator reaches, and giving the
# agent root over somebody's laptop to save a `--user` is not a trade worth making. Lingering is
# enabled so it comes up at boot with nobody logged in. Nothing about the running outpost changes;
# the same binary is started by systemd instead of by you.
#
# One unit per outpost NAME (ziggurat-outpost-<name>.service), because two outposts on one machine
# — one jailed, one not, on different ports — is a shape the binary already supports, and a single
# fixed unit name would let the second silently replace the first. --uninstall-service removes the
# one belonging to the same name, resolved the same way.
#
# The service runs a COPY of the binary under ~/.local/share/ziggurat-outpost, not the publish
# output in the checkout. Publishing over the file a running service is executing fails with "text
# file busy", and a `git clean` of bin/ would leave the unit pointing at nothing. The copy is
# renamed into place, so a reinstall never disturbs the process already running the old file.
#
# The secret stays out of the unit for the reason it stays off the command line: it is written to
# ~/.config/ziggurat-outpost/<name>.env at mode 600 and named there by EnvironmentFile=.
#
# WSL
#
# Both paths work on WSL and on an ordinary Linux machine; the only difference is that a WSL distro
# may have systemd switched off. When it is, this adds `[boot] systemd=true` to /etc/wsl.conf
# (keeping whatever else is in it), installs and enables the unit by hand, and tells you to run
# `wsl --shutdown` from Windows — the setting only takes effect when the distro next starts.
#
# Two WSL facts this script cannot fix for you, both about being reachable rather than about
# running: a distro does not start when Windows boots, so the service starts when you first open
# the distro (or when something else does — a Task Scheduler entry running `wsl.exe -d <distro>
# --exec true` at logon is the usual answer); and under WSL's default NAT networking the address
# the outpost works out for itself is the VM's 172.x, which the hub cannot dial back. Pass
# --advertise with an address the hub can reach and forward the port to it, or turn on mirrored
# networking. The script warns when it sees that combination — the registration would otherwise
# succeed and the mount would simply never answer.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
INVOKED_FROM="$PWD"
PROJECT="$ROOT/McpServerOutpost"
OUT="$PROJECT/bin/outpost"
BIN="$OUT/McpServerOutpost"
ENV_FILE="$ROOT/DockerCompose/.env"
CONFIG="${CONFIG:-Release}"
DEFAULT_HUB="${DEFAULT_HUB:-http://192.168.5.45:5000}"

# Where the service's own copy of the binary and its secret live. Under the user's home, because
# the unit is the user's.
INSTALL_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/ziggurat-outpost"
INSTALL_BIN="$INSTALL_DIR/McpServerOutpost"
CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/ziggurat-outpost"
UNIT_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"

usage() {
  # The header comment above, to its end: one description, not two that can disagree.
  awk 'NR > 1 && !/^#/ { exit } NR > 1 { sub(/^#[[:space:]]?/, ""); print }' "${BASH_SOURCE[0]}"
  echo "Binary flags: --name --dir --jailed --exec --hub --advertise --port --ext"
  echo "Script flags: --install-service --uninstall-service"
}

# Present, in either the `--flag value` or the `--flag=value` form the configuration
# provider accepts. Used only to decide whether a default is needed.
typed() {
  local want=$1 arg
  for arg in "${ARGS[@]:-}"; do
    [[ $arg == "$want" || $arg == "$want="* ]] && return 0
  done
  return 1
}

# What the binary will actually use for a flag, read off the fully resolved list. Scanned from the
# end because the command-line configuration provider keeps the last of a repeated key — which is
# also how this script overrides its own defaults by appending.
flag_value() {
  local want=$1 i
  for ((i = ${#flags[@]} - 1; i >= 0; i--)); do
    case ${flags[i]} in
      "$want="*)
        printf '%s' "${flags[i]#*=}"
        return 0
        ;;
      "$want")
        printf '%s' "${flags[i + 1]:-}"
        return 0
        ;;
    esac
  done
  return 1
}

# Take a flag and its value out of the resolved list, so the one appended after it is the only one
# left. Appending alone would already win — the configuration provider keeps the last of a repeated
# key — but a unit file is something a person reads, and a flag written twice reads like a bug.
drop_flag() {
  local want=$1 i out=()
  for ((i = 0; i < ${#flags[@]}; i++)); do
    case ${flags[i]} in
      "$want="*) ;;
      "$want") i=$((i + 1)) ;;
      *) out+=("${flags[i]}") ;;
    esac
  done
  flags=(${out[@]+"${out[@]}"})
}

# One key out of the env file, read rather than sourced: the file is the compose stack's
# and holds every secret the deployment has, none of which this needs. Trimmed at the edges
# only — a secret is allowed to contain a space, and mangling one here would start an outpost
# the hub silently refuses.
secret_from_env_file() {
  [[ -f $ENV_FILE ]] || return 0
  local value
  value="$(grep -m1 -E '^[[:space:]]*OUTPOSTS__SHAREDSECRET=' "$ENV_FILE" || true)"
  value="${value#*=}"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  value="${value%\"}"
  value="${value#\"}"
  printf '%s' "$value"
}

is_wsl() {
  [[ -n ${WSL_DISTRO_NAME:-} ]] || grep -qi microsoft /proc/sys/kernel/osrelease 2>/dev/null
}

# Said on every start under WSL NAT, not only when installing the service: the registration
# succeeds and the mount simply never answers, so the run that looks fine is exactly the one
# that needs the warning.
warn_wsl_nat() {
  if is_wsl && ! typed --advertise; then
    local port
    port="$(flag_value --port || true)"
    echo >&2
    echo "WSL: this outpost will register whatever address WSL's NAT gives it (172.x), which the" >&2
    echo "hub cannot dial back — the registration succeeds and the mount never answers. Start it" >&2
    echo "again with --advertise <an address the hub can reach> and forward port ${port:-8099} to" >&2
    echo "this distro, or turn on mirrored networking." >&2
  fi
}

# The canonical question, and the only one worth asking: not whether systemd is installed but
# whether it is pid 1 right now, which is exactly what a WSL distro without `[boot] systemd=true`
# fails.
systemd_running() {
  [[ -d /run/systemd/system ]]
}

# One ExecStart argument. systemd expands `%` specifiers over the whole line before it splits and
# unquotes it, so a `%` in a path has to be doubled; quoting carries the spaces.
unit_arg() {
  local value=${1//\\/\\\\}
  value=${value//\"/\\\"}
  printf '"%s"' "${value//%/%%}"
}

# A path for a setting that takes ONE path rather than a command line. EnvironmentFile= and
# WorkingDirectory= do not unquote — both reject a quoted value as "path is not absolute" — so the
# path goes in bare and only the specifier character is escaped. A directory with a space in it is
# therefore not expressible here, which is systemd's limitation rather than this script's.
unit_path() {
  printf '%s' "${1//%/%%}"
}

# A file name has a smaller alphabet than an outpost name does — the name reaches the hub intact,
# and only what this machine writes to disk is folded.
safe_name() {
  printf '%s' "$1" | tr -c 'A-Za-z0-9_.-' '-'
}

# systemctl --user needs to find the user manager's bus, which a plain login sets up and a shell
# reached some other way may not have.
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"

ARGS=()
ACTION=run

for arg in "$@"; do
  case $arg in
    --help | -h)
      usage
      exit 0
      ;;
    --install-service) ACTION=install ;;
    --uninstall-service) ACTION=uninstall ;;
    *) ARGS+=("$arg") ;;
  esac
done

# Resolved before anything is published or removed, because the outpost's name is what both the
# unit and the uninstall are keyed on, and it is the same answer either way.
flags=()
typed --name || flags+=(--name "$(hostname -s 2>/dev/null || uname -n)")
# Absolute, so the workspace the mount declares is the same directory whatever the
# binary's own working directory turns out to be.
typed --dir || flags+=(--dir "$INVOKED_FROM")
typed --hub || flags+=(--hub "$DEFAULT_HUB")
if [[ ${#ARGS[@]} -gt 0 ]]; then
  flags+=("${ARGS[@]}")
fi

NAME="$(flag_value --name)"
if [[ -z $NAME ]]; then
  echo "--name is empty. The name is the outpost's identity at the hub and this machine's unit" >&2
  echo "file is keyed on it, so there is nothing to install or remove under it." >&2
  exit 1
fi
UNIT="ziggurat-outpost-$(safe_name "$NAME").service"
UNIT_FILE="$UNIT_DIR/$UNIT"
UNIT_ENV="$CONFIG_DIR/$(safe_name "$NAME").env"

uninstall_service() {
  local removed=0
  if [[ -f $UNIT_FILE ]]; then
    removed=1
  fi

  if systemd_running; then
    systemctl --user disable --now "$UNIT" 2>/dev/null || true
  fi

  # The symlink `enable` writes, removed by hand as well: on a distro whose systemd has never run,
  # this script created it by hand too.
  rm -f "$UNIT_FILE" "$UNIT_ENV" "$UNIT_DIR/default.target.wants/$UNIT"
  rmdir "$CONFIG_DIR" 2>/dev/null || true

  if systemd_running; then
    systemctl --user daemon-reload
  fi

  # The binary copy is shared by every outpost installed on this machine, so it goes only when the
  # last unit that could be executing it has gone.
  if ! compgen -G "$UNIT_DIR/ziggurat-outpost-*.service" >/dev/null; then
    rm -f "$INSTALL_BIN"
    rmdir "$INSTALL_DIR" 2>/dev/null || true
  fi

  if [[ $removed -eq 1 ]]; then
    echo "removed $UNIT"
  else
    echo "no service installed for outpost '$NAME' — nothing to remove"
  fi
}

# Add `[boot] systemd=true` to /etc/wsl.conf without disturbing anything else in it: a distro's
# wsl.conf usually already carries the default user and the interop settings.
enable_wsl_systemd() {
  local conf=/etc/wsl.conf tmp
  if [[ -f $conf ]] && grep -qE '^[[:space:]]*systemd[[:space:]]*=[[:space:]]*true' "$conf"; then
    echo "/etc/wsl.conf already asks for systemd — this distro has not been restarted into it yet"
    return 0
  fi

  tmp="$(mktemp)"
  if [[ -f $conf ]]; then
    awk '
      /^[[:space:]]*\[/ {
        if (inboot && !done) { print "systemd=true"; done = 1 }
        inboot = ($0 ~ /^[[:space:]]*\[boot\][[:space:]]*$/)
        if (inboot) { seen = 1 }
        print; next
      }
      inboot && /^[[:space:]]*systemd[[:space:]]*=/ {
        if (!done) { print "systemd=true"; done = 1 }
        next
      }
      { print }
      END {
        if (!done) {
          if (!seen) { print "[boot]" }
          print "systemd=true"
        }
      }
    ' "$conf" >"$tmp"
    sudo cp -a "$conf" "$conf.bak"
  else
    printf '[boot]\nsystemd=true\n' >"$tmp"
  fi

  sudo install -m 644 "$tmp" "$conf"
  rm -f "$tmp"
  echo "wrote [boot] systemd=true to $conf"
}

install_service() {
  local secret=$1 dir
  dir="$(flag_value --dir)"

  mkdir -p "$INSTALL_DIR" "$CONFIG_DIR" "$UNIT_DIR"

  # Renamed into place rather than copied over: the destination may be the file a running service
  # is executing, and writing into that inode is ETXTBSY. A rename leaves the old inode to the old
  # process and hands the new file to the next start.
  install -m 755 "$BIN" "$INSTALL_DIR/.McpServerOutpost.new"
  mv -f "$INSTALL_DIR/.McpServerOutpost.new" "$INSTALL_BIN"

  # Never briefly world-readable, which writing then chmod'ing would be.
  (
    umask 077
    printf 'SHAREDSECRET=%s\n' "$secret" >"$UNIT_ENV"
  )

  local exec_line
  exec_line="$(unit_arg "$INSTALL_BIN")"
  local flag
  for flag in "${flags[@]}"; do
    exec_line+=" $(unit_arg "$flag")"
  done

  cat >"$UNIT_FILE" <<UNITEOF
# Written by scripts/run-outpost.sh --install-service. Reinstalling rewrites it; edit the flags by
# running that again rather than here, so the file and the command that produced it agree.
[Unit]
Description=Ziggurat outpost ($NAME)

[Service]
ExecStart=$exec_line
EnvironmentFile=$(unit_path "$UNIT_ENV")
WorkingDirectory=$(unit_path "$dir")
Restart=always
RestartSec=10
# No start-rate limit, deliberately. The outpost resolves the address the hub will dial at startup
# and refuses to start when it cannot, so a machine that booted before its network came up fails
# its first few starts — exactly the case a rate limit would turn into a permanent 'failed'. The
# registrar's own rule is that a hub coming back later must find the machine there; the unit keeps
# the same promise one level up.
StartLimitIntervalSec=0
# A clean stop is what deregisters the machine at the hub, so give it room to say so before SIGKILL.
TimeoutStopSec=20

[Install]
WantedBy=default.target
UNITEOF

  if systemd_running; then
    systemctl --user daemon-reload
    systemctl --user enable "$UNIT" >/dev/null
    # restart rather than start: reinstalling has to pick up the flags just written, and the unit
    # may already be running the old ones.
    systemctl --user restart "$UNIT"
    # Without lingering a user manager stops at logout and never runs at boot, which is most of
    # what installing a service was for. polkit usually grants this to an active session; sudo is
    # the fallback for a session it does not.
    loginctl enable-linger "$USER" 2>/dev/null || sudo loginctl enable-linger "$USER"
    echo "installed and started $UNIT"
    echo "  systemctl --user status $UNIT"
    echo "  journalctl --user -u $UNIT -f"
  else
    # A WSL distro with systemd switched off. `enable` writes one symlink and `enable-linger` one
    # empty file; both are written by hand here so the unit is already enabled when the distro
    # next starts with systemd as pid 1.
    mkdir -p "$UNIT_DIR/default.target.wants"
    ln -sf "$UNIT_FILE" "$UNIT_DIR/default.target.wants/$UNIT"
    sudo mkdir -p /var/lib/systemd/linger
    sudo touch "/var/lib/systemd/linger/$USER"
    enable_wsl_systemd
    echo "installed and enabled $UNIT, but systemd is not running on this distro."
    echo "Run 'wsl --shutdown' in Windows, then reopen this distro — it starts there."
  fi

  warn_wsl_nat
}

if [[ $ACTION == uninstall ]]; then
  uninstall_service
  exit 0
fi

command -v dotnet >/dev/null 2>&1 || {
  echo "dotnet is not on PATH — the binary is published from this checkout, not downloaded" >&2
  exit 1
}

secret="${SHAREDSECRET:-}"
[[ -n $secret ]] || secret="$(secret_from_env_file)"
if [[ -z $secret ]]; then
  echo "No shared secret. An outpost without one registers with nobody and answers nobody:" >&2
  echo "  the hub refuses an unset secret, and so does this binary's own /mcp." >&2
  echo "Set OUTPOSTS__SHAREDSECRET in $ENV_FILE (the same value the hub reads), or export" >&2
  echo "SHAREDSECRET for this run. It is deliberately not a flag." >&2
  exit 1
fi

if [[ $ACTION == install ]] && ! systemd_running && ! is_wsl; then
  echo "systemd is not running on this machine, and it is not WSL — there is nothing here to" >&2
  echo "install a unit into. Run the outpost in the foreground instead, or start it from" >&2
  echo "whatever init this machine does use." >&2
  exit 1
fi

# Every run, because the whole point of publishing from the checkout is that the binary
# is the code you have. It is incremental, so an unchanged tree costs a few seconds.
echo "publishing $CONFIG → $OUT"
dotnet publish "$PROJECT/McpServerOutpost.csproj" -c "$CONFIG" -o "$OUT" --nologo

[[ -x $BIN ]] || {
  echo "publish produced no runnable binary at $BIN" >&2
  exit 1
}

if [[ $ACTION == install ]]; then
  # The service's working directory is systemd's, not this shell's, so a --dir somebody typed
  # relative would name a different directory there, and the unit has to carry the resolved one.
  install_dir="$(flag_value --dir)"
  if [[ ! -d $install_dir ]]; then
    echo "--dir '$install_dir' is not a directory. The service would fail to start on it." >&2
    exit 1
  fi
  install_dir="$(cd "$install_dir" && pwd)"
  drop_flag --dir
  flags+=(--dir "$install_dir")
  echo "installing: McpServerOutpost ${flags[*]}"
  install_service "$secret"
  exit 0
fi

warn_wsl_nat

echo "running: McpServerOutpost ${flags[*]}"

# exec, so Ctrl-C and any supervisor's TERM land on the binary itself: a clean stop is
# what sends the deregistration.
exec env SHAREDSECRET="$secret" "$BIN" "${flags[@]}"

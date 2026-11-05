#!/bin/sh
# AMPLE installer for Linux (Ubuntu): per user by default, or for everyone with --system.
#
#   sh install.sh [--version 0.9.2] [--channel stable|unstable] [--system] [--no-packs]
#   (download the file first and run it; it cannot be piped into sh, because --system runs it again under sudo)
#
# AMPLE is beta: it works, and anything may still change, including the file formats, the sign-in and the server set-up.
#
# What it does: finds the release, downloads the zip, checks its SHA-256 against the release manifest, checks the manifest's
# signature against the release keys built into AMPLE, unpacks next to earlier versions and switches the `current` link.
# It switches on nothing: no service, no network address, no sign-in.
#
# What the signature check does and does not protect (read this):
#   - On an UPDATE the program that is already installed checks the new manifest BEFORE anything new is unpacked.
#   - On a FIRST install there is no program you already trust, so the new program checks the manifest itself. That catches a
#     damaged or wrongly built release, but NOT a compromised download server (which could serve its own program and manifest).
#     Your protection there is HTTPS to github.com. To check by hand, compare the fingerprints in keys/README.md of the
#     repository with the key id that `ample verify --file <manifest> --release` prints.
#
# Per user:  ~/.local/share/ample/<version>, link ~/.local/share/ample/current, command ~/.local/bin/ample
# System:    /opt/ample/<version>, link /opt/ample/current, command /usr/local/bin/ample (root only for the copy; AMPLE
#            itself is never run as root by this script)
# Testing:   AMPLE_RELEASE_BASE=<url or file:// url> reads <base>/ample-<version>.manifest.json and the zip from there.
set -eu

REPO="ample-koda/ample"
VERSION=""
CHANNEL="stable"
SYSTEM=0
PACKS=1

say() { printf '%s\n' "$*"; }
die() { printf 'install: %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "'$1' is required but was not found (on Ubuntu: sudo apt install $1)."; }

while [ $# -gt 0 ]; do
  case "$1" in
    --version) [ $# -ge 2 ] || die "--version needs a value"; VERSION="$2"; shift 2 ;;
    --channel) [ $# -ge 2 ] || die "--channel needs a value"; CHANNEL="$2"; shift 2 ;;
    --system) SYSTEM=1; shift ;;
    --no-packs) PACKS=0; shift ;;
    -h|--help) sed -n '2,24p' "$0"; exit 0 ;;
    *) die "Unknown option: $1" ;;
  esac
done
case "$CHANNEL" in stable|unstable) ;; *) die "The channel must be stable or unstable." ;; esac
case "$VERSION" in
  "") ;;
  [0-9]*) case "$VERSION" in *[!0-9A-Za-z.+-]*|*..*) die "'$VERSION' is not a version." ;; esac ;;
  *) die "'$VERSION' is not a version." ;;
esac
umask 022   # what a system install writes must be readable by everyone, whatever root's umask is

[ "$(uname -s)" = "Linux" ] || die "This installer is for Linux. On Windows use install.ps1."
case "$(uname -m)" in x86_64|amd64) RID="linux-x64" ;; *) die "Only x86_64 is built so far (this machine: $(uname -m))." ;; esac
for tool in unzip sha256sum find sort readlink mktemp tr sed grep head cut wc ln mv rm mkdir id uname dirname; do need "$tool"; done
# the cleanup uses GNU find's -printf; check it works here instead of assuming it
find / -maxdepth 0 -printf '' 2>/dev/null || die "This 'find' does not support -printf (GNU findutils is required)."
if command -v curl >/dev/null 2>&1; then
  fetch() { curl -fsSL "$1" -o "$2"; }
  fetch_text() { curl -fsSL "$1"; }
elif command -v wget >/dev/null 2>&1; then
  fetch() { wget -q "$1" -O "$2"; }
  fetch_text() { wget -qO- "$1"; }
else
  die "curl or wget is required."
fi

if [ "$SYSTEM" -eq 0 ]; then
  [ "$(id -u)" -ne 0 ] || die "Do not install as root for one user. Run it as yourself, or use --system to install the command for everyone."
  [ -n "${HOME:-}" ] && [ -d "$HOME" ] || die "HOME is not set to an existing folder."
  ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/ample"; LINK="$HOME/.local/bin/ample"
else
  ROOT="/opt/ample"; LINK="/usr/local/bin/ample"
  if [ "$(id -u)" -ne 0 ]; then
    need sudo
    [ -f "$0" ] && [ -r "$0" ] || die "--system runs this script again under sudo, so it must be a file: download it first, then run  sh install.sh --system"
    say "A system install copies files into /opt/ample and /usr/local/bin and needs sudo for that only."
    # the options were consumed (shifted) by the loop above, so the re-run is rebuilt from what was parsed
    set -- --system --channel "$CHANNEL"
    [ -z "$VERSION" ] || set -- "$@" --version "$VERSION"
    [ "$PACKS" -eq 1 ] || set -- "$@" --no-packs
    exec sudo env AMPLE_RELEASE_BASE="${AMPLE_RELEASE_BASE:-}" sh "$0" "$@"
  fi
fi

say "AMPLE is beta: it works, and anything may still change, including the file formats, the sign-in and the server set-up."

# 1. which release
if [ -z "$VERSION" ]; then
  [ -z "${AMPLE_RELEASE_BASE:-}" ] || die "AMPLE_RELEASE_BASE needs --version."
  if [ "$CHANNEL" = "stable" ]; then
    VERSION="$(fetch_text "https://api.github.com/repos/$REPO/releases/latest" | sed -n 's/.*"tag_name": *"ample-v\([^"]*\)".*/\1/p' | head -n 1)"
  else
    VERSION="$(fetch_text "https://api.github.com/repos/$REPO/releases?per_page=30" | sed -n 's/.*"tag_name": *"ample-v\([^"]*-[^"]*\)".*/\1/p' | head -n 1)"
  fi
  [ -n "$VERSION" ] || die "No $CHANNEL release was found. Pass --version."
  case "$VERSION" in [0-9]*) ;; *) die "The release list gave '$VERSION', which is not a version." ;; esac
  case "$VERSION" in *[!0-9A-Za-z.+-]*|*..*) die "The release list gave '$VERSION', which is not a version." ;; esac
fi
BASE="${AMPLE_RELEASE_BASE:-https://github.com/$REPO/releases/download/ample-v$VERSION}"
ZIPNAME="ample-$VERSION-$RID.zip"
say "Installing AMPLE $VERSION ($RID) into $ROOT"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT INT TERM
MANIFEST="$TMP/ample-$VERSION.manifest.json"
fetch "$BASE/ample-$VERSION.manifest.json" "$MANIFEST" || die "The release manifest could not be downloaded from $BASE."
fetch "$BASE/ample-$VERSION.manifest.json.sig" "$MANIFEST.sig" || die "The manifest signature could not be downloaded."

# 2. a program that is already installed (and is ours) checks the manifest before anything new is unpacked
TRUSTED=""
# (an installed program from before this check existed cannot do it; then the new program does, as on a first install)
if [ -x "$ROOT/current/ample" ] && "$ROOT/current/ample" verify --help 2>/dev/null | grep -qF -- '--release'; then
  "$ROOT/current/ample" verify --file "$MANIFEST" --release >/dev/null 2>&1 \
    || die "The manifest is not signed by an AMPLE release key (checked by the installed program). Nothing was changed."
  TRUSTED="installed"
fi

# the manifest must be for the version that was asked for
tr -d '\n\r\t ' < "$MANIFEST" | grep -qF "\"version\":\"$VERSION\"" || die "The manifest is not for version $VERSION. Nothing was changed."

# 3. the zip, checked against the manifest (one artefact object per line; the order of its fields does not matter)
EXPECTED="$(tr -d '\n\r\t ' < "$MANIFEST" | sed 's/},{/}\n{/g' | grep -F "\"name\":\"$ZIPNAME\"" | sed -n 's/.*"sha256":"\([0-9a-fA-F]\{64\}\)".*/\1/p' | head -n 1 | tr 'A-F' 'a-f')"
[ -n "$EXPECTED" ] || die "$ZIPNAME is not listed in the manifest (or has no SHA-256)."
fetch "$BASE/$ZIPNAME" "$TMP/$ZIPNAME" || die "$ZIPNAME could not be downloaded."
ACTUAL="$(sha256sum "$TMP/$ZIPNAME" | cut -d ' ' -f 1)"
[ "$ACTUAL" = "$EXPECTED" ] || die "The download does not match the manifest (SHA-256). Nothing was changed."

# 4. look before touching the target: nothing of ours may be replaced by accident
if [ -e "$ROOT/current" ] || [ -L "$ROOT/current" ]; then
  [ -L "$ROOT/current" ] || die "$ROOT/current exists and is not a link; leaving it alone. Move it away and run again."
fi
if [ -e "$LINK" ] || [ -L "$LINK" ]; then
  if [ -L "$LINK" ]; then
    case "$(readlink "$LINK")" in "$ROOT"/*) ;; *) die "$LINK is a link to something that is not an AMPLE install ($(readlink "$LINK")); leaving it alone." ;; esac
  else
    die "$LINK exists and is not a link made by this installer; leaving it alone."
  fi
fi

DEST="$ROOT/$VERSION"
mkdir -p "$ROOT"
if [ -e "$DEST" ]; then
  # an earlier install of the same version: use it only if it is whole and is that version
  [ -d "$DEST" ] && [ ! -L "$DEST" ] && [ -x "$DEST/ample" ] && [ -f "$DEST/install.json" ] \
    && grep -qF "\"version\": \"$VERSION\"" "$DEST/install.json" && "$DEST/ample" --version >/dev/null 2>&1 \
    || die "$DEST exists but is not a complete install of $VERSION. Move it away and run again."
  say "Version $VERSION is already installed in $DEST; keeping it."
else
  STAGE="$ROOT/.installing-$VERSION-$$"
  trap 'rm -rf "$TMP" "$STAGE"' EXIT INT TERM
  rm -rf "$STAGE"; mkdir "$STAGE"
  unzip -q "$TMP/$ZIPNAME" -d "$STAGE" || die "The zip could not be unpacked."
  # the zip must hold exactly one top-level folder with the program in it
  [ "$(find "$STAGE" -mindepth 1 -maxdepth 1 | wc -l)" -eq 1 ] || die "The zip does not have the expected shape (one folder with the program)."
  set -- "$STAGE"/*
  [ $# -eq 1 ] && [ -d "$1" ] && [ ! -L "$1" ] && [ -f "$1/ample" ] || die "The zip does not have the expected shape (one folder with the program)."
  INNER="$1"
  chmod +x "$INNER/ample"
  if [ -z "$TRUSTED" ]; then
    "$INNER/ample" verify --file "$MANIFEST" --release || die "The manifest is not signed by an AMPLE release key. Nothing was changed."
    say "First install: the manifest was checked by the new program itself (see the notes at the top of this script)."
  fi
  printf '{ "how": "script", "scope": "%s", "channel": "%s", "version": "%s" }\n' "$( [ "$SYSTEM" -eq 1 ] && echo system || echo user )" "$CHANNEL" "$VERSION" > "$INNER/install.json"
  mv "$INNER" "$DEST"   # same file system as $STAGE: a rename
  rm -rf "$STAGE"
fi

# 5. switch `current`, keep the previous one, tidy the older ones (keep the newest two)
PREVIOUS=""
[ -L "$ROOT/current" ] && PREVIOUS="$(readlink "$ROOT/current")"
ln -sfn "$DEST" "$ROOT/current"
mkdir -p "$(dirname "$LINK")"
ln -sfn "$ROOT/current/ample" "$LINK"
# Only real directories that look like a version are candidates (never a link, never anything else in the folder); the
# newest two stay, and so do the current and the previous one. Names come from `find`, one per line, and are checked again.
KEPT=0
find "$ROOT" -mindepth 1 -maxdepth 1 -type d -name '[0-9]*' -printf '%T@ %f\n' | sort -rn | while read -r _ name; do
  case "$name" in *[!0-9A-Za-z.+-]*|"") continue ;; esac
  KEPT=$((KEPT + 1))
  [ "$KEPT" -gt 2 ] || continue
  [ "$ROOT/$name" != "$PREVIOUS" ] && [ "$ROOT/$name" != "$DEST" ] || continue
  [ -d "$ROOT/$name" ] && [ ! -L "$ROOT/$name" ] || continue
  rm -rf -- "${ROOT:?}/$name"
done

say "Installed: $LINK -> $ROOT/current/ample"
"$LINK" --version || die "The installed program does not start."
case ":$PATH:" in *":$(dirname "$LINK"):"*) ;; *) say "Note: $(dirname "$LINK") is not on your PATH yet; add it to your shell profile." ;; esac

# 6. the SoundFont (required). A person's own install fetches it with the program, as that person. A system install puts ONE
# copy in the machine-wide folder, read-only for users and searched after each person's own folder (so anyone can still
# override it with `ample packs install soundfont`). Root only downloads and verifies data here; it never runs AMPLE.
install_system_soundfont() {
  SFDIR="/usr/share/ample/packs/licensed/musescore-general"
  SFNAME="MuseScore_General.sf2"
  # name, url and SHA-256 come from the manifest whose signature was checked above (every { and } becomes a line break)
  SFLINE="$(tr -d '\n\r\t ' < "$MANIFEST" | sed 's/[{}]/\n/g' | grep -F "\"name\":\"$SFNAME\"" | head -n 1)"
  SFURL="$(printf '%s' "$SFLINE" | sed -n 's/.*"url":"\([^"]*\)".*/\1/p')"
  SFSHA="$(printf '%s' "$SFLINE" | sed -n 's/.*"sha256":"\([0-9a-fA-F]\{64\}\)".*/\1/p' | tr 'A-F' 'a-f')"
  [ -n "$SFURL" ] && [ -n "$SFSHA" ] || die "The manifest does not name the SoundFont (url and SHA-256)."
  case "$SFURL" in
    https://*) ;;
    file://*) [ -n "${AMPLE_RELEASE_BASE:-}" ] || die "The SoundFont url '$SFURL' is not https." ;;
    *) die "The SoundFont url '$SFURL' is not https." ;;
  esac
  if [ -e "$SFDIR/$SFNAME" ] || [ -L "$SFDIR/$SFNAME" ]; then
    [ -f "$SFDIR/$SFNAME" ] && [ ! -L "$SFDIR/$SFNAME" ] || die "$SFDIR/$SFNAME exists and is not a plain file; leaving it alone."
    [ "$(sha256sum "$SFDIR/$SFNAME" | cut -d ' ' -f 1)" = "$SFSHA" ] || die "$SFDIR/$SFNAME exists but does not match the pinned SHA-256; leaving it alone. Move it away and run again."
    say "The system SoundFont is already in place and verified: $SFDIR/$SFNAME"
    return 0
  fi
  say "Fetching the General MIDI SoundFont for everyone (about 205 MB, checked by SHA-256)..."
  fetch "$SFURL" "$TMP/$SFNAME" || die "The SoundFont could not be downloaded from $SFURL."
  [ "$(wc -c < "$TMP/$SFNAME")" -le 536870912 ] || die "The SoundFont is larger than the allowed 512 MB. Nothing was installed."
  [ "$(sha256sum "$TMP/$SFNAME" | cut -d ' ' -f 1)" = "$SFSHA" ] || die "The SoundFont does not match the pinned SHA-256. Nothing was installed."
  mkdir -p "$SFDIR"
  cp "$TMP/$SFNAME" "$SFDIR/$SFNAME.part" && chmod 644 "$SFDIR/$SFNAME.part" && mv "$SFDIR/$SFNAME.part" "$SFDIR/$SFNAME" || { rm -f "$SFDIR/$SFNAME.part"; die "The SoundFont could not be written to $SFDIR."; }
  # its licence text, best effort (small files from the same folder; a failure here does not undo the install)
  if fetch "${SFURL%/*}/MuseScore_General_License.md" "$TMP/license.md" 2>/dev/null && [ "$(wc -c < "$TMP/license.md")" -le 16777216 ]; then
    cp "$TMP/license.md" "$SFDIR/MuseScore_General_License.md" && chmod 644 "$SFDIR/MuseScore_General_License.md" || true
  fi
  say "Installed: $SFDIR/$SFNAME  (a person can override it with: ample packs install soundfont)"
}
if [ "$PACKS" -eq 1 ]; then
  if [ "$SYSTEM" -eq 1 ]; then
    install_system_soundfont
  else
    say "Fetching the General MIDI SoundFont (about 205 MB, checked by SHA-256)..."
    if ! "$LINK" packs install soundfont; then
      say "AMPLE is installed, but the SoundFont is required and was not installed. Run again when you are online:  ample packs install soundfont"
      exit 1
    fi
  fi
fi
say "Check the installation any time with:  ample doctor --env"

#!/bin/sh
# Builds the Debian packages of AMPLE: `ample` (the program) and `ample-soundfont` (the General MIDI SoundFont).
#
#   sh tools/build-deb.sh --version 0.9.2 --program <folder with the unpacked linux-x64 release> --out <folder> [--soundfont <MuseScore_General.sf2>] [--licence <LICENSE>]
#
# The program folder is the one inside ample-<version>-linux-x64.zip (it holds `ample`, web/, spec/, plugins/, docs/).
# --soundfont builds `ample-soundfont` too; the file must match the pinned SHA-256 or nothing is built.
# Nothing here starts AMPLE, registers a service or opens a port; there are no maintainer scripts at all.
set -eu

PINNED_SF_SHA256="ee51d2c4b1525e70f19a45909c4fd7a2e26d91d115fa89dbf5a6bc413d8b9bf3"
VERSION=""; PROGRAM=""; OUT=""; SOUNDFONT=""; LICENCE=""

die() { printf 'build-deb: %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "'$1' is required but was not found."; }

while [ $# -gt 0 ]; do
  case "$1" in
    --version) [ $# -ge 2 ] || die "--version needs a value"; VERSION="$2"; shift 2 ;;
    --program) [ $# -ge 2 ] || die "--program needs a value"; PROGRAM="$2"; shift 2 ;;
    --out) [ $# -ge 2 ] || die "--out needs a value"; OUT="$2"; shift 2 ;;
    --soundfont) [ $# -ge 2 ] || die "--soundfont needs a value"; SOUNDFONT="$2"; shift 2 ;;
    --licence|--license) [ $# -ge 2 ] || die "$1 needs a value"; LICENCE="$2"; shift 2 ;;
    -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
    *) die "Unknown option: $1" ;;
  esac
done
for tool in dpkg-deb sha256sum md5sum xargs find mktemp tr sed cp mkdir ln chmod rm du cut grep; do need "$tool"; done

# the version: 1.2.3 or 1.3.0-rc.1 (a pre-release); in Debian a pre-release sorts BEFORE the release with a tilde
case "$VERSION" in
  [0-9]*) case "$VERSION" in *[!0-9A-Za-z.+-]*|*..*) die "'$VERSION' is not a version." ;; esac ;;
  *) die "--version is required and must start with a digit." ;;
esac
DEBVERSION="$(printf '%s' "$VERSION" | sed 's/-/~/')"
[ -n "$PROGRAM" ] && [ -d "$PROGRAM" ] && [ ! -L "$PROGRAM" ] || die "--program must be an existing folder (not a link)."
[ -f "$PROGRAM/ample" ] && [ ! -L "$PROGRAM/ample" ] || die "$PROGRAM/ample was not found (or is a link)."
[ -n "$OUT" ] || die "--out is required."
# everything that can be refused is checked BEFORE anything is built, so a refusal never leaves a half result behind
if [ -n "$SOUNDFONT" ]; then
  [ -f "$SOUNDFONT" ] && [ ! -L "$SOUNDFONT" ] || die "--soundfont must be a plain file."
  [ "$(sha256sum "$SOUNDFONT" | cut -d ' ' -f 1)" = "$PINNED_SF_SHA256" ] || die "The SoundFont does not match the pinned SHA-256; no package was built."
fi
if [ -n "$LICENCE" ]; then [ -f "$LICENCE" ] || die "--licence '$LICENCE' is not a file."; fi
# a link anywhere in what we package could point outside it
if [ -n "$(find "$PROGRAM" -type l -print -quit)" ]; then die "$PROGRAM holds a symbolic link; refusing to package it."; fi
# file names the package tools and shell scripts handle badly
if find "$PROGRAM" -name '*[[:cntrl:]]*' -print -quit | grep -q .; then die "$PROGRAM holds a file name with a control character."; fi

mkdir -p "$OUT"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT INT TERM
umask 022

# ---- ample
ROOT="$TMP/ample"
mkdir -p "$ROOT/DEBIAN" "$ROOT/usr/lib/ample" "$ROOT/usr/bin" "$ROOT/usr/share/doc/ample-koda"
cp -R "$PROGRAM/." "$ROOT/usr/lib/ample/"
# whatever the modes in the source were: nothing is writable by group or others, everything is readable, folders are searchable
chmod -R go-w,a+rX "$ROOT/usr"
chmod 755 "$ROOT/usr/lib/ample/ample"
printf '{ "how": "apt", "scope": "system", "channel": "%s", "version": "%s" }\n' "$(case "$VERSION" in *-*) echo unstable ;; *) echo stable ;; esac)" "$VERSION" > "$ROOT/usr/lib/ample/install.json"
ln -s /usr/lib/ample/ample "$ROOT/usr/bin/ample"
if [ -n "$LICENCE" ]; then
  cp "$LICENCE" "$ROOT/usr/share/doc/ample-koda/copyright"
else
  printf 'AMPLE is released under the MIT licence. See https://github.com/ample-koda/ample\n' > "$ROOT/usr/share/doc/ample-koda/copyright"
fi
SIZE="$(du -sk "$ROOT/usr" | cut -f1)"
cat > "$ROOT/DEBIAN/control" <<EOF
Package: ample-koda
Version: $DEBVERSION
Architecture: amd64
Maintainer: AMPLE packages <packages@ample-koda.dev>
Installed-Size: $SIZE
Section: sound
Priority: optional
Homepage: https://ample-koda.dev
Depends: libc6, libstdc++6, libgcc-s1, libssl3 | libssl1.1, ca-certificates, zlib1g
Conflicts: ample
Replaces: ample
Recommends: ample-soundfont, libicu74 | libicu72 | libicu70 | libicu66
Description: AMPLE, songs as reproducible text (beta)
 AMPLE is beta: it works, and anything may still change, including
 the file formats, the sign-in and the server set-up.
 .
 This package installs the ample command and the studio for everyone
 on the machine. It starts nothing and opens no network port.
 Without the ICU libraries the program still runs when the environment
 variable DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 is set.
EOF
( cd "$ROOT" && find usr -type f -print0 | xargs -0 md5sum > DEBIAN/md5sums )   # what dpkg --verify reads
dpkg-deb --root-owner-group --build "$ROOT" "$OUT/ample-koda_${DEBVERSION}_amd64.deb" >/dev/null
printf 'built %s\n' "$OUT/ample-koda_${DEBVERSION}_amd64.deb"

# ---- ample-soundfont (only when the file is given, and only when it is the pinned one)
if [ -n "$SOUNDFONT" ]; then
  SROOT="$TMP/soundfont"
  SFDIR="$SROOT/usr/share/ample/packs/licensed/musescore-general"
  mkdir -p "$SROOT/DEBIAN" "$SFDIR" "$SROOT/usr/share/doc/ample-soundfont"
  cp "$SOUNDFONT" "$SFDIR/MuseScore_General.sf2"; chmod 644 "$SFDIR/MuseScore_General.sf2"
  printf 'MuseScore General (MIT). https://musescore.org/en/handbook/3/soundfonts-and-sfz-files\n' > "$SROOT/usr/share/doc/ample-soundfont/copyright"
  SSIZE="$(du -sk "$SROOT/usr" | cut -f1)"
  cat > "$SROOT/DEBIAN/control" <<EOF
Package: ample-soundfont
Version: 1.0
Architecture: all
Maintainer: AMPLE packages <packages@ample-koda.dev>
Installed-Size: $SSIZE
Section: sound
Priority: optional
Homepage: https://ample-koda.dev
Description: General MIDI SoundFont for AMPLE (MuseScore General)
 One machine-wide copy of the General MIDI SoundFont that AMPLE needs,
 verified against a pinned SHA-256 when this package was built.
 A person can still override it with their own: ample packs install soundfont
EOF
  dpkg-deb --root-owner-group --build "$SROOT" "$OUT/ample-soundfont_1.0_all.deb" >/dev/null
  printf 'built %s\n' "$OUT/ample-soundfont_1.0_all.deb"
fi

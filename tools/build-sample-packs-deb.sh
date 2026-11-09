#!/bin/sh
# Builds the Debian package `ample-sample-packs`: every sample pack of the catalog, installed machine-wide in /usr/share/ample/packs/<tier>/<id>/
# (the folder AMPLE searches after the packs of the user). It is the same content as `ample packs install all`, for people who prefer apt.
#
#   sh tools/build-sample-packs-deb.sh --zips <folder with the pack zips and packs-index.json> --out <folder> [--version 1.0]
#
# Every zip is checked against the SHA-256 in packs-index.json first; one that does not match, an unsafe path in a zip, or a link stops the build.
# Nothing here starts AMPLE, registers a service or opens a port; there are no maintainer scripts at all.
set -eu

ZIPS=""; OUT=""; VERSION="1.0"
die() { printf 'build-sample-packs-deb: %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "'$1' is required but was not found."; }

while [ $# -gt 0 ]; do
  case "$1" in
    --zips) [ $# -ge 2 ] || die "--zips needs a value"; ZIPS="$2"; shift 2 ;;
    --out) [ $# -ge 2 ] || die "--out needs a value"; OUT="$2"; shift 2 ;;
    --version) [ $# -ge 2 ] || die "--version needs a value"; VERSION="$2"; shift 2 ;;
    -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
    *) die "Unknown option: $1" ;;
  esac
done
for tool in dpkg-deb unzip sha256sum awk find mktemp du cut grep sed cp mkdir chmod rm; do need "$tool"; done
case "$VERSION" in [0-9]*) case "$VERSION" in *[!0-9A-Za-z.+~]*|*..*) die "'$VERSION' is not a version." ;; esac ;; *) die "--version must start with a digit." ;; esac
[ -n "$ZIPS" ] && [ -d "$ZIPS" ] || die "--zips must be an existing folder."
[ -n "$OUT" ] || die "--out is required."
INDEX="$ZIPS/packs-index.json"
[ -f "$INDEX" ] || die "$INDEX was not found."

# the list "<id> <tier> <asset> <sha256>" from the catalog (one field per line, as the build tool writes it)
LIST="$(awk '
  /"id":/     { gsub(/[",]/, "", $2); id = $2 }
  /"tier":/   { gsub(/[",]/, "", $2); tier = $2 }
  /"sha256":/ { gsub(/[",]/, "", $2); sha = $2 }
  /"asset":/  { gsub(/[",]/, "", $2); print id, tier, $2, sha }
' "$INDEX")"
[ -n "$LIST" ] || die "the catalog lists no pack."

# everything that can be refused is checked BEFORE anything is built
printf '%s\n' "$LIST" | while read -r id tier asset sha; do
  case "$id" in ''|*[!0-9A-Za-z-]*) die "unsafe pack id '$id'" ;; esac
  case "$tier" in free|licensed) ;; *) die "unknown tier '$tier' for $id" ;; esac
  [ -f "$ZIPS/$asset" ] && [ ! -L "$ZIPS/$asset" ] || die "$asset was not found (or is a link)."
  [ "$(sha256sum "$ZIPS/$asset" | cut -d ' ' -f 1)" = "$sha" ] || die "$asset does not match the SHA-256 of the catalog; no package was built."
  unzip -Z1 "$ZIPS/$asset" | while read -r name; do
    case "$name" in "$id"/*) ;; *) die "$asset holds '$name' outside $id/" ;; esac
    case "$name" in */../*|../*|/*|*:*) die "$asset holds an unsafe path '$name'" ;; esac
  done
done

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT INT TERM
ROOT="$TMP/pkg"
mkdir -p "$ROOT/DEBIAN" "$ROOT/usr/share/doc/ample-sample-packs" "$OUT"
printf '%s\n' "$LIST" | while read -r id tier asset sha; do
  mkdir -p "$ROOT/usr/share/ample/packs/$tier"
  unzip -q -o "$ZIPS/$asset" -d "$ROOT/usr/share/ample/packs/$tier"
done
if [ -n "$(find "$ROOT" -type l -print -quit)" ]; then die "a zip holds a symbolic link; refusing to package it."; fi
find "$ROOT/usr" -type d -exec chmod 755 {} +
find "$ROOT/usr" -type f -exec chmod 644 {} +
{
  printf 'Sample packs for AMPLE. Each pack folder holds its own LICENSE.txt and SOURCE.md; the packs under licensed/ also hold ATTRIBUTION.md with the credit line.\n'
  printf 'free/ is CC0 (public domain); licensed/ asks for a credit (CC BY).\n\n'
  printf '%s\n' "$LIST" | while read -r id tier asset sha; do printf '%s/%s\n' "$tier" "$id"; done
} > "$ROOT/usr/share/doc/ample-sample-packs/copyright"
SIZE="$(du -sk "$ROOT/usr" | cut -f1)"
cat > "$ROOT/DEBIAN/control" <<EOF
Package: ample-sample-packs
Version: $VERSION
Architecture: all
Maintainer: AMPLE packages <packages@ample-koda.dev>
Installed-Size: $SIZE
Section: sound
Priority: optional
Homepage: https://ample-koda.dev
Description: Sample packs for AMPLE (drums, basses, guitars, pianos)
 Machine-wide copies of the sample packs that AMPLE's sampled instruments
 play (CC0, and CC BY with the credit line in each pack). Each zip was
 checked against the SHA-256 of the catalog when this package was built.
 The same packs can be fetched for one person: ample packs install all
EOF
dpkg-deb --root-owner-group -Zxz --build "$ROOT" "$OUT/ample-sample-packs_${VERSION}_all.deb" >/dev/null
printf 'built %s\n' "$OUT/ample-sample-packs_${VERSION}_all.deb"

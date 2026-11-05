#!/bin/sh
# Runs ON THE APT SERVER: puts a new version of the apt repository in place without a moment in which it is inconsistent.
#
#   sh apt-server-update.sh --index <folder with the signed dists/ tree> --release-url <folder url that holds the .deb files> --webroot <served folder>
#
# The signed index was made on the owner's machine (tools/build-apt-repo.sh --debs ...); the private key never comes here.
# This script downloads every package the index lists from the release (a public https address; file:// works for tests),
# checks its size and SHA-256 against the index, puts the packages in the pool, and only then swaps the dists/ tree.
# Nothing is changed when anything does not match.
set -eu

INDEX=""; URL=""; WEBROOT=""; SUITE="stable"
die() { printf 'apt-server-update: %s\n' "$*" >&2; exit 1; }
while [ $# -gt 0 ]; do
  case "$1" in
    --index) [ $# -ge 2 ] || die "--index needs a value"; INDEX="$2"; shift 2 ;;
    --release-url) [ $# -ge 2 ] || die "--release-url needs a value"; URL="$2"; shift 2 ;;
    --webroot) [ $# -ge 2 ] || die "--webroot needs a value"; WEBROOT="$2"; shift 2 ;;
    --suite) [ $# -ge 2 ] || die "--suite needs a value"; SUITE="$2"; shift 2 ;;
    *) die "Unknown option: $1" ;;
  esac
done
[ -n "$INDEX" ] && [ -n "$URL" ] && [ -n "$WEBROOT" ] || die "--index, --release-url and --webroot are required."
case "$SUITE" in *[!a-z0-9-]*|"") die "bad suite name." ;; esac
case "$URL" in https://*|file://*) ;; *) die "the release url must be https:// (or file:// for a test)." ;; esac
PACKAGES="$INDEX/dists/$SUITE/main/binary-amd64/Packages"
for f in "$PACKAGES" "$INDEX/dists/$SUITE/InRelease" "$INDEX/dists/$SUITE/Release" "$INDEX/dists/$SUITE/Release.gpg"; do
  [ -s "$f" ] || die "missing in the index folder: $f"
done
[ -d "$WEBROOT" ] || die "no such web root: $WEBROOT"
for tool in curl sha256sum awk mktemp; do command -v "$tool" >/dev/null 2>&1 || die "'$tool' is required."; done

TMP=$(mktemp -d "$WEBROOT/.update.XXXXXX")
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$TMP/pool"

# 1. every package of the index: download, check size and SHA-256, keep the pool path
awk '
  $1 == "Filename:" { file = $2 }
  $1 == "Size:"     { size = $2 }
  $1 == "SHA256:"   { print file, size, $2; file = ""; size = "" }
' "$PACKAGES" > "$TMP/list"
[ -s "$TMP/list" ] || die "the index lists no packages."
while read -r path size sha; do
  case "$path" in pool/main/*/*/*.deb) ;; *) die "unexpected path in the index: $path" ;; esac
  case "$path" in *..*) die "unexpected path in the index: $path" ;; esac
  name=$(basename "$path")
  mkdir -p "$TMP/$(dirname "$path")"
  curl -fsSL --max-filesize 600000000 -o "$TMP/$path" "$URL/$name" || die "could not download $name from $URL"
  [ "$(wc -c < "$TMP/$path" | tr -d ' ')" = "$size" ] || die "$name has the wrong size."
  [ "$(sha256sum "$TMP/$path" | cut -d' ' -f1)" = "$sha" ] || die "$name does not match the SHA-256 of the index."
  printf 'ok  %s\n' "$path"
done < "$TMP/list"

# 2. the packages go in first (a client that still has the old index only asks for old files, which stay), then the index is swapped
mkdir -p "$WEBROOT/pool/main"
( cd "$TMP/pool" && find . -type f -name '*.deb' ) | while read -r rel; do
  dest="$WEBROOT/pool/$rel"
  mkdir -p "$(dirname "$dest")"
  cp "$TMP/pool/$rel" "$dest.new" && chmod 644 "$dest.new" && mv "$dest.new" "$dest"
done
cp -r "$INDEX/dists" "$WEBROOT/dists.new"
find "$WEBROOT/dists.new" -type d -exec chmod 755 {} +
find "$WEBROOT/dists.new" -type f -exec chmod 644 {} +
[ ! -d "$WEBROOT/dists" ] || mv "$WEBROOT/dists" "$WEBROOT/dists.old"
mv "$WEBROOT/dists.new" "$WEBROOT/dists"
rm -rf "$WEBROOT/dists.old"
printf 'updated %s\n' "$WEBROOT"

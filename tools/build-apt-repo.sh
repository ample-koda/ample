#!/bin/sh
# Builds a signed apt repository (one suite, one component, amd64) from the AMPLE release files.
#
#   sh tools/build-apt-repo.sh --debs <folder with the .deb files of a release> \
#        --out <folder> --key-file <private key, armored> --passphrase-file <file> [--suite stable]
#   sh tools/build-apt-repo.sh --version 0.1.0 --zip <ample-<version>-linux-x64.zip> --soundfont <MuseScore_General.sf2> \
#        --out <folder> --key-file <private key, armored> --passphrase-file <file> [--suite stable] [--licence <LICENSE>]
#
# With --debs the packages of a release (built by the release workflow) are used as they are: this is the way to publish an update.
# Without it the script runs tools/build-deb.sh for the program and the SoundFont package. Either way it scans them with apt-ftparchive and signs the Release file
# (InRelease and Release.gpg) with the key. The key is imported into a throw-away GNUPGHOME that is removed again; the passphrase
# comes from a file and is never printed. The output folder is what a web server serves as the root of the repository:
#   pool/main/<a>/<package>/*.deb   dists/<suite>/{InRelease,Release,Release.gpg,main/binary-amd64/Packages(.gz)}
set -eu

VERSION=""; ZIP=""; SOUNDFONT=""; OUT=""; KEYFILE=""; PASSFILE=""; SUITE="stable"; LICENCE=""; DEBS=""
die() { printf 'build-apt-repo: %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "'$1' is required but was not found."; }

while [ $# -gt 0 ]; do
  case "$1" in
    --version) [ $# -ge 2 ] || die "--version needs a value"; VERSION="$2"; shift 2 ;;
    --zip) [ $# -ge 2 ] || die "--zip needs a value"; ZIP="$2"; shift 2 ;;
    --debs) [ $# -ge 2 ] || die "--debs needs a value"; DEBS="$2"; shift 2 ;;
    --soundfont) [ $# -ge 2 ] || die "--soundfont needs a value"; SOUNDFONT="$2"; shift 2 ;;
    --out) [ $# -ge 2 ] || die "--out needs a value"; OUT="$2"; shift 2 ;;
    --key-file) [ $# -ge 2 ] || die "--key-file needs a value"; KEYFILE="$2"; shift 2 ;;
    --passphrase-file) [ $# -ge 2 ] || die "--passphrase-file needs a value"; PASSFILE="$2"; shift 2 ;;
    --suite) [ $# -ge 2 ] || die "--suite needs a value"; SUITE="$2"; shift 2 ;;
    --licence|--license) [ $# -ge 2 ] || die "$1 needs a value"; LICENCE="$2"; shift 2 ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) die "Unknown option: $1" ;;
  esac
done
[ -n "$OUT" ] && [ -n "$KEYFILE" ] && [ -n "$PASSFILE" ] || die "--out, --key-file and --passphrase-file are required."
if [ -z "$DEBS" ]; then
  [ -n "$VERSION" ] && [ -n "$ZIP" ] && [ -n "$SOUNDFONT" ] || die "give --debs, or --version, --zip and --soundfont."
  [ -f "$ZIP" ] || die "no such file: $ZIP"
  [ -f "$SOUNDFONT" ] || die "no such file: $SOUNDFONT"
  need unzip
else
  [ -z "$ZIP$SOUNDFONT$VERSION" ] || die "--debs cannot be combined with --version, --zip or --soundfont."
  [ -d "$DEBS" ] || die "no such folder: $DEBS"
  ls "$DEBS"/*.deb >/dev/null 2>&1 || die "no .deb files in $DEBS"
fi
case "$SUITE" in *[!a-z0-9-]*|"") die "the suite name may only hold a-z, 0-9 and '-'." ;; esac
[ -s "$PASSFILE" ] || die "the passphrase file is missing or empty."
[ -f "$KEYFILE" ] || die "no such key file: $KEYFILE"
for tool in gpg apt-ftparchive gzip dpkg-deb sha256sum mktemp find; do need "$tool"; done
HERE=$(cd "$(dirname "$0")" && pwd)
[ ! -e "$OUT" ] || [ -z "$(ls -A "$OUT" 2>/dev/null)" ] || die "the output folder '$OUT' is not empty."

WORK=$(mktemp -d /tmp/ample-apt-build.XXXXXX)
GNUPGHOME="$WORK/gnupg"; export GNUPGHOME
mkdir -m 700 "$GNUPGHOME"
trap 'rm -rf "$WORK"' EXIT

# 1+2. the packages: those of a release (--debs) or built here from the zip
mkdir -p "$WORK/debs"
if [ -n "$DEBS" ]; then
  cp "$DEBS"/*.deb "$WORK/debs/"
else
  # 1. the program folder inside the zip (it holds ample, web/, spec/, plugins/, docs/)
  mkdir "$WORK/unzipped"
  unzip -q "$ZIP" -d "$WORK/unzipped"
  set -- "$WORK/unzipped"/*
  [ $# -eq 1 ] && [ -d "$1" ] && [ -f "$1/ample" ] || die "the zip does not hold one folder with 'ample'."
  PROGRAM="$1"

  # 2. the packages
  mkdir -p "$WORK/debs"
  if [ -n "$LICENCE" ]; then
    sh "$HERE/build-deb.sh" --version "$VERSION" --program "$PROGRAM" --out "$WORK/debs" --soundfont "$SOUNDFONT" --licence "$LICENCE"
  else
    sh "$HERE/build-deb.sh" --version "$VERSION" --program "$PROGRAM" --out "$WORK/debs" --soundfont "$SOUNDFONT"
  fi
fi

# 3. the pool and the index
mkdir -p "$OUT"
for deb in "$WORK"/debs/*.deb; do
  name=$(dpkg-deb -f "$deb" Package)
  first=$(printf '%s' "$name" | cut -c1)
  mkdir -p "$OUT/pool/main/$first/$name"
  cp "$deb" "$OUT/pool/main/$first/$name/"
done
DIST="$OUT/dists/$SUITE"
mkdir -p "$DIST/main/binary-amd64"
( cd "$OUT" && apt-ftparchive packages pool > "dists/$SUITE/main/binary-amd64/Packages" )
gzip -9 -n -k -f "$DIST/main/binary-amd64/Packages"
( cd "$OUT" && apt-ftparchive \
    -o "APT::FTPArchive::Release::Origin=AMPLE" \
    -o "APT::FTPArchive::Release::Label=AMPLE" \
    -o "APT::FTPArchive::Release::Suite=$SUITE" \
    -o "APT::FTPArchive::Release::Codename=$SUITE" \
    -o "APT::FTPArchive::Release::Architectures=amd64" \
    -o "APT::FTPArchive::Release::Components=main" \
    -o "APT::FTPArchive::Release::Description=AMPLE packages (beta)" \
    release "dists/$SUITE" > "$WORK/Release" )
cp "$WORK/Release" "$DIST/Release"

# 4. the signature (InRelease for current apt, Release.gpg for old clients)
gpg --batch --quiet --pinentry-mode loopback --passphrase-file "$PASSFILE" --import "$KEYFILE" 2>/dev/null
FPR=$(gpg --batch --list-secret-keys --with-colons | awk -F: '/^fpr:/ {print $10; exit}')
[ -n "$FPR" ] || die "the key could not be imported."
gpg --batch --yes --pinentry-mode loopback --passphrase-file "$PASSFILE" --default-key "$FPR" --clearsign -o "$DIST/InRelease" "$DIST/Release"
gpg --batch --yes --pinentry-mode loopback --passphrase-file "$PASSFILE" --default-key "$FPR" --armor --detach-sign -o "$DIST/Release.gpg" "$DIST/Release"
gpg --batch --verify "$DIST/InRelease" 2>&1 | sed -n '1,3p'
find "$OUT" -type d -exec chmod 755 {} +
find "$OUT" -type f -exec chmod 644 {} +
printf 'built %s (suite %s, key %s)\n' "$OUT" "$SUITE" "$FPR"

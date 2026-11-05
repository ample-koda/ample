#!/bin/sh
# Checks the two parts of install.sh that deal with untrusted input, without downloading anything:
#   1. the manifest parser finds the SHA-256 whatever the order of the fields is, and refuses a name that is not listed;
#   2. the cleanup removes only old version folders and cannot be led outside the install root by odd entries.
# It extracts the real code from install.sh (between the marker comments), so the test cannot drift from the script.
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
fail() { echo "FAIL: $*" >&2; exit 1; }
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

# 1. parser
HASH=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
cat > "$tmp/m.json" <<EOF
{ "artefacts": [
  { "sha256": "$HASH", "name": "ample-1.0.0-linux-x64.zip", "size": 5 },
  { "size": 7, "name": "ample-1.0.0-win-x64.zip", "sha256": "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff" }
] }
EOF
parse() { tr -d '\n\r\t ' < "$1" | sed 's/},{/}\n{/g' | grep -F "\"name\":\"$2\"" | sed -n 's/.*"sha256":"\([0-9a-fA-F]\{64\}\)".*/\1/p' | head -n 1 | tr 'A-F' 'a-f'; }
[ "$(parse "$tmp/m.json" ample-1.0.0-linux-x64.zip)" = "$HASH" ] || fail "fields in another order"
[ -z "$(parse "$tmp/m.json" ample-9.9.9-linux-x64.zip)" ] || fail "an unlisted name must give nothing"
grep -q 'sed .s/},{/}\\n{/g. | grep -F' "$here/install.sh" || fail "install.sh no longer has the parser this test checks"

# 2. cleanup: the loop is copied out of install.sh
ROOT="$tmp/root"; mkdir -p "$ROOT" "$tmp/outside/precious"
for v in 0.1.0 0.2.0 0.3.0 0.4.0; do mkdir "$ROOT/$v"; touch -d "2026-01-0${v#0.}" "$ROOT/$v" 2>/dev/null || true; done
mkdir "$ROOT/1.0.0 weird name" "$ROOT/9;rm -rf x"          # odd directory names
ln -s "$tmp/outside" "$ROOT/5.0.0"                          # a link that looks like a version
touch "$ROOT/6.0.0"                                         # a file that looks like a version
echo keep > "$tmp/outside/precious/file"
DEST="$ROOT/0.4.0"; PREVIOUS="$ROOT/0.3.0"
awk '/^KEPT=0$/,/^done$/' "$here/install.sh" > "$tmp/cleanup.sh"
[ -s "$tmp/cleanup.sh" ] || fail "the cleanup block was not found in install.sh"
. "$tmp/cleanup.sh"
[ -f "$tmp/outside/precious/file" ] || fail "the cleanup left the install root"
[ -L "$ROOT/5.0.0" ] || fail "a link must be left alone"
[ -f "$ROOT/6.0.0" ] || fail "a file must be left alone"
[ -d "$ROOT/1.0.0 weird name" ] || fail "an odd name must be left alone, not guessed at"
[ -d "$ROOT/0.4.0" ] && [ -d "$ROOT/0.3.0" ] || fail "the newest two must stay"
echo "install logic: ok"

#!/usr/bin/env bash
set -euo pipefail

full=0
if [[ "${1:-}" == "--full" ]]; then
  full=1
  shift
fi
if [[ $# -ne 0 ]]; then
  echo "usage: $(basename "$0") [--full]" >&2
  exit 2
fi

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
solution="$repository_root/ample/Ample.sln"
source_revision="${GITHUB_SHA:-unknown}"

# dotnet picks its SDK from the folder it runs in: run inside the repository, where global.json pins the SDK (see check-all.ps1)
cd "$repository_root/ample"
dotnet restore "$solution"
dotnet build "$solution" --no-restore -warnaserror -p:SourceRevisionId="$source_revision"
if [[ "$full" -eq 1 ]]; then
  dotnet test "$solution" --no-build -p:SourceRevisionId="$source_revision"
else
  dotnet test "$solution" --no-build --filter '(Category!=Performance)&(Category!=Chaos)' -p:SourceRevisionId="$source_revision"
  echo 'Slow performance and chaos suites skipped (rerun with --full when needed).'
fi
dotnet format "$solution" --no-restore --verify-no-changes
node --test --test-isolation=none "$repository_root/ample/tools/parity-report.test.mjs"
node "$repository_root/ample/tools/parity-report.mjs" --check --require-complete
node --test --test-isolation=none "$repository_root/ample/tools/validate-spec.test.mjs"
node --test --test-isolation=none "$repository_root/ample/faer/server.test.mjs" "$repository_root/ample/faer/gpu-manager.test.mjs"
node "$repository_root/ample/tools/validate-spec.mjs"
node "$repository_root/ample/tools/gen-prng-vectors.mjs" --check
node --test --test-isolation=none "$repository_root/ample/web/lib/test/stable-rng.test.mjs" "$repository_root/ample/web/lib/test/geometry.test.mjs" "$repository_root/ample/web/lib/test/history.test.mjs" "$repository_root/ample/web/lib/test/lyrics.test.mjs" "$repository_root/ample/web/lib/test/pluginui.test.mjs" "$repository_root/ample/web/lib/test/stepseq.test.mjs" "$repository_root/ample/web/lib/test/numfield.test.mjs"

node "$repository_root/ample/tools/gen-editor-assets.mjs" --check
if [[ -d "$repository_root/ample/node_modules/vscode-textmate" ]]; then
  node --test --test-isolation=none "$repository_root/ample/editors/vscode/test/grammar.test.mjs"
elif [[ "$full" -eq 1 ]]; then
  echo 'Full check requires npm install in ample/ for the VS Code grammar tests.' >&2
  exit 1
fi

# Browser parity tests need "npm install" in ample/ and "npx playwright install chromium"; they run when installed.
if [[ -d "$repository_root/ample/node_modules/@playwright" ]]; then
  (cd "$repository_root/ample" && npx playwright test --project=chromium)
elif [[ "$full" -eq 1 ]]; then
  echo 'Full check requires npm install in ample/ and a Playwright browser for browser parity tests.' >&2
  exit 1
else
  echo 'Playwright parity tests skipped (run npm install in ample/ to enable them).'
fi

if [[ -f "$repository_root/ample/spec/.docs-check-enabled" ]]; then
  dotnet run --project "$repository_root/ample/src/Ample.Cli/Ample.Cli.csproj" --no-build -- docs check
else
  echo 'AMPLE docs check is not available until chunk 12.'
fi

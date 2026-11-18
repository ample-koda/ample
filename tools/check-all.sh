#!/usr/bin/env bash
set -euo pipefail

# Older Node versions (the CI runner's) do not know --test-isolation: pass it only when this Node lists it (as check-all.ps1).
node_isolation=()
if node --help | grep -q -- '--test-isolation'; then node_isolation=(--test-isolation=none); fi

full=0
skip_tests=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --full) full=1 ;;
    --skip-tests) skip_tests=1 ;;   # build, format and the node and spec checks only: the test projects and the browser tests run in the public CI
    *) echo "usage: $(basename "$0") [--full] [--skip-tests]" >&2; exit 2 ;;
  esac
  shift
done

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
solution="$repository_root/Ample.sln"
echo "check-all: repository root $repository_root"
source_revision="${GITHUB_SHA:-unknown}"

# dotnet picks its SDK from the folder it runs in: run inside the repository, where global.json pins the SDK (see check-all.ps1)
cd "$repository_root"
dotnet restore "$solution"
dotnet build "$solution" --no-restore -warnaserror -p:SourceRevisionId="$source_revision"
if [[ "$skip_tests" -eq 1 ]]; then
  echo 'Test projects skipped (--skip-tests).'
elif [[ "$full" -eq 1 ]]; then
  dotnet test "$solution" --no-build -p:SourceRevisionId="$source_revision"
else
  dotnet test "$solution" --no-build --filter '(Category!=Performance)&(Category!=Chaos)' -p:SourceRevisionId="$source_revision"
  echo 'Slow performance and chaos suites skipped (rerun with --full when needed).'
fi
dotnet format "$solution" --no-restore --verify-no-changes
# The parity matrix and its sketch are internal documents: the public tree has neither the tool nor the matrix, so this check runs only where they are.
if [[ -f "$repository_root/tools/parity-report.mjs" && -f "$repository_root/docs/parity-matrix.md" ]]; then
  node --test ${node_isolation[@]+"${node_isolation[@]}"} "$repository_root/tools/parity-report.test.mjs"
  node "$repository_root/tools/parity-report.mjs" --check --require-complete
fi
node --test ${node_isolation[@]+"${node_isolation[@]}"} "$repository_root/tools/validate-spec.test.mjs"
node --test ${node_isolation[@]+"${node_isolation[@]}"} "$repository_root/tools/check-all.test.mjs"
node --test ${node_isolation[@]+"${node_isolation[@]}"} "$repository_root/tools/quarantine-failed.test.mjs"
node --test ${node_isolation[@]+"${node_isolation[@]}"} "$repository_root/tools/issue-sync/sync.test.mjs"
node --test ${node_isolation[@]+"${node_isolation[@]}"} "$repository_root/faer/server.test.mjs" "$repository_root/faer/gpu-manager.test.mjs"
node "$repository_root/tools/validate-spec.mjs"
node "$repository_root/tools/gen-prng-vectors.mjs" --check
node --test ${node_isolation[@]+"${node_isolation[@]}"} "$repository_root/web/lib/test/stable-rng.test.mjs" "$repository_root/web/lib/test/geometry.test.mjs" "$repository_root/web/lib/test/history.test.mjs" "$repository_root/web/lib/test/lyrics.test.mjs" "$repository_root/web/lib/test/pluginui.test.mjs" "$repository_root/web/lib/test/stepseq.test.mjs" "$repository_root/web/lib/test/numfield.test.mjs"

node "$repository_root/tools/gen-editor-assets.mjs" --check
if [[ -d "$repository_root/node_modules/vscode-textmate" ]]; then
  node --test ${node_isolation[@]+"${node_isolation[@]}"} "$repository_root/editors/vscode/test/grammar.test.mjs"
elif [[ "$full" -eq 1 ]]; then
  echo 'Full check requires npm install in the repository for the VS Code grammar tests.' >&2
  exit 1
fi

# Browser parity tests need "npm install" in ample/ and "npx playwright install chromium"; they run when installed.
if [[ "$skip_tests" -eq 1 ]]; then
  echo 'Playwright parity tests skipped (--skip-tests).'
elif [[ -d "$repository_root/node_modules/@playwright" ]]; then
  (cd "$repository_root" && npx playwright test --project=chromium)
elif [[ "$full" -eq 1 ]]; then
  echo 'Full check requires npm install in the repository and a Playwright browser for browser parity tests.' >&2
  exit 1
else
  echo 'Playwright parity tests skipped (run npm install in the repository to enable them).'
fi

if [[ -f "$repository_root/spec/.docs-check-enabled" ]]; then
  dotnet run --project "$repository_root/src/Ample.Cli/Ample.Cli.csproj" --no-build -- docs check
else
  echo 'AMPLE docs check is not available until chunk 12.'
fi

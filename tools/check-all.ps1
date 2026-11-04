param([switch]$Full)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$solution = Join-Path $repositoryRoot 'ample\Ample.sln'
$sourceRevision = if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { 'unknown' }

Push-Location -LiteralPath $repositoryRoot
try {
  dotnet restore $solution
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  dotnet build $solution --no-restore -warnaserror -p:SourceRevisionId=$sourceRevision
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  if ($Full) {
    dotnet test $solution --no-build -p:SourceRevisionId=$sourceRevision
  } else {
    dotnet test $solution --no-build --filter '(Category!=Performance)&(Category!=Chaos)' -p:SourceRevisionId=$sourceRevision
    Write-Host 'Slow performance and chaos suites skipped (rerun with -Full when needed).'
  }
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  dotnet format $solution --no-restore --verify-no-changes
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  node --test --test-isolation=none (Join-Path $repositoryRoot 'ample\tools\parity-report.test.mjs')
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  node (Join-Path $repositoryRoot 'ample\tools\parity-report.mjs') --check --require-complete
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  node --test --test-isolation=none (Join-Path $repositoryRoot 'ample\tools\validate-spec.test.mjs')
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  node --test --test-isolation=none (Join-Path $repositoryRoot 'ample\faer\server.test.mjs') (Join-Path $repositoryRoot 'ample\faer\gpu-manager.test.mjs')
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  node (Join-Path $repositoryRoot 'ample\tools\validate-spec.mjs')
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  node (Join-Path $repositoryRoot 'ample\tools\gen-prng-vectors.mjs') --check
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  node --test --test-isolation=none (Join-Path $repositoryRoot 'ample\web\lib\test\stable-rng.test.mjs') (Join-Path $repositoryRoot 'ample\web\lib\test\geometry.test.mjs') (Join-Path $repositoryRoot 'ample\web\lib\test\history.test.mjs') (Join-Path $repositoryRoot 'ample\web\lib\test\lyrics.test.mjs') (Join-Path $repositoryRoot 'ample\web\lib\test\pluginui.test.mjs') (Join-Path $repositoryRoot 'ample\web\lib\test\stepseq.test.mjs') (Join-Path $repositoryRoot 'ample\web\lib\test\numfield.test.mjs')
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  node (Join-Path $repositoryRoot 'ample\tools\gen-editor-assets.mjs') --check
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  $textMate = Join-Path $repositoryRoot 'ample\node_modules\vscode-textmate'
  if (Test-Path -LiteralPath $textMate) {
    node --test --test-isolation=none (Join-Path $repositoryRoot 'ample\editors\vscode\test\grammar.test.mjs')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
  } elseif ($Full) {
    throw 'Full check requires npm install in ample/ for the VS Code grammar tests.'
  }

  # Browser parity tests need "npm install" in ample/ and the browser ("npx playwright install chromium"); they run when installed.
  $playwright = Join-Path $repositoryRoot 'ample\node_modules\@playwright'
  if (Test-Path -LiteralPath $playwright) {
    Push-Location -LiteralPath (Join-Path $repositoryRoot 'ample')
    try {
      npx playwright test --project=chromium
      if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    } finally {
      Pop-Location
    }
  } elseif ($Full) {
    throw 'Full check requires npm install in ample/ and a Playwright browser for browser parity tests.'
  } else {
    Write-Host 'Playwright parity tests skipped (run npm install in ample/ to enable them).'
  }

  if (Test-Path -LiteralPath (Join-Path $repositoryRoot 'ample\spec\.docs-check-enabled')) {
    dotnet run --project (Join-Path $repositoryRoot 'ample\src\Ample.Cli\Ample.Cli.csproj') --no-build -- docs check
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
  } else {
    Write-Host 'AMPLE docs check is not available until chunk 12.'
  }
} finally {
  Pop-Location
}

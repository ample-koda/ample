# Builds the release artefacts of AMPLE into ample/dist/:
#   ample-<version>-<rid>.zip   self-contained single-file program (win-x64, linux-x64) with web/, spec/, docs/, plugins/ next to it
#   Ample.Cli.<version>.nupkg   the dotnet tool ("dotnet tool install --global Ample.Cli")
#   ample-vscode-<version>.vsix the VS Code extension (needs npm; skipped with -SkipVsix)
#   SHA256SUMS.txt              hashes of all of the above (and a .sig next to each file when -Sign is given and a signing key exists)
# It does not run the tests: run tools/check-all.ps1 first (the release workflow does).
param(
  [string[]] $Runtimes = @('win-x64', 'linux-x64'),
  [switch] $SkipVsix,
  [switch] $Sign
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ample = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$dist = Join-Path $ample 'dist'
$cli = Join-Path $ample 'src\Ample.Cli\Ample.Cli.csproj'
$version = ([xml](Get-Content -LiteralPath (Join-Path $ample 'Directory.Build.props'))).Project.PropertyGroup.Version
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }
Remove-Item -LiteralPath $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $dist | Out-Null
$metrics = Join-Path $dist 'package-metrics.tsv'
"step`tseconds" | Set-Content -LiteralPath $metrics -Encoding utf8

function Step([string] $name, [scriptblock] $body) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  & $body
  if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw "$name failed ($LASTEXITCODE)" }
  "$name`t$([math]::Round($sw.Elapsed.TotalSeconds, 1))" | Add-Content -LiteralPath $metrics -Encoding utf8
}

Step 'help' { dotnet run --project $cli -c Release -- docs build }

foreach ($rid in $Runtimes) {
  $stage = Join-Path $dist "stage-$rid"
  Step "publish $rid" { dotnet publish $cli -c Release -r $rid --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $stage 'bin') --nologo -v q }
  $root = Join-Path $stage "ample-$version-$rid"
  New-Item -ItemType Directory -Path $root | Out-Null
  $program = if ($rid -like 'win-*') { 'Ample.Cli.exe' } else { 'Ample.Cli' }
  Copy-Item -LiteralPath (Join-Path $stage "bin\$program") -Destination (Join-Path $root ($rid -like 'win-*' ? 'ample.exe' : 'ample'))
  foreach ($folder in 'web', 'spec', 'plugins') { Copy-Item -LiteralPath (Join-Path $ample $folder) -Destination $root -Recurse }
  Get-ChildItem -LiteralPath (Join-Path $root 'plugins') -Directory -Recurse -Filter node_modules | Remove-Item -Recurse -Force
  foreach ($testPlugin in 'echo', 'flaky', 'analysis-fake') { Remove-Item -LiteralPath (Join-Path (Join-Path $root 'plugins') $testPlugin) -Recurse -Force }
  $docs = Join-Path $root 'docs'
  New-Item -ItemType Directory -Path $docs | Out-Null
  Copy-Item -Path (Join-Path $ample 'docs\*') -Destination $docs -Recurse -Exclude 'sketch'
  Copy-Item -LiteralPath (Join-Path $ample 'README.md') -Destination $root
  Step "zip $rid" { Compress-Archive -Path $root -DestinationPath (Join-Path $dist "ample-$version-$rid.zip") -CompressionLevel Optimal }
  Remove-Item -LiteralPath $stage -Recurse -Force
}

Step 'pack tool' { dotnet pack $cli -c Release -o $dist --nologo -v q }

if (-not $SkipVsix) {
  $ext = Join-Path $ample 'editors\vscode'
  Push-Location -LiteralPath $ext
  try {
    Step 'vsix' {
      npm install --no-audit --no-fund
      if ($LASTEXITCODE -ne 0) { return }
      npx --yes @vscode/vsce package --allow-missing-repository --skip-license -o (Join-Path $dist "ample-vscode-$version.vsix")
    }
  } finally { Pop-Location }
}

if ($Sign) {
  foreach ($file in Get-ChildItem -LiteralPath $dist -File | Where-Object { $_.Extension -in '.zip', '.nupkg', '.vsix' }) {
    dotnet run --project $cli -c Release -- sign --file $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "signing $($file.Name) failed" }
  }
}

$sums = Get-ChildItem -LiteralPath $dist -File | Where-Object { $_.Extension -in '.zip', '.nupkg', '.vsix' } | ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" }
$sums | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding utf8
Get-ChildItem -LiteralPath $dist -File | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table | Out-String | Write-Host
Get-Content -LiteralPath $metrics | Write-Host

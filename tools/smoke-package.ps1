# Installer smoke test: unpacks the zip of this operating system from ample/dist/ into a clean temporary folder (no repository, no dotnet needed by the program)
# and checks that the program starts, makes and builds a project, finds its spec, plugins and web UI next to itself, and serves the help.
param([string] $Zip)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ample = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$rid = if ($IsWindows) { 'win-x64' } elseif ($IsLinux) { 'linux-x64' } else { throw 'Only Windows and Linux packages are built.' }
if (-not $Zip) { $Zip = (Get-ChildItem -LiteralPath (Join-Path $ample 'dist') -Filter "ample-*-$rid.zip" | Select-Object -First 1).FullName }
$work = Join-Path ([IO.Path]::GetTempPath()) ('ample-smoke-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
try {
  Expand-Archive -LiteralPath $Zip -DestinationPath $work
  $root = (Get-ChildItem -LiteralPath $work -Directory | Select-Object -First 1).FullName
  $exe = Join-Path $root ($IsWindows ? 'ample.exe' : 'ample')
  if (-not $IsWindows) { chmod +x $exe }
  $env:AMPLE_HOME = Join-Path $work 'home'
  Push-Location -LiteralPath $work   # not inside the repository: the program must find spec/ and web/ next to itself
  try {
    $version = & $exe --version
    if ($LASTEXITCODE -ne 0) { throw 'ample --version failed' }
    Write-Host "version: $version"
    & $exe new (Join-Path $work 'song') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'ample new failed' }
    Set-Content -LiteralPath (Join-Path $work 'song\song.amplet') -Value "project `"smoke`" { tempo 120  master-seed 7 }`nmidi-clip chords stable@1.0 at 1 for 2 {`n  chords Am F`n}`nbass bass stable@1.1 at 1 for 2 {`n  from chords.notes -> notes`n}`n"
    & $exe check (Join-Path $work 'song\song.amplet')
    if ($LASTEXITCODE -ne 0) { throw 'ample check failed' }
    & $exe build (Join-Path $work 'song\song.amplet') --project (Join-Path $work 'song')
    if ($LASTEXITCODE -ne 0) { throw 'ample build failed' }
    & $exe plugins list
    if ($LASTEXITCODE -ne 0) { throw 'ample plugins list failed' }
    $serve = Start-Process -FilePath $exe -ArgumentList @('serve', '--project', (Join-Path $work 'song'), '--no-browser') -RedirectStandardOutput (Join-Path $work 'serve.out') -NoNewWindow -PassThru
    try {
      $url = $null
      for ($i = 0; $i -lt 60 -and -not $url; $i++) {
        Start-Sleep -Milliseconds 500
        if (Test-Path (Join-Path $work 'serve.out')) { $m = [regex]::Match((Get-Content -LiteralPath (Join-Path $work 'serve.out') -Raw), 'Open: (\S+)'); if ($m.Success) { $url = $m.Groups[1].Value } }
      }
      if (-not $url) { throw 'ample serve did not start' }
      $base = $url -replace '\?.*$', ''
      $page = Invoke-WebRequest -Uri $url -SessionVariable session -UseBasicParsing
      if ($page.StatusCode -ne 200 -or $page.Content -notmatch 'AMPLE') { throw 'the web UI was not served' }
      $help = Invoke-WebRequest -Uri ($base.TrimEnd('/') + '/help') -WebSession $session -UseBasicParsing
      if ($help.StatusCode -ne 200) { throw 'the help was not served' }
      Write-Host "serve: web UI and help answer at $base"
    } finally { Stop-Process -Id $serve.Id -Force -ErrorAction SilentlyContinue }
  } finally { Pop-Location }
  Write-Host 'smoke test passed'
} finally {
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

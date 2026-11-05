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

# A zip made with .NET, so that a Linux zip keeps its Unix modes (Compress-Archive stores none, and `unzip` then gives a program that cannot be run).
# The entries are the top-level folder itself and everything below it, with '/' separators. Modes (Linux only): the program, the Node program of a pack and *.sh 0755, other files 0644, folders 0755.
function New-ZipTree([string] $Folder, [string] $ZipPath, [bool] $UnixModes) {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $Folder = (Resolve-Path -LiteralPath $Folder).Path.TrimEnd('\', '/')
  $parent = Split-Path -Parent $Folder
  if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
  $stream = [IO.File]::Open($ZipPath, [IO.FileMode]::CreateNew)
  try {
    $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
      $items = @(Get-Item -LiteralPath $Folder -Force) + @(Get-ChildItem -LiteralPath $Folder -Recurse -Force)
      foreach ($item in $items) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing to pack a link: $($item.FullName)" }
        $name = $item.FullName.Substring($parent.Length + 1).Replace('\', '/')
        if ($item.PSIsContainer) {
          $entry = $zip.CreateEntry($name + '/')
          if ($UnixModes) { $entry.ExternalAttributes = ((0x4000 -bor 0x1ED) -shl 16) }   # directory, 0755
        } else {
          $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
          if ($UnixModes) {
            $executable = ($item.Name -in 'ample', 'node') -or ($item.Extension -eq '.sh')
            $mode = if ($executable) { 0x1ED } else { 0x1A4 }                                  # 0755 or 0644
            $entry.ExternalAttributes = ((0x8000 -bor $mode) -shl 16)                         # regular file
          }
          $in = [IO.File]::OpenRead($item.FullName)
          try { $out = $entry.Open(); try { $in.CopyTo($out) } finally { $out.Dispose() } } finally { $in.Dispose() }
        }
      }
    } finally { $zip.Dispose() }
  } finally { $stream.Dispose() }
  if ($UnixModes) { Set-ZipHostUnix $ZipPath }
}

# .NET writes "made by: FAT" on Windows, and then `unzip` ignores the Unix modes stored in the entries. This marks every entry of the zip as made on Unix (host 3).
# It walks the central directory exactly as the zip format describes it and refuses anything it does not fully understand (a zip64 archive, a bad signature, a count that does not add up).
function Set-ZipHostUnix([string] $ZipPath) {
  $bytes = [IO.File]::ReadAllBytes($ZipPath)
  $eocd = -1
  for ($i = $bytes.Length - 22; $i -ge [Math]::Max(0, $bytes.Length - 22 - 65535); $i--) {
    if ($bytes[$i] -eq 0x50 -and $bytes[$i + 1] -eq 0x4B -and $bytes[$i + 2] -eq 0x05 -and $bytes[$i + 3] -eq 0x06) { $eocd = $i; break }
  }
  if ($eocd -lt 0) { throw "No end-of-central-directory record in $ZipPath." }
  $count = [BitConverter]::ToUInt16($bytes, $eocd + 10)
  $cdSize = [BitConverter]::ToUInt32($bytes, $eocd + 12)
  $cdOffset = [BitConverter]::ToUInt32($bytes, $eocd + 16)
  if ($count -eq 0xFFFF -or $cdSize -eq 0xFFFFFFFF -or $cdOffset -eq 0xFFFFFFFF) { throw 'A zip64 archive: not handled by Set-ZipHostUnix.' }
  $pos = [int]$cdOffset
  for ($n = 0; $n -lt $count; $n++) {
    if ($pos + 46 -gt $bytes.Length -or $bytes[$pos] -ne 0x50 -or $bytes[$pos + 1] -ne 0x4B -or $bytes[$pos + 2] -ne 0x01 -or $bytes[$pos + 3] -ne 0x02) { throw "Central directory entry $n has a bad signature." }
    $bytes[$pos + 5] = 3   # the high byte of "version made by" is the host system: 3 = Unix
    $pos += 46 + [BitConverter]::ToUInt16($bytes, $pos + 28) + [BitConverter]::ToUInt16($bytes, $pos + 30) + [BitConverter]::ToUInt16($bytes, $pos + 32)
  }
  if ($pos -ne $cdOffset + $cdSize) { throw 'The central directory does not end where the end record says it does.' }
  [IO.File]::WriteAllBytes($ZipPath, $bytes)
}

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
  Remove-Item -LiteralPath (Join-Path (Join-Path $root 'web') 'remix') -Recurse -Force   # Remix is not part of the installers (plan/installer 4.13)
  Get-ChildItem -LiteralPath (Join-Path $root 'plugins') -Directory -Recurse -Filter node_modules | Remove-Item -Recurse -Force
  foreach ($testPlugin in 'echo', 'flaky', 'analysis-fake') { Remove-Item -LiteralPath (Join-Path (Join-Path $root 'plugins') $testPlugin) -Recurse -Force }
  $docs = Join-Path $root 'docs'
  New-Item -ItemType Directory -Path $docs | Out-Null
  Copy-Item -Path (Join-Path $ample 'docs\*') -Destination $docs -Recurse -Exclude 'sketch'
  Copy-Item -LiteralPath (Join-Path $ample 'README.md') -Destination $root
  Step "zip $rid" { New-ZipTree $root (Join-Path $dist "ample-$version-$rid.zip") ($rid -like 'linux-*') }
  Remove-Item -LiteralPath $stage -Recurse -Force
}

# The optional pack koda-browser (Koda's browser): the Playwright driver for ONE platform (the Node program) and the Playwright package folder, nothing else.
# It is not part of the program or of its other packages; `ample packs install koda-browser` fetches it through the release manifest.
$playwrightVersion = ([xml](Get-Content -LiteralPath (Join-Path $ample 'Directory.Packages.props'))).Project.ItemGroup.PackageVersion | Where-Object { $_.Include -eq 'Microsoft.Playwright' } | ForEach-Object { $_.Version }
if (-not $playwrightVersion) { throw 'No Microsoft.Playwright version in Directory.Packages.props.' }
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages' }
$driverSource = Join-Path $nugetRoot "microsoft.playwright\$playwrightVersion\.playwright"
if (-not (Test-Path -LiteralPath (Join-Path $driverSource 'package\package.json'))) { throw "The Playwright driver was not found in '$driverSource' (restore the solution first)." }
foreach ($rid in $Runtimes) {
  $nodeFolder = switch ($rid) { 'win-x64' { 'win32_x64' } 'linux-x64' { 'linux-x64' } default { throw "No koda-browser pack is built for $rid." } }
  Step "koda-browser $rid" {
    $stage = Join-Path $dist "stage-koda-browser-$rid"
    $target = Join-Path $stage '.playwright'
    New-Item -ItemType Directory -Path (Join-Path $target 'node') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $driverSource "node\$nodeFolder") -Destination (Join-Path $target 'node') -Recurse
    Copy-Item -LiteralPath (Join-Path $driverSource 'package') -Destination $target -Recurse
    New-ZipTree $target (Join-Path $dist "ample-koda-browser-$rid.zip") ($rid -like 'linux-*')   # the node program of a Linux pack keeps its mode too (the installer also sets it)
    Remove-Item -LiteralPath $stage -Recurse -Force
  }
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

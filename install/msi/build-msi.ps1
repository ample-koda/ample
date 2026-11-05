# Builds the per-user Windows installer (MSI) of AMPLE from the win-x64 release zip.
#
#   pwsh install/msi/build-msi.ps1 -Zip dist/ample-0.1.0-win-x64.zip -Out dist/ample-0.1.0-win-x64.msi
#
# Needs the WiX 4.0.6 command line tool (MS-RL, no maintenance-fee terms) and its UI and Util extensions:
#   dotnet tool install wix --version 4.0.6 --tool-path <folder>
#   <folder>\wix.exe extension add -g WixToolset.UI.wixext/4.0.6
#   <folder>\wix.exe extension add -g WixToolset.Util.wixext/4.0.6
# The path of wix.exe comes from -Wix or the environment variable AMPLE_WIX.
param(
  [Parameter(Mandatory)][string]$Zip,
  [Parameter(Mandatory)][string]$Out,
  [string]$Wix = $env:AMPLE_WIX,
  [string]$License = ''   # the licence text (the LICENSE file of the repository); the wizard shows it
)
$ErrorActionPreference = 'Stop'
if (-not $Wix) { $Wix = (Get-Command wix -ErrorAction SilentlyContinue).Source }
if (-not $Wix -or -not (Test-Path -LiteralPath $Wix)) { throw 'wix.exe was not found: pass -Wix or set AMPLE_WIX.' }
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $License) { $License = Join-Path $here '..\..\LICENSE' }
if (-not (Test-Path -LiteralPath $License)) { throw "The licence file '$License' was not found: give -License (the repository has LICENSE at its root)." }
$zipName = [IO.Path]::GetFileName($Zip)
if ($zipName -notmatch '^ample-(\d+\.\d+\.\d+)(-[0-9A-Za-z.]+)?-win-x64\.zip$') { throw "'$zipName' is not an ample-<version>-win-x64.zip." }
$version = $Matches[1]   # the MSI version is numeric (a.b.c); a pre-release suffix has no place in it

$stage = Join-Path ([IO.Path]::GetTempPath()) ('ample-msi-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
  New-Item -ItemType Directory -Path $stage | Out-Null
  Expand-Archive -LiteralPath $Zip -DestinationPath $stage
  $top = @(Get-ChildItem -LiteralPath $stage -Directory)
  if ($top.Count -ne 1 -or -not (Test-Path -LiteralPath (Join-Path $top[0].FullName 'ample.exe'))) { throw 'The zip does not hold one folder with ample.exe.' }
  $source = $top[0].FullName
  [IO.File]::WriteAllText((Join-Path $source 'install.json'), ('{ "how": "msi", "scope": "user", "version": "' + $version + '" }' + "`n"), (New-Object Text.UTF8Encoding $false))
  # the licence page of the wizard: the LICENSE text plus two short notes, as RTF (escaped: backslash, braces; one paragraph per line)
  function ConvertTo-Rtf([string]$text) { ($text -replace '\\', '\\\\' -replace '\{', '\{' -replace '\}', '\}') -replace "`r?`n", "\par`n" }
  $licenseText = (Get-Content -LiteralPath $License -Raw -Encoding utf8).TrimEnd()
  $rtf = '{\rtf1\ansi\deff0{\fonttbl{\f0\fswiss Segoe UI;}}\f0\fs18' + "`n" + (ConvertTo-Rtf $licenseText) + '\par\par' + "`n" +
    '{\b The General MIDI SoundFont} that this installer can fetch (MuseScore General) has its own licence (MIT) and is downloaded from its own source, checked against a fixed SHA-256.\par\par' + "`n" +
    '{\b AMPLE is beta:} it works, and anything may still change, including the file formats, the sign-in and the server set-up.\par' + "`n}"
  [IO.File]::WriteAllText((Join-Path $stage 'license.rtf'), $rtf, (New-Object Text.ASCIIEncoding))
  $caOut = Join-Path $stage 'ca'
  & dotnet build (Join-Path $here 'CustomActions/AmpleCa.csproj') -nologo -v q -c Release -o $caOut | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'the custom action did not build' }
  $outFull = [IO.Path]::GetFullPath($Out)
  & $Wix build (Join-Path $here 'Package.wxs') -arch x64 -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext `
    -d "Version=$version" -d "SourceDir=$source" -d "CaDll=$(Join-Path $caOut 'AmpleCa.CA.dll')" -loc (Join-Path $here 'en-us.wxl') -bindpath $here -bindpath $stage -o $outFull   # (ICE validation is a separate step: wix msi validate)
  if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }
  $item = Get-Item -LiteralPath $outFull
  "built $($item.FullName) ($([math]::Round($item.Length / 1MB, 1)) MB, version $version)"
}
finally { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }

<#
.SYNOPSIS
  AMPLE installer for Windows: per user by default, or for everyone with -System (from an elevated PowerShell).

.DESCRIPTION
  AMPLE is beta: it works, and anything may still change, including the file formats, the sign-in and the server set-up.

    powershell -ExecutionPolicy Bypass -File install.ps1 [-Version 0.9.2] [-Channel stable|unstable] [-System] [-NoPacks]

  What it does: finds the release, downloads the zip, checks its SHA-256 against the release manifest, checks the manifest's
  signature against the release keys built into AMPLE, unpacks next to earlier versions and switches the `current` junction.
  It switches on nothing: no service, no network address, no sign-in. The only system setting it changes is the PATH, to make
  the `ample` command available (a new terminal is needed to see it).

  What the signature check does and does not protect (read this):
    - On an UPDATE the program that is already installed checks the new manifest BEFORE anything new is unpacked.
    - On a FIRST install there is no program you already trust, so the new program checks the manifest itself. That catches a
      damaged or wrongly built release, but NOT a compromised download server (which could serve its own program and manifest).
      Your protection there is HTTPS to github.com. To check by hand, compare the fingerprints in keys/README.md of the
      repository with the key id that `ample verify --file <manifest> --release` prints.

  Per user:  %LOCALAPPDATA%\Programs\AMPLE\<version>, junction ...\current, PATH of the user gets ...\current
  System:    %ProgramFiles%\AMPLE\<version>, junction ...\current, PATH of the machine gets ...\current (needs an elevated
             PowerShell; this script never elevates itself and AMPLE is never run elevated by it). The SoundFont goes to
             %ProgramData%\AMPLE\packs, read-only for users, searched after each person's own folder.
  Testing:   AMPLE_RELEASE_BASE=<url or file:// url> reads <base>/ample-<version>.manifest.json and the zip from there;
             AMPLE_INSTALL_ROOT=<folder> installs there instead, and then the PATH is left alone (per-user only).
#>
[CmdletBinding()]
param(
  [string]$Version = '',
  [ValidateSet('stable', 'unstable')][string]$Channel = 'stable',
  [switch]$System,
  [switch]$NoPacks
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$Repo = 'ample-koda/ample'
function Say([string]$Message) { Write-Host $Message }
function Fail([string]$Message) { throw "install: $Message" }
function IsLink([string]$Path) { $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue; return ($null -ne $item) -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) }
function Prop($Object, [string]$Name) { if ($null -eq $Object) { return $null }; $p = $Object.PSObject.Properties[$Name]; if ($null -eq $p) { return $null }; return $p.Value }
function VersionOk([string]$Text) { return ($Text -match '^[0-9][0-9A-Za-z.+-]*$') -and (-not $Text.Contains('..')) }

# --- what we are running on, checked instead of assumed
if ($env:OS -ne 'Windows_NT') { Fail 'This installer is for Windows. On Linux use install.sh.' }
$isX64 = ($env:PROCESSOR_ARCHITECTURE -eq 'AMD64') -or ($env:PROCESSOR_ARCHITEW6432 -eq 'AMD64')
if ((-not [Environment]::Is64BitOperatingSystem) -or (-not $isX64)) { Fail 'Only 64-bit Windows on x64 is built so far.' }
$Rid = 'win-x64'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { Fail 'TLS 1.2 could not be enabled for downloads.' }
if ($Version -ne '' -and -not (VersionOk $Version)) { Fail "'$Version' is not a version." }

$testRoot = $env:AMPLE_INSTALL_ROOT
$testBase = $env:AMPLE_RELEASE_BASE
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($testRoot) {
  if ($System) { Fail 'AMPLE_INSTALL_ROOT is for per-user tests only; it cannot be combined with -System.' }
  if (-not [IO.Path]::IsPathRooted($testRoot)) { Fail 'AMPLE_INSTALL_ROOT must be a full path.' }
  $Root = [IO.Path]::GetFullPath($testRoot)
} elseif ($System) {
  if (-not $isAdmin) { Fail 'A system install needs an elevated PowerShell (Run as administrator). This script does not elevate itself.' }
  if (-not $env:ProgramFiles) { Fail 'ProgramFiles is not set.' }
  $Root = Join-Path $env:ProgramFiles 'AMPLE'
} else {
  if ($isAdmin) { Fail 'Do not install elevated for one user. Run it as yourself, or use -System to install for everyone.' }
  if (-not $env:LOCALAPPDATA -or -not (Test-Path -LiteralPath $env:LOCALAPPDATA)) { Fail 'LOCALAPPDATA is not set to an existing folder.' }
  $Root = Join-Path $env:LOCALAPPDATA 'Programs\AMPLE'
}

Say 'AMPLE is beta: it works, and anything may still change, including the file formats, the sign-in and the server set-up.'

# --- downloads: https from GitHub; file:// only for tests (AMPLE_RELEASE_BASE)
function Fetch([string]$Url, [string]$To) {
  if ($Url.StartsWith('file://', [StringComparison]::OrdinalIgnoreCase)) {
    if (-not $testBase) { Fail "A file:// address is only allowed for tests: $Url" }
    Copy-Item -LiteralPath ([Uri]$Url).LocalPath -Destination $To -ErrorAction Stop
  } elseif ($Url.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) {
    Invoke-WebRequest -Uri $Url -OutFile $To -UseBasicParsing -ErrorAction Stop
  } else {
    Fail "Only https addresses are downloaded: $Url"
  }
}

# --- 1. which release
if ($Version -eq '') {
  if ($testBase) { Fail 'AMPLE_RELEASE_BASE needs -Version.' }
  if ($Channel -eq 'stable') {
    $tag = (Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -UseBasicParsing).tag_name
  } else {
    $list = @(Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases?per_page=30" -UseBasicParsing)
    $tag = ($list | Where-Object { $_.tag_name -match '^ample-v.+-.+' } | Select-Object -First 1).tag_name
  }
  if (-not $tag -or -not ($tag -match '^ample-v(.+)$')) { Fail "No $Channel release was found. Pass -Version." }
  $Version = $Matches[1]
  if (-not (VersionOk $Version)) { Fail "The release list gave '$Version', which is not a version." }
}
$Base = if ($testBase) { $testBase.TrimEnd('/') } else { "https://github.com/$Repo/releases/download/ample-v$Version" }
$ZipName = "ample-$Version-$Rid.zip"
Say "Installing AMPLE $Version ($Rid) into $Root"

$Tmp = Join-Path ([IO.Path]::GetTempPath()) ('ample-install-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $Tmp | Out-Null
$Stage = $null
try {
  $Manifest = Join-Path $Tmp "ample-$Version.manifest.json"
  try { Fetch "$Base/ample-$Version.manifest.json" $Manifest } catch { Fail "The release manifest could not be downloaded from $Base ($($_.Exception.Message))." }
  try { Fetch "$Base/ample-$Version.manifest.json.sig" "$Manifest.sig" } catch { Fail 'The manifest signature could not be downloaded.' }

  # --- 2. a program that is already installed (and is ours) checks the manifest before anything new is unpacked
  $Trusted = $false
  $installedExe = Join-Path $Root 'current\ample.exe'
  if (Test-Path -LiteralPath $installedExe -PathType Leaf) {
    # (an installed program from before this check existed cannot do it; then the new program does, as on a first install)
    $help = (& $installedExe verify --help 2>&1 | Out-String)
    if ($help.Contains('--release')) {
      & $installedExe verify --file $Manifest --release *> $null
      if ($LASTEXITCODE -ne 0) { Fail 'The manifest is not signed by an AMPLE release key (checked by the installed program). Nothing was changed.' }
      $Trusted = $true
    }
  }

  # the manifest must be for the version that was asked for
  try { $m = Get-Content -LiteralPath $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json } catch { Fail 'The manifest is not valid JSON. Nothing was changed.' }
  if ((Prop $m 'version') -ne $Version) { Fail "The manifest is not for version $Version. Nothing was changed." }

  # --- 3. the zip, checked against the manifest (a real JSON parser: the order of fields does not matter)
  $artefact = @(Prop $m 'artefacts') | Where-Object { (Prop $_ 'name') -eq $ZipName } | Select-Object -First 1
  if (-not $artefact -or -not ("$(Prop $artefact 'sha256')" -match '^[0-9a-fA-F]{64}$')) { Fail "$ZipName is not listed in the manifest (or has no SHA-256)." }
  $Zip = Join-Path $Tmp $ZipName
  try { Fetch "$Base/$ZipName" $Zip } catch { Fail "$ZipName could not be downloaded." }
  $actual = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash
  if ($actual -ine (Prop $artefact 'sha256')) { Fail 'The download does not match the manifest (SHA-256). Nothing was changed.' }

  # --- 4. look before touching the target: nothing of ours may be replaced by accident
  $Current = Join-Path $Root 'current'
  if ((Test-Path -LiteralPath $Current) -and -not (IsLink $Current)) { Fail "$Current exists and is not a junction; leaving it alone. Move it away and run again." }
  if ((Test-Path -LiteralPath $Root) -and -not (Get-Item -LiteralPath $Root -Force).PSIsContainer) { Fail "$Root exists and is not a folder." }
  New-Item -ItemType Directory -Path $Root -Force | Out-Null

  $Dest = Join-Path $Root $Version
  if (Test-Path -LiteralPath $Dest) {
    # an earlier install of the same version: use it only if it is whole and is that version
    $marker = Join-Path $Dest 'install.json'
    $good = (-not (IsLink $Dest)) -and (Test-Path -LiteralPath (Join-Path $Dest 'ample.exe') -PathType Leaf) -and (Test-Path -LiteralPath $marker -PathType Leaf)
    if ($good) { try { $good = ((Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json).version -eq $Version) } catch { $good = $false } }
    if ($good) { & (Join-Path $Dest 'ample.exe') --version *> $null; $good = ($LASTEXITCODE -eq 0) }
    if (-not $good) { Fail "$Dest exists but is not a complete install of $Version. Move it away and run again." }
    Say "Version $Version is already installed in $Dest; keeping it."
  } else {
    $Stage = Join-Path $Root ".installing-$Version-$PID"
    if (Test-Path -LiteralPath $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
    New-Item -ItemType Directory -Path $Stage | Out-Null
    # every entry is checked BEFORE anything is unpacked: no absolute path, no drive, no '..', a sane size
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
      $total = 0L
      foreach ($entry in $archive.Entries) {
        $name = $entry.FullName
        $segments = $name -split '[\\/]'
        if ($name -match '^[\\/]' -or $name.Contains(':') -or ($segments -contains '..') -or $name.IndexOf([char]0) -ge 0) { Fail "The zip holds an unsafe path ('$name'). Nothing was installed." }
        $total += $entry.Length
      }
      if ($total -gt 2GB) { Fail 'The zip would unpack to more than 2 GB. Nothing was installed.' }
    } finally { $archive.Dispose() }
    try { Expand-Archive -LiteralPath $Zip -DestinationPath $Stage -ErrorAction Stop } catch { Fail "The zip could not be unpacked ($($_.Exception.Message))." }
    # nothing may have been written outside the staging folder, and nothing may be a link
    $stageFull = [IO.Path]::GetFullPath($Stage).TrimEnd('\') + '\'
    $all = @(Get-ChildItem -LiteralPath $Stage -Recurse -Force)
    foreach ($item in $all) {
      if (-not ([IO.Path]::GetFullPath($item.FullName)).StartsWith($stageFull, [StringComparison]::OrdinalIgnoreCase)) { Fail 'The zip holds a path outside its own folder. Nothing was installed.' }
      if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Fail 'The zip holds a link. Nothing was installed.' }
    }
    # the zip must hold exactly one top-level folder with the program in it
    $top = @(Get-ChildItem -LiteralPath $Stage -Force)
    if ($top.Count -ne 1 -or -not $top[0].PSIsContainer -or -not (Test-Path -LiteralPath (Join-Path $top[0].FullName 'ample.exe') -PathType Leaf)) { Fail 'The zip does not have the expected shape (one folder with the program).' }
    $Inner = $top[0].FullName
    if (-not $Trusted) {
      & (Join-Path $Inner 'ample.exe') verify --file $Manifest --release
      if ($LASTEXITCODE -ne 0) { Fail 'The manifest is not signed by an AMPLE release key. Nothing was changed.' }
      Say 'First install: the manifest was checked by the new program itself (see the notes at the top of this script).'
    }
    $scope = if ($System) { 'system' } else { 'user' }
    $json = (@{ how = 'script'; scope = $scope; channel = $Channel; version = $Version } | ConvertTo-Json)
    [IO.File]::WriteAllText((Join-Path $Inner 'install.json'), $json + "`n", (New-Object Text.UTF8Encoding $false))
    Move-Item -LiteralPath $Inner -Destination $Dest   # same volume as the staging folder: a rename
    Remove-Item -LiteralPath $Stage -Recurse -Force
    $Stage = $null
  }

  # --- 5. switch `current`, keep the previous one, tidy the older ones (keep the newest two)
  $Previous = $null
  if (IsLink $Current) { $Previous = @((Get-Item -LiteralPath $Current -Force).Target)[0] }
  if (IsLink $Current) { [IO.Directory]::Delete($Current, $false) }   # removes the link only, never what it points to
  New-Item -ItemType Junction -Path $Current -Target $Dest | Out-Null
  # Only real directories that look like a version are candidates (never a link, never anything else in the folder).
  $kept = 0
  foreach ($dir in (Get-ChildItem -LiteralPath $Root -Directory -Force | Where-Object { $_.Name -match '^[0-9][0-9A-Za-z.+-]*$' -and -not $_.Name.Contains('..') } | Sort-Object LastWriteTime -Descending)) {
    if (($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
    $kept++
    if ($kept -le 2) { continue }
    if ($Previous -and ([IO.Path]::GetFullPath($dir.FullName) -ieq [IO.Path]::GetFullPath($Previous))) { continue }
    if ([IO.Path]::GetFullPath($dir.FullName) -ieq [IO.Path]::GetFullPath($Dest)) { continue }
    Remove-Item -LiteralPath $dir.FullName -Recurse -Force
  }

  # --- the command on the PATH (registry value kept as it was: %VARIABLES% stay unexpanded)
  $exe = Join-Path $Current 'ample.exe'
  if ($testRoot) {
    Say "Test install: the PATH was left alone. The program is $exe"
  } else {
    $key = if ($System) { 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' } else { 'HKCU:\Environment' }
    $reg = Get-Item -LiteralPath $key
    $old = [string]$reg.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $parts = @($old -split ';' | Where-Object { $_ -ne '' })
    if (-not ($parts | Where-Object { $_.TrimEnd('\') -ieq $Current })) {
      Set-ItemProperty -LiteralPath $key -Name 'Path' -Value (($parts + $Current) -join ';') -Type ExpandString
      Say "Added $Current to the $(if ($System) { 'machine' } else { 'user' }) PATH. Open a new terminal to use 'ample'."
    }
  }
  & $exe --version
  if ($LASTEXITCODE -ne 0) { Fail 'The installed program does not start.' }

  # --- 6. the SoundFont (required)
  if (-not $NoPacks) {
    if ($System) {
      $sf = Prop (Prop $m 'packs') 'soundfont'
      if (-not $sf -or (Prop $sf 'name') -ne 'MuseScore_General.sf2' -or -not ("$(Prop $sf 'sha256')" -match '^[0-9a-fA-F]{64}$') -or -not "$(Prop $sf 'url')") { Fail 'The manifest does not name the SoundFont (url and SHA-256).' }
      if (-not ("$($sf.url)".StartsWith('https://', [StringComparison]::OrdinalIgnoreCase) -or ($testBase -and "$($sf.url)".StartsWith('file://', [StringComparison]::OrdinalIgnoreCase)))) { Fail "The SoundFont url '$($sf.url)' is not https." }
      $packs = Join-Path $env:ProgramData 'AMPLE\packs\licensed\musescore-general'
      $target = Join-Path $packs $sf.name
      if (Test-Path -LiteralPath $target) {
        if ((IsLink $target) -or -not (Test-Path -LiteralPath $target -PathType Leaf)) { Fail "$target exists and is not a plain file; leaving it alone." }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ine $sf.sha256) { Fail "$target exists but does not match the pinned SHA-256; leaving it alone. Move it away and run again." }
        Say "The system SoundFont is already in place and verified: $target"
      } else {
        Say 'Fetching the General MIDI SoundFont for everyone (about 205 MB, checked by SHA-256)...'
        $sfTmp = Join-Path $Tmp $sf.name
        try { Fetch "$($sf.url)" $sfTmp } catch { Fail "The SoundFont could not be downloaded from $($sf.url)." }
        if ((Get-Item -LiteralPath $sfTmp).Length -gt 512MB) { Fail 'The SoundFont is larger than the allowed 512 MB. Nothing was installed.' }
        if ((Get-FileHash -LiteralPath $sfTmp -Algorithm SHA256).Hash -ine $sf.sha256) { Fail 'The SoundFont does not match the pinned SHA-256. Nothing was installed.' }
        New-Item -ItemType Directory -Path $packs -Force | Out-Null
        # read-only for users: administrators and SYSTEM full control, users read and execute (by well-known SIDs, not by names)
        # (only the packs folder: nothing else under %ProgramData%\AMPLE may lose its permissions)
        & icacls.exe (Join-Path $env:ProgramData 'AMPLE\packs') /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail 'The permissions of the system pack folder could not be set. Nothing was installed.' }
        Copy-Item -LiteralPath $sfTmp -Destination "$target.part"
        Move-Item -LiteralPath "$target.part" -Destination $target
        Say "Installed: $target  (a person can override it with: ample packs install soundfont)"
      }
    } else {
      Say 'Fetching the General MIDI SoundFont (about 205 MB, checked by SHA-256)...'
      & $exe packs install soundfont
      if ($LASTEXITCODE -ne 0) { Fail 'AMPLE is installed, but the SoundFont is required and was not installed. Run again when you are online:  ample packs install soundfont' }
    }
  }
  Say 'Check the installation any time with:  ample doctor --env'
}
finally {
  if ($Stage -and (Test-Path -LiteralPath $Stage)) { Remove-Item -LiteralPath $Stage -Recurse -Force -ErrorAction SilentlyContinue }
  Remove-Item -LiteralPath $Tmp -Recurse -Force -ErrorAction SilentlyContinue
}

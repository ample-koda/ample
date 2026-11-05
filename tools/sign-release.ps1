# Signs release files with the AMPLE release key, then checks them against the keys built into the program.
#
#   pwsh tools/sign-release.ps1 -Files dist/ample-0.9.2.manifest.json, dist/ample-0.9.2-win-x64.zip
#
# The key's passphrase is read from a file and handed to the signing process through the environment of that
# one process. It is never printed, never an argument and never written anywhere. The key and the passphrase
# file stay outside the repository: give them with -KeyHome and -PassphraseFile, or set AMPLE_RELEASE_KEY_HOME and
# AMPLE_RELEASE_KEY_PASSPHRASE_FILE once (there is no default location).
param(
  [Parameter(Mandatory)][string[]]$Files,
  [string]$KeyHome = $env:AMPLE_RELEASE_KEY_HOME,
  [string]$PassphraseFile = $env:AMPLE_RELEASE_KEY_PASSPHRASE_FILE,
  [string]$Cli
)
$ErrorActionPreference = 'Stop'
if (-not $KeyHome -or -not $PassphraseFile) { throw 'Give -KeyHome and -PassphraseFile, or set AMPLE_RELEASE_KEY_HOME and AMPLE_RELEASE_KEY_PASSPHRASE_FILE.' }
if (-not (Test-Path -LiteralPath $KeyHome -PathType Container)) { throw "The key folder '$KeyHome' was not found." }
if (-not (Test-Path -LiteralPath $PassphraseFile)) { throw "The passphrase file '$PassphraseFile' is missing; stop and ask the owner." }
if (-not $Cli) { $Cli = Join-Path $PSScriptRoot '..\src\Ample.Cli\bin\Release\net9.0\Ample.Cli.dll' }
if (-not (Test-Path -LiteralPath $Cli)) { throw "The program '$Cli' was not found; build it first or pass -Cli." }
function Ample { if ($Cli -like '*.dll') { & dotnet $Cli @args } else { & $Cli @args } }
try {
  $env:AMPLE_VAULT_PASSPHRASE = (Get-Content -LiteralPath $PassphraseFile -Raw).Trim()
  foreach ($file in $Files) {
    Ample sign --file $file --home $KeyHome
    if ($LASTEXITCODE -ne 0) { throw "signing $file failed" }
  }
}
finally {
  Remove-Item Env:AMPLE_VAULT_PASSPHRASE -ErrorAction SilentlyContinue
}
foreach ($file in $Files) {
  Ample verify --file $file --release
  if ($LASTEXITCODE -ne 0) { throw "$file does not verify against the keys built into the program" }
}

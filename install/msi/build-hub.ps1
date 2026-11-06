# Builds the two small programs of the MSI into one folder: the tray hub (Ample.Hub.exe, a framework-dependent single file that needs the
# .NET 9 Desktop Runtime) and the mini wizard (Ample.Setup.exe, .NET Framework 4.7.2: starts on every Windows 10/11).
#
#   pwsh install/msi/build-hub.ps1 -Out dist/hub
#   pwsh install/msi/build-msi.ps1 -Zip dist/ample-0.1.3-win-x64.zip -Out dist/ample-0.1.3-win-x64.msi -HubDir dist/hub
param([Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$target = [IO.Path]::GetFullPath($Out)
Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $target | Out-Null
& dotnet publish (Join-Path $repo 'src\Ample.Hub\Ample.Hub.csproj') -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -o $target -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'the hub did not build' }
& dotnet publish (Join-Path $repo 'src\Ample.Setup\Ample.Setup.csproj') -c Release -o $target -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'the setup wizard did not build' }
foreach ($need in 'Ample.Hub.exe', 'Ample.Setup.exe') { if (-not (Test-Path -LiteralPath (Join-Path $target $need))) { throw "$need is missing from the output." } }
Get-ChildItem -LiteralPath $target -File | ForEach-Object { '{0}  {1:N0} bytes' -f $_.Name, $_.Length }

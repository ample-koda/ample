@echo off
setlocal
set "result="
pushd "%~dp0\.."
if not exist node_modules\.bin\playwright.cmd goto missingPlaywright
echo === Install or verify the licensed GM SoundFont used by MIDI playback ===
node tools\fetch-gm-soundfont.mjs --download
if errorlevel 1 goto failed
node tools\fetch-gm-soundfont.mjs --verify
if errorlevel 1 goto failed
if not exist "audio-packs\licensed\musescore-general\fetch-gm-soundfont-metrics.tsv" goto missingMetrics
echo === AMPLE full check suite, including Playwright, slow suites and docs check ===
pwsh -NoProfile -ExecutionPolicy Bypass -File tools\check-all.ps1 -Full
if errorlevel 1 if not defined result set "result=%errorlevel%"
echo === MIDI note comparison unit tests ===
node --test web\lib\test\midi-compare.test.mjs
if errorlevel 1 if not defined result set "result=%errorlevel%"
echo === MIDI focused engine tests ===
dotnet test tests\Ample.Engine.Tests\Ample.Engine.Tests.csproj --filter Category=Midi
if errorlevel 1 if not defined result set "result=%errorlevel%"
echo === SF2 range and percussion regression tests ===
dotnet test tests\Ample.Audio.Tests\Ample.Audio.Tests.csproj --no-build --filter "FullyQualifiedName~Sf2ReaderTests.Skips_preset_zones_with_empty_key_or_velocity_intersections|FullyQualifiedName~Sf2ReaderTests.Uses_one_shot_playback_when_zone_offsets_put_loop_points_outside_the_sample|FullyQualifiedName~SamplerTests.SoundFont_GM_renderer_routes_midi_channel_ten_to_the_percussion_bank"
if errorlevel 1 if not defined result set "result=%errorlevel%"
echo === MIDI import upload limit test ===
dotnet test tests\Ample.Server.Tests\Ample.Server.Tests.csproj --no-build --filter FullyQualifiedName~ProjectRoutesTests.Midi_upload_accepts_generated_smf_and_rejects_bad_track_or_bytes
if errorlevel 1 if not defined result set "result=%errorlevel%"
echo === AMPLE docs check ===
dotnet run --project src\Ample.Cli\Ample.Cli.csproj -- docs check
if errorlevel 1 if not defined result set "result=%errorlevel%"
echo === Sketch parity check ===
node tools\parity-report.mjs --check
if errorlevel 1 if not defined result set "result=%errorlevel%"
echo === MIDI module navigation and editable-node browser tests ===
call npx playwright test spec-tests/parity/groups.spec.ts spec-tests/parity/midi-drop-import.spec.ts --project chromium
if errorlevel 1 if not defined result set "result=%errorlevel%"
goto afterPlaywright
:missingPlaywright
echo Playwright dependencies are missing; run npm ci from the AMPLE folder and rerun this batch for browser verification.
set "result=2"
goto failed
:afterPlaywright
echo === General MIDI bank coverage ===
dotnet run --project src\Ample.Cli\Ample.Cli.csproj -- midi soundfont-check --file pack:musescore-general
if errorlevel 1 if not defined result set "result=%errorlevel%"
echo === Owner MIDI conversion targets ===
set "CHANTILLY_ORCHESTRATED=%USERPROFILE%\Downloads\CHILL - Lofi Jazz\CHILL - Lofi Jazz\Chantilly\Chantilly - Orchestrated.mid"
set "CHANTILLY_REDUCED=%USERPROFILE%\Downloads\CHILL - Lofi Jazz\CHILL - Lofi Jazz\Chantilly\Chantilly - Reduced.mid"
if not exist "%CHANTILLY_ORCHESTRATED%" goto missingTarget
if not exist "%CHANTILLY_REDUCED%" goto missingTarget
dotnet run --project src\Ample.Cli\Ample.Cli.csproj -- midi verify-conversion "%CHANTILLY_ORCHESTRATED%"
if errorlevel 1 if not defined result set "result=%errorlevel%"
dotnet run --project src\Ample.Cli\Ample.Cli.csproj -- midi verify-conversion "%CHANTILLY_REDUCED%"
if errorlevel 1 if not defined result set "result=%errorlevel%"
set "QUEEN_MIDI=%USERPROFILE%\Downloads\Queen - Bohemian Rhapsody.mid"
if exist "%QUEEN_MIDI%" dotnet run --project src\Ample.Cli\Ample.Cli.csproj -- midi verify-conversion "%QUEEN_MIDI%"
if exist "%QUEEN_MIDI%" if errorlevel 1 if not defined result set "result=%errorlevel%"
if defined result goto failed
popd
echo All requested checks passed.
exit /b 0
:failed
if not defined result set "result=%errorlevel%"
popd
echo Check failed with exit code %result%.
exit /b %result%
:missingTarget
echo Required owner MIDI targets were not found under the expected Downloads folder.
set "result=2"
goto failed
:missingMetrics
echo GM SoundFont setup completed without its required metrics record.
set "result=1"
goto failed

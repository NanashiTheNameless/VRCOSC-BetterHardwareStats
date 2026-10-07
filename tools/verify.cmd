@echo off
rem Builds, tests and probes on Windows; every step writes a log to logs\ (gitignored).
rem Usage: tools\verify.cmd [all|build|test|probe]   (default all). Set NOPAUSE=1 to skip the final pause.
setlocal
cd /d "%~dp0.."
if not exist logs mkdir logs
set DOTNET_NOLOGO=1
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set STEP=%~1
if "%STEP%"=="" set STEP=all
set FAILED=
echo started %DATE% %TIME% > logs\verify-status.txt

if /i "%STEP%"=="all" goto build
if /i "%STEP%"=="build" goto build
if /i "%STEP%"=="test" goto test
if /i "%STEP%"=="probe" goto probe
echo unknown step %STEP% >> logs\verify-status.txt
exit /b 2

:build
echo [build] dotnet build (log: logs\build.log)
dotnet --info > logs\dotnet-info.log 2>&1
dotnet build BetterHardwareStats.sln -c Debug > logs\build.log 2>&1
set RC=%ERRORLEVEL%
echo build exit %RC% >> logs\verify-status.txt
if not "%RC%"=="0" (
  set FAILED=build
  echo BUILD FAILED. Errors:
  findstr /c:": error " logs\build.log
  goto done
)
if /i "%STEP%"=="build" goto done

:test
echo [test] core tests (log: logs\test-core.log)
dotnet test --project tests\BetterHardwareStats.Core.Tests > logs\test-core.log 2>&1
set RC=%ERRORLEVEL%
echo test-core exit %RC% >> logs\verify-status.txt
if not "%RC%"=="0" set FAILED=%FAILED% test-core
echo [test] windows hardware tests (log: logs\test-windows.log)
dotnet test --project tests\BetterHardwareStats.Windows.Tests > logs\test-windows.log 2>&1
set RC=%ERRORLEVEL%
echo test-windows exit %RC% >> logs\verify-status.txt
if not "%RC%"=="0" set FAILED=%FAILED% test-windows
rem Same tests through the xunit runner directly, to capture each test's output text.
if exist tests\BetterHardwareStats.Windows.Tests\bin\Debug\net10.0-windows\win-x64\BetterHardwareStats.Windows.Tests.exe (
  tests\BetterHardwareStats.Windows.Tests\bin\Debug\net10.0-windows\win-x64\BetterHardwareStats.Windows.Tests.exe -showliveoutput > logs\test-windows-output.log 2>&1
)
if /i "%STEP%"=="test" goto done

:probe
echo [probe] 20 s hardware probe (log: logs\probe.log)
dotnet run --project tools\HardwareProbe -c Debug --no-build -- --duration 20 --diagnostics --dump-lhm-sensors --dump-counters --fixtures logs\fixtures > logs\probe.log 2>&1
set RC=%ERRORLEVEL%
echo probe exit %RC% >> logs\verify-status.txt
if not "%RC%"=="0" set FAILED=%FAILED% probe

:done
echo finished %DATE% %TIME% >> logs\verify-status.txt
echo.
if defined FAILED (echo FAILED:%FAILED%  - see logs\verify-status.txt) else (echo ALL STEPS PASSED. Logs are in the logs folder.)
if not defined NOPAUSE pause

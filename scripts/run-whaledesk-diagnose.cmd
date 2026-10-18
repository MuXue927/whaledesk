@echo off
rem ---------------------------------------------------------------------------
rem Why can't the pet start DeepSeek Harness?
rem
rem Run this from FILE EXPLORER (double-click). The pet inherits the integrity
rem label of its own executable: while WhaleDesk.exe lives inside DSH's default
rem workspace it carries a "Low Mandatory Level" label, so Windows starts it at
rem Low integrity and it cannot create any normal child process (DeepSeek
rem Harness -> 0x80000003, Explorer -> 0xc0000142).
rem
rem Optional: pass seconds, e.g.  run-whaledesk-diagnose.cmd 20
rem ---------------------------------------------------------------------------
setlocal
set "ROOT=%~dp0.."
set "EXE=%ROOT%\src\WhaleDesk\bin\Release\net9.0-windows\WhaleDesk.exe"
set "PROBE=%ROOT%\build\spawn-probe.txt"
set "SECONDS=60"
if not "%~1"=="" set "SECONDS=%~1"

if not exist "%EXE%" (
  echo WhaleDesk.exe was not found at:
  echo   %EXE%
  echo Build it first:
  echo   dotnet build "%ROOT%\src\WhaleDesk\WhaleDesk.csproj" -c Release
  pause
  exit /b 1
)

echo.
echo Integrity label of the pet executable:
icacls "%EXE%" 2>nul | findstr /I "Mandatory"
echo   ^(Low Mandatory Level means she cannot start DeepSeek Harness^)

echo.
echo Running the diagnostic for %SECONDS% seconds...
"%EXE%" --live --seconds %SECONDS% --probe-spawn

echo.
echo ============ SPAWN PROBE RESULT ============
if exist "%PROBE%" powershell -NoProfile -Command "Get-Content -LiteralPath '%PROBE%' -Encoding UTF8"
echo ============================================
echo.
echo Full log: %ROOT%\build\whaledesk.log
echo.
echo Please send the block above (it starts with "when=" and "trust=").
pause
endlocal
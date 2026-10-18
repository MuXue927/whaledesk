@echo off
rem ===========================================================================
rem  Packages the current dist\ into a release zip with notes and checksum.
rem  Run AFTER build-dist.cmd (this script does not rebuild).
rem
rem  Output: whaledesk\release\WhaleDesk-<x.y.z>.zip
rem          whaledesk\release\RELEASE_NOTES-<x.y.z>.md
rem          whaledesk\release\SHA256.txt
rem  Pure ASCII on purpose (cmd decodes UTF-8 as GBK - see the installer docs).
rem ===========================================================================
setlocal enabledelayedexpansion
set "ROOT=%~dp0.."
set "DIST=%ROOT%\dist"
set "OUT=%ROOT%\release"

if not exist "%DIST%\app\WhaleDesk.exe" (
  echo dist is missing or incomplete.
  echo Run whaledesk\scripts\build-dist.cmd first.
  pause
  exit /b 1
)

rem FileVersion is 4 parts (0.16.0.0); use the first three for names.
set "FULLVER=0.0.0.0"
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Item '%DIST%\app\WhaleDesk.exe').VersionInfo.FileVersion"`) do set "FULLVER=%%v"
for /f "tokens=1,2,3 delims=." %%a in ("%FULLVER%") do set "VER=%%a.%%b.%%c"
if "%VER%"=="" set "VER=%FULLVER%"

if not exist "%OUT%" mkdir "%OUT%" 2>nul
set "ZIP=%OUT%\WhaleDesk-%VER%.zip"
if exist "%ZIP%" del /q "%ZIP%" >nul 2>&1

echo Packing %VER% ...
powershell -NoProfile -Command "$ErrorActionPreference='Stop'; Compress-Archive -Path '%DIST%\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
  echo Packing failed.
  pause
  exit /b 1
)

echo Writing release notes ...
set "NOTES=%~dp0make-release-notes.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File "%NOTES%" -Version "%VER%" -ZipPath "%ZIP%" -OutDir "%OUT%"
if errorlevel 1 echo   notes step reported a problem.

echo Computing checksum ...
set "SUM=%~dp0make-checksum.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File "%SUM%" -ZipPath "%ZIP%" -OutDir "%OUT%"
if errorlevel 1 echo   checksum step reported a problem.

echo.
echo Done. Files in %OUT%:
dir /b "%OUT%"
echo.
echo To publish on GitHub (after committing + pushing a tag):
echo   gh release create v%VER% "%ZIP%" --title "WhaleDesk %VER%" --notes-file "%OUT%\RELEASE_NOTES-%VER%.md"
pause
endlocal

@echo off
rem ===========================================================================
rem  Builds a SELF-CONTAINED (no .NET runtime needed on the target machine) dist.
rem
rem  Output: whaledesk\dist-selfcontained\app\  (WhaleDesk.exe + runtime + assets)
rem          whaledesk\dist-selfcontained\install.cmd / make-shortcut.ps1 / VERSION.txt
rem
rem  This needs nuget.org access the first time: the win-x64 runtime packs
rem  (Microsoft.NETCore.App.Runtime.win-x64, Microsoft.WindowsDesktop.App.Runtime.win-x64)
rem  are downloaded during restore. If the machine is offline, the publish step fails -
rem  that is expected and is reported clearly below.
rem
rem  Pure ASCII on purpose (cmd decodes UTF-8 as GBK).
rem ===========================================================================
setlocal
set "ROOT=%~dp0.."
set "OUT=%ROOT%\dist-selfcontained"
set "APP=%OUT%\app"

rem Keep NuGet away from %APPDATA%: a locked-down user profile makes restore fail before it
rem even reaches the network. A local home dir is enough for restore.
if not exist "%ROOT%\build\nuget-home" mkdir "%ROOT%\build\nuget-home" 2>nul
set "APPDATA=%ROOT%\build\nuget-home"
set "NUGET_HOME=%ROOT%\build\nuget-home"

where dotnet >nul 2>&1
if errorlevel 1 (
  echo dotnet was not found on PATH.
  pause
  exit /b 1
)

echo Cleaning %OUT% ...
if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%APP%" 2>nul

echo Publishing self-contained win-x64 ^(single file + assets^) ...
dotnet publish "%ROOT%\src\WhaleDesk\WhaleDesk.csproj" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none ^
  -o "%APP%" --nologo
if errorlevel 1 (
  echo.
  echo ============================================================
  echo  PUBLISH FAILED.
  echo  The usual cause is missing win-x64 runtime packs because this
  echo  machine cannot reach nuget.org. Run this script on a machine
  echo  with internet access, then copy dist-selfcontained\ over.
  echo ============================================================
  pause
  exit /b 1
)

echo Copying assets and installer helpers ...
if exist "%ROOT%\assets" xcopy "%ROOT%\assets" "%APP%\assets\" /E /I /Y /Q >nul
copy /Y "%~dp0install.cmd" "%OUT%\install.cmd" >nul 2>&1
copy /Y "%~dp0make-shortcut.ps1" "%OUT%\make-shortcut.ps1" >nul 2>&1

set "EXEVER=unknown"
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Item '%APP%\WhaleDesk.exe').VersionInfo.FileVersion"`) do set "EXEVER=%%v"
powershell -NoProfile -Command "$v='%EXEVER%'; $t=(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'); Set-Content -LiteralPath '%OUT%\VERSION.txt' -Encoding ascii -Value ('WhaleDesk '+$v); Add-Content -LiteralPath '%OUT%\VERSION.txt' -Encoding ascii -Value ('built '+$t); Add-Content -LiteralPath '%OUT%\VERSION.txt' -Encoding ascii -Value ('mode self-contained win-x64 (no .NET runtime needed)')"

echo.
echo Done. Self-contained build in %OUT%
echo   app\     : exe + runtime + assets (runs on machines without .NET)
echo   install.cmd : installer helpers
echo Note: the installer expects the app folder next to it (it copies %%~dp0app\*).
pause
endlocal

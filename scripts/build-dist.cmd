@echo off
rem ---------------------------------------------------------------------------
rem Builds a self-contained distribution folder (dist\app) and stages the installer.
rem
rem Usage (from any folder):  whaledesk\scripts\build-dist.cmd
rem
rem After it finishes, double-click dist\install.cmd to install/update the pet.
rem ---------------------------------------------------------------------------
setlocal
set "ROOT=%~dp0.."
set "OUT=%ROOT%\dist\app"
set "VERSIONFILE=%ROOT%\dist\VERSION.txt"

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
mkdir "%OUT%" 2>nul

echo Publishing (framework-dependent single file; needs the .NET 9 desktop runtime) ...
rem Framework-dependent publish (no RID): works offline from the local package cache.
rem A RID / single-file publish needs the win-x64 runtime packs from nuget.org.
dotnet restore "%ROOT%\src\WhaleDesk\WhaleDesk.csproj" -p:NuGetAudit=false --nologo >nul
if errorlevel 1 (
  echo Restore failed.
  pause
  exit /b 1
)

dotnet publish "%ROOT%\src\WhaleDesk\WhaleDesk.csproj" -c Release -p:DebugType=none -p:GenerateDocumentationFile=false -p:NuGetAudit=false -o "%OUT%" --nologo --no-restore
if errorlevel 1 (
  echo Publish failed.
  pause
  exit /b 1
)

echo Copying assets and icons ...
if exist "%ROOT%\assets" xcopy "%ROOT%\assets" "%OUT%\assets\" /E /I /Y /Q >nul
copy /Y "%ROOT%\assets\whaledesk-clear.ico" "%OUT%\whaledesk-clear.ico" >nul 2>&1
copy /Y "%ROOT%\assets\whaledesk.ico" "%OUT%\whaledesk.ico" >nul 2>&1

echo Staging the installer ...
copy /Y "%~dp0install.cmd" "%ROOT%\dist\install.cmd" >nul 2>&1
copy /Y "%~dp0make-shortcut.ps1" "%ROOT%\dist\make-shortcut.ps1" >nul 2>&1

set "EXEVER=unknown"
rem Version + build stamp: PURE ASCII on purpose. A GBK console turns UTF-8 bytes into
rem mojibake, so the file must not contain the localized weekday (%DATE%) nor a Chinese path.
set "EXEVER=unknown"
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Item '%OUT%\WhaleDesk.exe').VersionInfo.FileVersion"`) do set "EXEVER=%%v"
if "%EXEVER%"=="" set "EXEVER=unknown"
powershell -NoProfile -Command "$v='%EXEVER%'; $t=(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'); Set-Content -LiteralPath '%VERSIONFILE%' -Encoding ascii -Value ('WhaleDesk '+$v); Add-Content -LiteralPath '%VERSIONFILE%' -Encoding ascii -Value ('built '+$t)"
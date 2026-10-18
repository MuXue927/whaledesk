@echo off
rem Launch the whale-girl desktop companion and keep her on screen.
rem
rem Recovery paths if she is ever hidden:
rem   * left-click the tray icon
rem   * press Ctrl+Alt+W
rem   * run this script again (it asks the running instance to show itself)
setlocal
set EXE=%~dp0..\src\WhaleDesk\bin\Release\net9.0-windows\WhaleDesk.exe
if not exist "%EXE%" (
  echo WhaleDesk.exe was not found. Build it first:
  echo   dotnet build "%~dp0..\src\WhaleDesk\WhaleDesk.csproj" -c Release
  pause
  exit /b 1
)

tasklist /FI "IMAGENAME eq WhaleDesk.exe" 2>nul | find /I "WhaleDesk.exe" >nul
if not errorlevel 1 (
  echo WhaleDesk is already running - asking it to show the pet...
) else (
  echo Starting WhaleDesk...
)
start "" "%EXE%" --live --seconds 0 %*
endlocal

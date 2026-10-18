@echo off
rem ===========================================================================
rem  WhaleDesk installer: install or update the desktop pet.
rem
rem  SOURCE IS PURE ASCII ON PURPOSE. cmd.exe decodes batch files with the OEM
rem  code page (GBK on this machine), so any Chinese literal here becomes mojibake
rem  and can break quoting of the PowerShell commands we call. The shortcut name
rem  is built from code points inside make-shortcut.ps1 instead.
rem
rem  What it does:
rem    1. asks the running pet to quit (WhaleDesk.exe --quit), waits, force-stops
rem    2. keeps the previous exe as WhaleDesk.exe.old (one-file rollback)
rem    3. copies dist\app\* into %LOCALAPPDATA%\WhaleDesk
rem    4. clears any inherited low-integrity label on the copy
rem    5. recreates the desktop shortcut through make-shortcut.ps1
rem    6. starts the new version and prints the installed version
rem ===========================================================================
setlocal
set "SRC=%~dp0app"
if not exist "%SRC%\WhaleDesk.exe" set "SRC=%~dp0..\dist\app"
if not exist "%SRC%\WhaleDesk.exe" (
  echo Could not find the built app.
  echo Run whaledesk\scripts\build-dist.cmd first.
  pause
  exit /b 1
)

set "DEST=%LOCALAPPDATA%\WhaleDesk"
set "EXE=%DEST%\WhaleDesk.exe"

echo ============================================================
echo  WhaleDesk installer
echo  from: %SRC%
echo  to  : %DEST%
echo ============================================================
echo.

rem ---- 1. stop the running pet --------------------------------------------
tasklist /FI "IMAGENAME eq WhaleDesk.exe" 2>nul | find /I "WhaleDesk.exe" >nul
if not errorlevel 1 (
  echo Stopping the running pet ...
  if exist "%EXE%" "%EXE%" --quit >nul 2>&1
  for /l %%i in (1,1,10) do (
    tasklist /FI "IMAGENAME eq WhaleDesk.exe" 2>nul | find /I "WhaleDesk.exe" >nul
    if errorlevel 1 goto stopped
    ping -n 2 127.0.0.1 >nul
  )
  echo   no response, forcing ...
  taskkill /IM WhaleDesk.exe /F >nul 2>&1
  ping -n 2 127.0.0.1 >nul
)
:stopped

rem ---- 2. keep the previous exe for rollback -------------------------------
if exist "%EXE%" (
  echo Keeping a rollback copy ^(WhaleDesk.exe.old^) ...
  if exist "%DEST%\WhaleDesk.exe.old" del /q "%DEST%\WhaleDesk.exe.old" >nul 2>&1
  ren "%EXE%" "WhaleDesk.exe.old" >nul 2>&1
)

rem ---- 3. copy the new build ----------------------------------------------
echo Copying files ...
if not exist "%DEST%" mkdir "%DEST%" 2>nul
xcopy "%SRC%" "%DEST%\" /E /I /Y /Q >nul
if errorlevel 1 (
  echo Copy failed. If the pet is still running, close it and try again.
  pause
  exit /b 1
)
if exist "%~dp0VERSION.txt" copy /Y "%~dp0VERSION.txt" "%DEST%\VERSION.txt" >nul 2>&1
if exist "%~dp0..\dist\VERSION.txt" copy /Y "%~dp0..\dist\VERSION.txt" "%DEST%\VERSION.txt" >nul 2>&1
if not exist "%EXE%" (
  echo Copy finished but WhaleDesk.exe is missing. Aborting.
  pause
  exit /b 1
)

rem ---- 4. clear the inherited low-integrity label ---------------------------
icacls "%DEST%" /setintegritylevel (OI)(CI)Medium >nul 2>&1
icacls "%EXE%" /setintegritylevel Medium >nul 2>&1

rem ---- 5. desktop shortcut --------------------------------------------------
echo Recreating the desktop shortcut ...
set "PS1=%~dp0make-shortcut.ps1"
if not exist "%PS1%" set "PS1=%~dp0..\scripts\make-shortcut.ps1"
if exist "%PS1%" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" -Target "%EXE%" -IconPath "%DEST%\whaledesk-clear.ico"
  if errorlevel 1 echo   shortcut step reported a problem ^(see line above^).
) else (
  echo   make-shortcut.ps1 not found; skipping the shortcut.
)

rem ---- 6. version + start --------------------------------------------------
set "EXEVER=unknown"
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Item '%EXE%').VersionInfo.FileVersion"`) do set "EXEVER=%%v"
echo.
echo Installed version: %EXEVER%
if exist "%DEST%\VERSION.txt" type "%DEST%\VERSION.txt"
echo.
echo Starting the new version ...
start "" "%EXE%" --live --seconds 0

echo.
echo ============================================================
echo  Done. Start her from the desktop shortcut from now on.
echo  Rollback: rename WhaleDesk.exe.old back to WhaleDesk.exe
echo  Log:      %DEST%\build\whaledesk.log
echo ============================================================
pause
endlocal

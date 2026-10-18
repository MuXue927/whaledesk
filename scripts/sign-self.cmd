@echo off
rem Creates a self-signed code-signing certificate (current user) and signs WhaleDesk.exe.
rem This ONLY silences prompts on THIS machine; other machines still warn.
setlocal
if not exist "%~dp0..\dist\app\WhaleDesk.exe" ( echo Run build-dist.cmd first & pause & exit /b 1 )
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$c = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -like 'CN=WhaleDesk Dev*' } | Select-Object -First 1;" ^
  "if (-not $c) { $c = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=WhaleDesk Dev' -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5) };" ^
  "Set-AuthenticodeSignature -FilePath '%~dp0..\dist\app\WhaleDesk.exe' -Certificate $c;" ^
  "Write-Host 'signed for local testing only'"
pause
endlocal
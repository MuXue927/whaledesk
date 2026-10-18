# Generates the release notes markdown for a WhaleDesk build.
# Pure ASCII source: it is called from a .cmd and the content is written as ASCII so that
# cmd/PowerShell code pages can never mangle it.
param(
    [Parameter(Mandatory = $true)][string]$Version,   # e.g. 0.16.0
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = 'Stop'
try {
    $zip = Get-Item -LiteralPath $ZipPath
    $size = [math]::Round($zip.Length / 1MB, 2)
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'

    $lines = @(
        "# WhaleDesk $Version",
        "",
        "- built: $stamp",
        "- package: $($zip.Name) ($size MB)",
        "",
        "## How to install",
        "",
        "1. Unzip anywhere.",
        "2. Double-click install.cmd.",
        "   It stops the old version, keeps WhaleDesk.exe.old for rollback, copies to",
        "   %LOCALAPPDATA%\WhaleDesk, recreates the desktop shortcut and starts it.",
        "3. Requires the .NET 9 Desktop Runtime (framework-dependent build).",
        "",
        "## What is inside",
        "",
        "- app\ : WhaleDesk.exe + dlls + assets + icons",
        "- install.cmd / make-shortcut.ps1 : installer helpers",
        "- VERSION.txt : build stamp",
        "",
        "## Known limits",
        "",
        "- The executable is unsigned; SmartScreen may warn on first run.",
        "- Sprite art credit belongs to its original author (@linxin666); do not redistribute the art alone.",
        "- Needs the DSH pet + usage plugins for the DSH-integrated features (degrades gracefully).",
        "",
        "## Logs and state",
        "",
        "- %LOCALAPPDATA%\WhaleDesk\build\whaledesk.log",
        "- %LOCALAPPDATA%\WhaleDesk\state\*.json"
    )

    $out = Join-Path $OutDir ("RELEASE_NOTES-$Version.md")
    Set-Content -LiteralPath $out -Value $lines -Encoding ascii
    Write-Host ("notes -> " + $out)
    exit 0
}
catch {
    Write-Host ("notes FAILED: " + $_.Exception.Message)
    exit 1
}

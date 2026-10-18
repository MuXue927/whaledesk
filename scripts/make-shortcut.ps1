# Makes (or repairs) the desktop shortcut for the pet.
# Source is pure ASCII on purpose: install.cmd is a cmd batch file, and cmd decodes
# UTF-8 bytes using the OEM code page (GBK), which previously turned a Chinese
# literal into mojibake that broke the quoting of the whole -Command string.
# The Chinese name is therefore built from code points:
#   0x5C0F = xiao, 0x9CB8 = jing, 0x5A18 = niang  -> "xiao jing niang"
param(
    [Parameter(Mandatory = $true)][string]$Target,
    [string]$IconPath = ""
)

$ErrorActionPreference = 'Stop'
try {
    if (-not (Test-Path -LiteralPath $Target)) {
        Write-Host "  shortcut skipped: target not found: $Target"
        exit 1
    }

    $name = [string]([char]0x5C0F) + [string]([char]0x9CB8) + [string]([char]0x5A18)
    $desk = [Environment]::GetFolderPath('Desktop')
    $shell = New-Object -ComObject WScript.Shell

    $lnk = Join-Path $desk ($name + '.lnk')
    try {
        $s = $shell.CreateShortcut($lnk)
    }
    catch {
        # A non-UTF8 shortcut name can fail on exotic setups; fall back to ASCII.
        $lnk = Join-Path $desk 'WhaleDesk.lnk'
        $s = $shell.CreateShortcut($lnk)
    }

    $s.TargetPath = $Target
    $s.Arguments = '--live --seconds 0'
    $s.WorkingDirectory = Split-Path -Parent $Target
    if ($IconPath -and (Test-Path -LiteralPath $IconPath)) { $s.IconLocation = $IconPath + ',0' }
    $s.Description = 'Whale girl desktop pet'
    try {
        $s.Save()
    }
    catch {
        # If the Unicode shortcut name cannot be saved, retry with an ASCII name so the user
        # still gets a working shortcut instead of nothing.
        Write-Host ('  unicode shortcut failed (' + $_.Exception.Message + '), retrying with ASCII name')
        $lnk = Join-Path $desk 'WhaleDesk.lnk'
        $s2 = $shell.CreateShortcut($lnk)
        $s2.TargetPath = $Target
        $s2.Arguments = '--live --seconds 0'
        $s2.WorkingDirectory = Split-Path -Parent $Target
        if ($IconPath -and (Test-Path -LiteralPath $IconPath)) { $s2.IconLocation = $IconPath + ',0' }
        $s2.Description = 'Whale girl desktop pet'
        $s2.Save()
    }

    if (Test-Path -LiteralPath $lnk) {
        Write-Host ('  shortcut OK: ' + $lnk)
        exit 0
    }
    Write-Host ('  shortcut FAILED: ' + $lnk)
    exit 1
}
catch {
    Write-Host ('  shortcut error: ' + $_.Exception.Message)
    exit 1
}

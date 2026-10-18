# Writes SHA256.txt next to a release zip.
# Separate -File script on purpose: inline PowerShell in a .cmd mangles non-ASCII paths.
# Two implementations: the cmdlet, then a pure .NET fallback (some locked-down environments
# do not autoload Microsoft.PowerShell.Utility).
param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = 'Stop'
$name = [System.IO.Path]::GetFileName($ZipPath)
$hash = $null

try { $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ZipPath).Hash.ToLower() } catch { $hash = $null }

if (-not $hash) {
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $stream = [System.IO.File]::OpenRead($ZipPath)
        try { $bytes = $sha.ComputeHash($stream) } finally { $stream.Dispose(); $sha.Dispose() }
        $sb = New-Object System.Text.StringBuilder
        foreach ($b in $bytes) { [void]$sb.Append($b.ToString('x2')) }
        $hash = $sb.ToString()
    }
    catch { $hash = $null }
}

if (-not $hash) {
    Set-Content -LiteralPath (Join-Path $OutDir 'SHA256.txt') -Value ('unavailable  ' + $name) -Encoding ascii
    Write-Host ('sha256 UNAVAILABLE for ' + $name)
    exit 1
}

$line = $hash + '  ' + $name
Set-Content -LiteralPath (Join-Path $OutDir 'SHA256.txt') -Value $line -Encoding ascii
Write-Host ('sha256 -> ' + $line)
exit 0
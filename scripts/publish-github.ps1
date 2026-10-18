<#
.SYNOPSIS
  Publish WhaleDesk to GitHub: create/refresh the repo, push, and attach the release zip.

.DESCRIPTION
  Prompts for a token (never echoed, never written to disk) and uses it only in this process.
  Create a fine-grained token with "Contents: read and write" at https://github.com/settings/tokens

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish-github.ps1 -Repo MuXue927/whaledesk
#>
param(
  [string]$Repo = "MuXue927/whaledesk",
  [string]$Version,
  [switch]$SkipRelease
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
  $versionFile = Join-Path $root "dist\VERSION.txt"
  if (Test-Path $versionFile) { $m = [regex]::Match((Get-Content $versionFile)[0], '(\d+\.\d+\.\d+)'); if ($m.Success) { $Version = $m.Groups[1].Value } }
}
if (-not $Version) { throw "cannot determine version; pass -Version x.y.z" }
Write-Host "Publishing WhaleDesk $Version to $Repo"

$secure = Read-Host "Paste a GitHub token (Contents: read/write). Input is hidden" -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try { $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
if ([string]::IsNullOrWhiteSpace($token)) { throw "no token" }

$headers = @{ Authorization = "Bearer $token"; Accept = "application/vnd.github+json"; "User-Agent" = "WhaleDesk-Publish" }
$owner, $name = $Repo.Split('/')

# 1. repo (create if missing)
try { Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo" -Headers $headers -Method Get | Out-Null; Write-Host "repo exists" }
catch {
  Write-Host "creating repo $Repo ..."
  Invoke-RestMethod -Uri "https://api.github.com/user/repos" -Headers $headers -Method Post -Body (@{ name = $name; private = $false; description = "WhaleDesk 小鲸娘桌面伴侣" } | ConvertTo-Json) | Out-Null
}

# 2. git init + commit + push
if (-not (Test-Path (Join-Path $root ".git"))) { git init | Out-Null }
git add -A
git -c user.name="WhaleDesk" -c user.email="whaledesk@users.noreply.github.com" commit -m "WhaleDesk $Version" 2>$null | Out-Null
git branch -M main
git remote remove origin 2>$null | Out-Null
git remote add origin "https://github.com/$Repo.git"
$env:GIT_TERMINAL_PROMPT = "0"
git -c credential.helper= push --force-with-lease "https://$owner`:$token@github.com/$Repo.git" main

if ($SkipRelease) { Write-Host "repo pushed, release skipped"; exit 0 }

# 3. release + asset
$zip = Join-Path $root "release\WhaleDesk-$Version.zip"
$notes = Join-Path $root "release\RELEASE_NOTES-$Version.md"
if (-not (Test-Path $zip)) { throw "missing $zip (run scripts\package-release.cmd first)" }
$body = if (Test-Path $notes) { Get-Content $notes -Raw } else { "WhaleDesk $Version" }
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases" -Headers $headers -Method Post -Body (@{
  tag_name = "v$Version"; name = "WhaleDesk $Version"; body = $body; draft = $false; prerelease = $false
} | ConvertTo-Json)
$uploadUrl = $release.upload_url -replace '\{.*\}',''
$bytes = [IO.File]::ReadAllBytes($zip)
Invoke-RestMethod -Uri "$uploadUrl?name=WhaleDesk-$Version.zip" -Headers (@{ Authorization = "Bearer $token"; "User-Agent" = "WhaleDesk-Publish"; "Content-Type" = "application/zip" }) -Method Post -Body $bytes | Out-Null
Write-Host "published: https://github.com/$Repo/releases/tag/v$Version"
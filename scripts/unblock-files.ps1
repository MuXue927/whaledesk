# Removes the "blocked" mark (Mark of the Web) from every file in this folder.
# Run this after extracting a downloaded zip: right-click does it per file, this does all at once.
param([string]$Path = (Split-Path -Parent $PSScriptRoot))
Get-ChildItem -Path $Path -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
Write-Host "Unblocked files under $Path"
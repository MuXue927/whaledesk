# Capture neutral-background documentation screenshots.
#
# Strategy:
#   1. start a full-screen TOPMOST neutral backdrop (scripts\neutral-backdrop.ps1) so the wallpaper,
#      desktop icons and other apps are hidden without touching the user's wallpaper
#   2. for every UI state: capture a reference frame (pet closed), launch the matching --probe-*,
#      wait, capture, then diff the two frames and crop to the changed UI region
#   3. stop the pet and the backdrop; crops land in ..\docs\images\
#
# Run from a normal (non-sandboxed) PowerShell on the desktop session:
#   powershell -ExecutionPolicy Bypass -File scripts\capture-doc-shots.ps1
param(
  [string]$Root = (Split-Path -Parent $PSScriptRoot),
  [string]$Exe = "",
  [string]$OutDir = "",
  [switch]$KeepRaw
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
if (-not $Exe)    { $Exe = Join-Path $Root "src\WhaleDesk\bin\Release\net9.0-windows\WhaleDesk.exe" }
if (-not $OutDir) { $OutDir = Join-Path $Root "docs\images" }
$work = Join-Path $Root "build\shots"
New-Item -ItemType Directory -Path $work -Force | Out-Null
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
if (-not (Test-Path $Exe)) { throw "exe not found: $Exe (run build-dist.cmd first)" }

function Cap([string]$file) {
  $b = New-Object System.Drawing.Bitmap 1920,1080
  $gr = [System.Drawing.Graphics]::FromImage($b)
  $gr.CopyFromScreen(0,0,0,0,(New-Object System.Drawing.Size 1920,1080))
  $gr.Dispose(); $b.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
}
function Stop-Pet {
  Get-Process WhaleDesk -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 800
}
function Start-Pet([string[]]$extra) {
  Stop-Pet
  $all = @('--live','--seconds','120','--reset-position','--allow-multiple','--no-tray','--no-onboard') + $extra
  & cmd.exe /c ('start "" "' + $Exe + '" ' + ($all -join ' '))
}
function Stop-Backdrop {
  foreach ($p in (Get-Process powershell -ErrorAction SilentlyContinue)) {
    try { if ($p.MainWindowTitle -eq '' -and $p.Id -ne $PID) { } } catch { }
  }
}
$log = Join-Path $Root "build\whaledesk.log"

# ---------- diff + crop ----------
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class ShotCrop {
  public static Rectangle DiffBox(string aPath, string bPath, int step) {
    using (var a = new Bitmap(aPath)) using (var b = new Bitmap(bPath)) {
      int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
      var ad = a.LockBits(new Rectangle(0,0,w,h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
      var bd = b.LockBits(new Rectangle(0,0,w,h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
      int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
      try {
        int stride = ad.Stride;
        var ab = new byte[stride * h]; var bb = new byte[stride * h];
        Marshal.Copy(ad.Scan0, ab, 0, ab.Length);
        Marshal.Copy(bd.Scan0, bb, 0, bb.Length);
        int limitY = Math.Min(h, 1079);
        for (int y = 0; y < limitY; y += step) {
          int row = y * stride;
          for (int x = 0; x < w; x += step) {
            int i = row + x * 4;
            int d = Math.Abs(ab[i] - bb[i]) + Math.Abs(ab[i+1] - bb[i+1]) + Math.Abs(ab[i+2] - bb[i+2]);
            if (d > 20) {
              if (x < minX) minX = x; if (x > maxX) maxX = x;
              if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
          }
        }
      } finally { a.UnlockBits(ad); b.UnlockBits(bd); }
      if (maxX < 0) return Rectangle.Empty;
      return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }
  }
  public static void Crop(string src, string dst, Rectangle box, int pad) {
    using (var img = Image.FromFile(src)) {
      var r = Rectangle.Intersect(Rectangle.FromLTRB(box.Left - pad, box.Top - pad, box.Right + pad, box.Bottom + pad),
                                  new Rectangle(0, 0, img.Width, img.Height));
      if (r.Width < 40 || r.Height < 40) r = new Rectangle(0, 0, img.Width, img.Height);
      using (var outBmp = new Bitmap(r.Width, r.Height))
      using (var g = Graphics.FromImage(outBmp)) {
        g.DrawImage(img, new Rectangle(0,0,r.Width,r.Height), r, GraphicsUnit.Pixel);
        outBmp.Save(dst, ImageFormat.Png);
      }
    }
  }
}
'@

$backdrop = Start-Process powershell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'neutral-backdrop.ps1')) -PassThru
Start-Sleep -Seconds 2

function Shot([string]$name, [string[]]$extra, [int]$waitMs) {
  Stop-Pet
  $ref = Join-Path $work "_ref.png"
  Cap $ref
  Start-Pet $extra
  Start-Sleep -Milliseconds $waitMs
  $raw = Join-Path $work ("raw-" + $name)
  Cap $raw
  $box = [ShotCrop]::DiffBox($ref, $raw, 2)
  $dest = Join-Path $OutDir $name
  if ($box.Width -gt 0) { [ShotCrop]::Crop($raw, $dest, $box, 16) }
  else { [ShotCrop]::Crop($raw, $dest, (New-Object System.Drawing.Rectangle 0,0,1920,1080), 0) }
  Write-Host ("  {0}: box={1}" -f $name, $box)
}

try {
  Shot "01-pet.png" @('--bubble','今天也一起加油呀') 4200
  # menu + submenu (same run)
  Stop-Pet
  $ref = Join-Path $work "_ref.png"; Cap $ref
  Start-Pet @('--probe-menu'); Start-Sleep -Milliseconds 4300
  $raw = Join-Path $work "raw-menu.png"; Cap $raw
  $box = [ShotCrop]::DiffBox($ref, $raw, 2)
  if ($box.Width -gt 0) { [ShotCrop]::Crop($raw, (Join-Path $OutDir "02-menu.png"), $box, 16) }
  Write-Host ("  02-menu.png: box={0}" -f $box)
  $menuLine = Select-String -Path $log -Pattern 'menu window placed: menu phys (\d+),(\d+) (\d+)x(\d+)' -ErrorAction SilentlyContinue | Select-Object -Last 1
  if ($menuLine) {
    $mx=[int]$menuLine.Matches[0].Groups[1].Value; $my=[int]$menuLine.Matches[0].Groups[2].Value; $mh=[int]$menuLine.Matches[0].Groups[4].Value
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class Cur { [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); }'
    [Cur]::SetCursorPos($mx + 110, $my + [int]($mh * 0.54)) | Out-Null
    Start-Sleep -Milliseconds 1500
    $raw2 = Join-Path $work "raw-submenu.png"; Cap $raw2
    $box2 = [ShotCrop]::DiffBox($ref, $raw2, 2)
    if ($box2.Width -gt 0) { [ShotCrop]::Crop($raw2, (Join-Path $OutDir "03-submenu.png"), $box2, 16) }
    Write-Host ("  03-submenu.png: box={0}" -f $box2)
  }
  Shot "04-monitor.png" @('--probe-monitor-panel') 7000
  Shot "05-cleanup.png" @('--probe-cleanup-panel') 17000
  Shot "06-analysis.png" @('--probe-analyze') 30000
  Shot "07-clipboard.png" @('--probe-clipboard') 4500
  Shot "08-launcher.png" @('--probe-launcher') 3500
  Shot "09-plugins.png" @('--probe-plugins') 4000
  Shot "10-onboarding.png" @('--onboard') 4500
  Shot "11-dsh-guide.png" @('--probe-dsh-guide') 4500
  Shot "12-focus.png" @('--probe-focus') 6500
}
finally {
  Stop-Pet
  if ($backdrop -and -not $backdrop.HasExited) { Stop-Process -Id $backdrop.Id -Force -ErrorAction SilentlyContinue }
}
if (-not $KeepRaw) { Get-ChildItem $work -Filter "raw-*.png" | Remove-Item -Force -ErrorAction SilentlyContinue }
Write-Host "done -> $OutDir"

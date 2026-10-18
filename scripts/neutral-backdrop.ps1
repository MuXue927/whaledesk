# A full-screen neutral backdrop for documentation screenshots (no wallpaper changes).
#
# The pet window and its menus are always TOPMOST, so a TOPMOST full-screen form drawn first
# sits below them and above everything else (desktop icons, other apps, the taskbar). That makes
# captures reproducible on any machine without touching the user's wallpaper.
#
#   powershell -ExecutionPolicy Bypass -File scripts\neutral-backdrop.ps1
#   ... capture ...
#   Stop-Process -Name powershell ... (or kill the returned process)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$form.TopMost = $true
$form.ShowInTaskbar = $false
$form.BackColor = [System.Drawing.Color]::FromArgb(16, 21, 34)
$form.Add_Paint({
    param($sender, $e)
    $g = $e.Graphics
    $rect = New-Object System.Drawing.Rectangle 0, 0, $sender.Width, $sender.Height
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, `
        ([System.Drawing.Color]::FromArgb(12, 16, 28)), ([System.Drawing.Color]::FromArgb(42, 54, 82)), 40.0
    $g.FillRectangle($brush, $rect)
    $brush.Dispose()
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddEllipse(-260, -260, ($sender.Width + 520), ($sender.Height + 520))
    $pg = New-Object System.Drawing.Drawing2D.PathGradientBrush $path
    $pg.CenterColor = [System.Drawing.Color]::FromArgb(0, 0, 0, 0)
    $pg.SurroundColors = @([System.Drawing.Color]::FromArgb(110, 0, 0, 0))
    $g.FillPath($pg, $path)
    $pg.Dispose()
    $path.Dispose()
})
$form.Show()
[System.Windows.Forms.Application]::Run($form)

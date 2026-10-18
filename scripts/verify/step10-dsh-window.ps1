# End-to-end test of the reported bug: DSH hidden to the tray must be re-openable.
$ErrorActionPreference = 'Continue'
$w = "C:\Users\whxnt\OneDrive\文档\deepseek-harness\default-workspace"
$log = "$w\whaledesk\build\whaledesk.log"
$exe = "$w\whaledesk\src\WhaleDesk\bin\Release\net9.0-windows\WhaleDesk.exe"

$code = @'
using System;using System.Text;using System.Runtime.InteropServices;
public static class H {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  public static IntPtr Main() {
    IntPtr best = IntPtr.Zero;
    EnumWindows((h,l) => {
      var t = new StringBuilder(400); GetWindowTextW(h,t,400);
      var c = new StringBuilder(200); GetClassNameW(h,c,200);
      if (t.ToString().Contains("DeepSeek Harness") && c.ToString() == "Chrome_WidgetWin_1") { best = h; return false; }
      return true;
    }, IntPtr.Zero);
    return best;
  }
}
'@
Add-Type -TypeDefinition $code -ErrorAction SilentlyContinue
$h = [H]::Main()
Write-Host ("step 1  DSH main window hwnd=0x{0:X}  visible={1}" -f $h.ToInt64(), [H]::IsWindowVisible($h))

[H]::ShowWindow($h, 0) | Out-Null     # SW_HIDE: this is what "closed to tray" looks like
Start-Sleep -Milliseconds 800
Write-Host ("step 2  after hiding: visible={0}   <-- the reported broken state" -f [H]::IsWindowVisible($h))

Write-Host "step 3  now the pet runs its 'open / start DSH' action:"
Remove-Item $log -ErrorAction SilentlyContinue
Start-Process -FilePath $exe -ArgumentList '--live','--seconds','6','--probe-dsh' -Wait -NoNewWindow | Out-Null
Get-Content $log -Encoding UTF8 | Select-String 'DSH PROBE' | ForEach-Object { "        " + $_.Line }
Start-Sleep -Milliseconds 600
Write-Host ("step 4  after the action: visible={0}" -f [H]::IsWindowVisible($h))

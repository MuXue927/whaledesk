# 实验：同一个受限父进程下，直接启动 DSH vs 经 Explorer 中转启动
# 目的：区分「代码逻辑错」与「继承了受限环境（STATUS_BREAKPOINT）」
$ErrorActionPreference = 'Continue'
$exe = "D:\Software\DeepSeek Harness\DeepSeek Harness.exe"

$code = @'
using System;using System.Text;using System.Runtime.InteropServices;
public static class W3 {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  public static string Describe() {
    var h = GetForegroundWindow();
    var t = new StringBuilder(300); GetWindowTextW(h, t, 300);
    var c = new StringBuilder(120); GetClassNameW(h, c, 120);
    return h.ToInt64().ToString("X") + " class='" + c + "' title='" + t + "'";
  }
  public static bool IsDshForeground() {
    var h = GetForegroundWindow();
    var t = new StringBuilder(300); GetWindowTextW(h, t, 300);
    var c = new StringBuilder(120); GetClassNameW(h, c, 120);
    return c.ToString() == "Chrome_WidgetWin_1" && t.ToString().Contains("DeepSeek Harness");
  }
}
'@
Add-Type -TypeDefinition $code -ErrorAction SilentlyContinue

Write-Host "step 0  当前前台: $([W3]::Describe())"
Write-Host "step 1  用记事本抢走前台（这样'DSH 变回前台'就成了可观测信号）"
$np = Start-Process notepad -PassThru
Start-Sleep -Seconds 2
Write-Host "        现在前台: $([W3]::Describe())   (dshForeground=$([W3]::IsDshForeground()))"

Write-Host ""
Write-Host "=== A) 直接启动（和桌宠当前实现一样：UseShellExecute=false）==="
$before = (Get-Process 'DeepSeek Harness' -ErrorAction SilentlyContinue | Measure-Object).Count
$sw = [Diagnostics.Stopwatch]::StartNew()
$a = Start-Process -FilePath $exe -PassThru -WindowStyle Normal
$a.WaitForExit(12000) | Out-Null
$sw.Stop()
$exitedA = $a.HasExited
$codeA = if ($exitedA) { $a.ExitCode } else { 'still-running' }
$after = (Get-Process 'DeepSeek Harness' -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Host ("        进程数 {0} -> {1} ; hasExited={2} ; exitCode={3} ; {4}ms" -f $before, $after, $exitedA, $codeA, $sw.ElapsedMilliseconds)
Write-Host ("        dshForeground={0} ; 前台={1}" -f [W3]::IsDshForeground(), [W3]::Describe())

Start-Sleep -Seconds 2
if (-not [W3]::IsDshForeground()) { Start-Process notepad | Out-Null; Start-Sleep -Seconds 2 }

Write-Host ""
Write-Host "=== B) 经 Explorer 中转启动（子进程由 explorer 创建，不继承我的受限环境）==="
$sw2 = [Diagnostics.Stopwatch]::StartNew()
Start-Process -FilePath "explorer.exe" -ArgumentList "`"$exe`"" | Out-Null
$seen = $false
for ($i = 0; $i -lt 40; $i++) {
  Start-Sleep -Milliseconds 400
  if ([W3]::IsDshForeground()) { $seen = $true; break }
}
$sw2.Stop()
$after2 = (Get-Process 'DeepSeek Harness' -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Host ("        进程数 {0} ; dshForeground={1} ; 用时={2}ms" -f $after2, $seen, $sw2.ElapsedMilliseconds)
Write-Host ("        前台={0}" -f [W3]::Describe())

Write-Host ""
if ($codeA -eq -2147483645) { Write-Host "结论A: 直接启动在本受限父进程下复现了 STATUS_BREAKPOINT(0x80000003)" }
if ($seen) { Write-Host "结论B: 经 Explorer 中转成功触发了 DSH 自己的单实例接管（窗口回到前台）" }
else { Write-Host "结论B: Explorer 中转未观测到前台变化" }
Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force

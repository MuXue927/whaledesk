# 实验：从零冷启动 DSH（用独立的临时 user-data-dir，避免碰用户正在运行的实例与配置）
$ErrorActionPreference = 'Continue'
$exe = "D:\Software\DeepSeek Harness\DeepSeek Harness.exe"
$temp = Join-Path $env:TEMP ("dsh-cold-probe-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

$code = @'
using System;using System.Text;using System.Collections.Generic;using System.Runtime.InteropServices;
public static class W4 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  public static List<string> Dsh() {
    var list = new List<string>();
    EnumWindows((h,l) => {
      var t = new StringBuilder(400); GetWindowTextW(h,t,400);
      var c = new StringBuilder(200); GetClassNameW(h,c,200);
      if (t.ToString().Contains("DeepSeek Harness") && c.ToString() == "Chrome_WidgetWin_1") {
        int pid; GetWindowThreadProcessId(h, out pid);
        list.Add(string.Format("hwnd=0x{0:X} pid={1} visible={2} title='{3}'", h.ToInt64(), pid, IsWindowVisible(h), t));
      }
      return true;
    }, IntPtr.Zero);
    return list;
  }
}
'@
Add-Type -TypeDefinition $code -ErrorAction SilentlyContinue

$beforePids = @(Get-Process 'DeepSeek Harness' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
Write-Host "step 0  现有 DSH pid: $($beforePids -join ', ')"
Write-Host "step 0  现有 DSH 窗口:"; [W4]::Dsh() | ForEach-Object { "        $_" }
Write-Host "step 1  用独立 user-data-dir 冷启动: $temp"
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $exe -ArgumentList "--user-data-dir=`"$temp`"" -PassThru -WindowStyle Normal
Write-Host "        launch pid=$($p.Id)"

$newWindowAt = -1
for ($i = 0; $i -lt 90; $i++) {
  Start-Sleep -Milliseconds 1000
  $newPids = @(Get-Process 'DeepSeek Harness' -ErrorAction SilentlyContinue | Where-Object { $beforePids -notcontains $_.Id } | ForEach-Object { $_.Id })
  $wins = [W4]::Dsh()
  $newWins = $wins | Where-Object { $_ -match 'visible=True' }
  if ($i % 5 -eq 0) {
    Write-Host ("        t={0,3}s  新进程=[{1}]  可见DSH窗口数={2}  启动进程hasExited={3}" -f ($i*1), ($newPids -join ','), $newWins.Count, $p.HasExited)
  }
  if ($newWins.Count -gt 1 -or ($newWins.Count -ge 1 -and $newPids.Count -gt 0)) { $newWindowAt = $i; break }
  if ($p.HasExited -and $newPids.Count -eq 0 -and $i -gt 3) {
    Write-Host ("        t={0}s 启动进程已退出，无新进程 -> 可能是单实例接管(退出码 {1})" -f $i, $p.ExitCode)
    break
  }
}
$sw.Stop()
Write-Host ""
Write-Host "step 2  结果: 新窗口出现于第 $newWindowAt 秒 ; 总耗时 $($sw.ElapsedMilliseconds)ms ; 启动进程 hasExited=$($p.HasExited) exitCode=$(if($p.HasExited){$p.ExitCode}else{'running'})"
[W4]::Dsh() | ForEach-Object { "        $_" }

Write-Host "step 3  清理：只结束本次新建的进程，并删除临时目录"
$nowPids = @(Get-Process 'DeepSeek Harness' -ErrorAction SilentlyContinue | Where-Object { $beforePids -notcontains $_.Id } | ForEach-Object { $_.Id })
if ($nowPids.Count -gt 0) {
  Write-Host "        结束新进程: $($nowPids -join ', ')"
  $nowPids | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }
} else { Write-Host "        没有新增进程（说明它把请求交给了已有实例）" }
Start-Sleep -Seconds 2
Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "        完成后现有 pid: $((Get-Process 'DeepSeek Harness' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }) -join ', ')"

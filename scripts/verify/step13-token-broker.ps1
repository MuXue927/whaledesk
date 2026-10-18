# 实验：从「Low 完整性」进程里，能否借 explorer.exe 的 COM 代理拿到 Medium 令牌的子进程？
# 判据：子进程自己执行 whoami /groups，输出里的 Mandatory Label 是 Low 还是 Medium。
$ErrorActionPreference = 'Continue'
$w = "C:\Users\whxnt\OneDrive\文档\deepseek-harness\default-workspace"
$build = Join-Path $w "whaledesk\build"
New-Item -ItemType Directory -Force -Path $build | Out-Null   # 工作区内，允许写
$direct = Join-Path $build "whoami-direct.txt"
$broker = Join-Path $build "whoami-broker.txt"
Remove-Item $direct,$broker -ErrorAction SilentlyContinue

function Show-Label($file, $label) {
  if (-not (Test-Path $file)) { Write-Host ("  {0,-10} -> 文件未生成（子进程没跑起来）" -f $label); return }
  $text = Get-Content $file -Raw -Encoding UTF8
  $m = [regex]::Match($text, 'Mandatory Label\\([^:]+):')
  if ($m.Success) { Write-Host ("  {0,-10} -> {1}" -f $label, $m.Groups[1].Value.Trim()) }
  else { Write-Host ("  {0,-10} -> 未找到 Mandatory Label（文件 {1} 字节）" -f $label, $text.Length) }
}

Write-Host "=== A) 直接启动（当前桌宠的做法 Process.Start）==="
Start-Process -FilePath "$env:SystemRoot\System32\cmd.exe" `
  -ArgumentList "/c whoami /groups > `"$direct`"" -Wait -WindowStyle Hidden
Show-Label $direct "direct"

Write-Host "=== B) 经 Shell.Application（explorer.exe 的 COM 代理）启动 ==="
try {
  $shell = New-Object -ComObject Shell.Application
  # 第 5 个参数 0 = 隐藏窗口
  $shell.ShellExecute("cmd.exe", "/c whoami /groups > `"$broker`"", "", "open", 0)
  Start-Sleep -Seconds 3
  Show-Label $broker "broker"
} catch {
  Write-Host ("  broker 调用失败: " + $_.Exception.Message)
}

Write-Host ""
Write-Host "=== 结论 ==="
$d = if (Test-Path $direct) { (Get-Content $direct -Raw) } else { "" }
$b = if (Test-Path $broker) { (Get-Content $broker -Raw) } else { "" }
$dLow = $d -match 'Mandatory Label\\Low'
$bMed = $b -match 'Mandatory Label\\Medium'
if ($dLow) { Write-Host "  直接启动 = Low  ✓（复现了受限继承）" }
if ($bMed) { Write-Host "  COM 代理  = Medium  ★★★ 说明代理可以借到正常令牌 -> 这就是修复方案" }
elseif ($b -match 'Mandatory Label\\Low') { Write-Host "  COM 代理  = Low（代理没帮上忙）" }
elseif ($b -eq "") { Write-Host "  COM 代理  = 子进程未产生输出" }

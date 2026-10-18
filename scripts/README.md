# scripts\ 说明

所有版本号都来自 `WhaleDesk.csproj` 的 `<Version>`；`dist\VERSION.txt` 在每次构建时按 exe 的
FileVersion 重新生成。`install.cmd` 是**通用安装器**（把同目录的 `app\` 复制到 `%LOCALAPPDATA%\WhaleDesk`），
所以它本身不需要随版本改动，但 `build-dist.cmd` 每次都会把它和 `make-shortcut.ps1` 重新复制到 `dist\`。

## 构建 / 打包
| 文件 | 作用 |
|---|---|
| `build-dist.cmd` | 发布框架依赖版到 `dist\app`（约 6 MB，需要目标机装 .NET 9 桌面运行时） |
| `build-dist-selfcontained.cmd` | 发布自包含单文件版到 `dist-selfcontained\app`（约 150 MB，目标机不需要 .NET；首次需联网下载 runtime pack） |
| `package-release.cmd` | 把 `dist\` 打成 `release\WhaleDesk-<ver>.zip` + 发布说明 + SHA256 |
| `make-release-notes.ps1` | 生成发布说明（被 `package-release.cmd` 调用） |
| `make-checksum.ps1` | 生成 `SHA256.txt`（被 `package-release.cmd` 调用） |
| `NuGet.Config` | 构建时用的最小还原源配置（避免读取机器上不可读的 NuGet 配置） |

## 安装 / 启动 / 快捷方式
| 文件 | 作用 |
|---|---|
| `install.cmd` | 安装或更新到 `%LOCALAPPDATA%\WhaleDesk`：复制文件、保留回滚副本、清除低完整性标签、建桌面快捷方式、启动 |
| `make-shortcut.ps1` | 创建/修复桌面快捷方式「小鲸娘」（被 `install.cmd` 调用，也可单独运行） |
| `run-whaledesk.cmd` | 直接启动（调试用，不安装） |
| `run-whaledesk-diagnose.cmd` | 诊断启动：开日志/探针后启动，用于排查 |

## 发布 / 安全
| 文件 | 作用 |
|---|---|
| `publish-github.ps1` | 建仓库→push→创建 Release 并上传 zip（token 交互输入，只在本进程内存中使用） |
| `PUBLISH-GITHUB.md` | 发布步骤说明 |
| `unblock-files.ps1` | 批量解除下载文件的 Mark-of-the-Web（消除"打开文件-安全警告"） |
| `sign-self.cmd` | 本机自签名 exe（只对本机有效，不能替代 CA 证书） |

## 诊断
主要验证统一用 exe 自带探针，不需要额外脚本：
```
WhaleDesk.exe --selftest                 # 126 项逻辑/采样自检
WhaleDesk.exe --rehearsal                # 清理 dry-run（不删除）
WhaleDesk.exe --live --seconds 10 --probe-menu-monitor 0
WhaleDesk.exe --live --seconds 12 --probe-dsh-api
WhaleDesk.exe --live --seconds 12 --probe-hotplug --dsh-base http://127.0.0.1:64688
WhaleDesk.exe --live --seconds 8  --probe-follow
WhaleDesk.exe --live --seconds 6  --probe-clipboard-image   # 不碰剪贴板
WhaleDesk.exe --live --seconds 10 --probe-update
```
`verify\` 只保留 4 个"研究型"脚本（不是日常验证）：
| 文件 | 保留原因 |
|---|---|
| `step10-dsh-window.ps1` | DSH 隐藏到托盘后无法重新打开的回归场景 |
| `step11-launch-channel.ps1` | 低完整性进程启动 DSH 的通道实验 |
| `step12-cold-boot.ps1` | 独立 user-data-dir 冷启动实验 |
| `step13-token-broker.ps1` | 低完整性→Medium 令牌代理实验 |

原先的 step2~step7（窗口/点击/菜单/多屏/监控卡/清理卡）已删除：这些场景现在由
`--probe-hittest/interact/menu/monitors/monitor-panel/cleanup-panel` 与 `--selftest` 覆盖。

## 开发工具
| 文件 | 作用 |
|---|---|
| `make-icon-v4.py` | 一次性图标生成脚本（Python），产出 assets 里的 ico/png；平时不需要运行 |
| `neutral-backdrop.ps1` | 文档截图用的全屏中性背景（置顶深色渐变，不改壁纸） |
| `capture-doc-shots.ps1` | 一键生成 `docs\images\` 的 12 张中性背景截图（启动探针 → 截图 → 自动裁剪） |
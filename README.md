# WhaleDesk 小鲸娘桌面伴侣

一个 .NET 9 WPF 桌面宠物：住在桌面右下角，陪你看 DSH 会话状态、监控内存/磁盘/GPU、
做 C 盘清理与空间分析，还有跟随光标、专注番茄钟、剪贴板历史、久坐提醒等小功能。

## 平台支持（重要）

| 平台 | 状态 | 说明 |
|---|---|---|
| **Windows 10 / 11（x64）** | ✅ **已支持并实测** | 唯一受支持的平台；当前版本在 Windows 11 上完成 126 项自检、清理演练与真机截图验证 |
| macOS（Intel / Apple Silicon） | ❌ **不支持** | 技术栈为 WPF + Win32 API，**无法在 macOS 上运行**（不是"没测"，是跑不起来） |
| Linux（X11 / Wayland） | ❌ **不支持** | 同上；且目标用户较少，暂无计划 |

**为什么暂时不做 macOS/Linux**：本项目的 UI（透明置顶窗口、点击穿透、多显示器 DPI、托盘、动画）、
系统能力（注册表自启动、回收站、进程/GPU 性能计数器、前台窗口句柄）都基于 **Windows 专属 API**。
跨平台需要把 UI 换成 Avalonia/MAUI 并重写各平台服务层，工作量以"周/月"计，且 macOS 还需要
Apple 开发者账号做签名与 notarization；**在没有 Mac 真机验证的情况下，无法承诺任何 macOS 功能正常**。

> 如果你在非 Windows 系统上看到本项目，请把它理解为"Windows 桌面宠物"，不要期待在这里运行。

## 文档
- [功能说明](docs/功能说明.md)（发布页版：12 张截图/安装/校验/FAQ）
- [技术说明](docs/技术说明.md)
- [安全说明](docs/安全说明.md)
- [互动说明书](docs/互动说明书.md)（每个功能怎么用、彩蛋与台词）
- [设计方案](docs/设计方案.md)（完整技术档案：架构/模块/历史问题/验证）

## 安装
1. 下载 `WhaleDesk-<version>.zip` 并解压
2. 右键解压出来的 `install.cmd` → **解除锁定**（属性 → 勾选"解除锁定"），或运行 `scripts\unblock-files.ps1`
3. 双击 `install.cmd`，安装到 `%LOCALAPPDATA%\WhaleDesk`
4. 桌面会出现「小鲸娘」快捷方式

> 提示：如果 Windows 提示"未知发布者"，见 [防"未知发布者"提示](#防未知发布者提示)。

## 自包含版（不需要 .NET）
```
whaledesk\scripts\build-dist-selfcontained.cmd
```
产出 `dist-selfcontained\`（约 150 MB，自带 .NET 运行时）。

## 命令行
| 开关 | 作用 |
|---|---|
| `--selftest` | 126 项自检 |
| `--rehearsal` | 清理演练（dry-run） |
| `--probe-update` | 检查 GitHub Releases 有没有新版本 |
| `--probe-dsh-api` | 探测 DSH 接口（自动发现端口） |
| `--probe-hotplug` | DSH 掉线/恢复端到端测试 |
| `--help` | 全部开关 |

## 防"未知发布者"提示
Windows 对**没有数字签名**的 exe/cmd 会弹 SmartScreen 或"打开文件-安全警告"：

| 方案 | 效果 | 成本 |
|---|---|---|
| 右键文件 → 属性 → 解除锁定（或 `Unblock-File`） | 只对**本机、本次**有效，不弹"打开文件-安全警告" | 免费 |
| 代码签名证书（OV/EV）签名 exe + cmd | 对所有用户都不提示 | 约 $100–400/年 |
| 自签名证书 + 导入"受信任的根证书颁发机构" | 只对你自己的电脑有效 | 免费，但不适合分发 |
| 上架 Microsoft Store | 不提示 | 需开发者账号 |

`scripts\` 里提供 `sign-self.cmd`（自签名，仅本机）与 `unblock-files.ps1`（批量解除锁定）。

## 许可
代码 MIT。精灵素材版权归原作者（@linxin666），请勿单独再分发素材。
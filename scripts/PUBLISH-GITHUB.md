# 发布到 GitHub 的步骤

> **本机现状（2026-10-24 核实）**：发布链路已经通了，**不再需要第三方工具代劳**。
> - `gh` CLI 2.102.0（scoop 装在 `D:\Software\scoop`，已在用户 PATH 里），已登录账号 **MuXue927**（OAuth 令牌，作用域 `repo` / `workflow` / `read:org` / `gist`）
> - git 侧另有一份 HTTPS 凭据存在 Windows 凭据管理器（`git:https://github.com`），所以 `git push` 无需交互
> - 2026-10-24 的 **v0.24.6** 与 **v0.25.0** 都是 DSH 会话直接完成 `commit → tag → push → Release → 上传附件` 的，全程无人工粘贴令牌
>
> 谁执行都行：你自己在终端跑，或让 DSH 跑。下面是同一套命令。

版本号规则见 [设计方案 §14](../docs/设计方案.md)：修 bug → 修位+1，加功能 → 次位+1，发布时打 `vX.Y.Z` tag。下面命令里的 `<版本>` 换成三位版本号（例如 `0.24.6`）。

## 1. 打包

```bat
whaledesk\scripts\build-dist.cmd              :: 框架依赖版：发布 + 组装 dist\
whaledesk\scripts\build-dist-selfcontained.cmd :: 自包含版：发布 + 组装 dist-selfcontained\（首次需联网取 win-x64 运行时包）
```

然后把两个目录各打成一个 zip（`package-release.cmd` 只处理 `dist\`，自包含包需要单独打包）：

```powershell
Compress-Archive -Path whaledesk\dist\*             -DestinationPath whaledesk\release\WhaleDesk-<版本>.zip             -Force
Compress-Archive -Path whaledesk\dist-selfcontained\* -DestinationPath whaledesk\release\WhaleDesk-<版本>-selfcontained.zip -Force
```

产物（`whaledesk\release\`）：

| 文件 | 用途 |
|---|---|
| `WhaleDesk-<版本>.zip` | 框架依赖版（含 exe/dll/assets/install.cmd/make-shortcut.ps1/VERSION.txt） |
| `WhaleDesk-<版本>-selfcontained.zip` | 自包含版（自带 .NET 运行时，目标机无需安装） |
| `RELEASE_NOTES-<版本>.md` | 发布说明（手写；`make-release-notes.ps1` 生成的模板可作底稿） |
| `SHA256.txt` | 两个包的校验和（下载者可自证未被篡改） |

## 2. 发布到 GitHub

```bat
cd /d <仓库目录>
git add -A
git commit -m "release: WhaleDesk <版本>（<一句话主题>）"
git tag -a v<版本> -m "WhaleDesk <版本> — <一句话主题>"
git push origin main --tags
gh release create v<版本> ^
   "release\WhaleDesk-<版本>.zip" ^
   "release\WhaleDesk-<版本>-selfcontained.zip" ^
   --title "WhaleDesk <版本>" ^
   --notes-file "release\RELEASE_NOTES-<版本>.md"
```

**兜底路径**（`gh` 不可用时）：

1. **API**：用 Windows 凭据管理器里的令牌调 `POST /repos/<owner>/<repo>/releases`，再往上传地址 `POST <upload_url>?name=<文件名>`（0.24.6 用的就是这条）
2. **网页**：Releases → Draft a new release → 选 tag → 上传两个 zip → 粘贴 `RELEASE_NOTES-<版本>.md`

> 注意：`git push` 用的是凭据管理器里的凭据，`gh` 用的是自己的 OAuth 令牌，两份是独立的。改密码/吊销令牌后要各自重新登录（`git credential-manager` / `gh auth login`）。

## 3. 发布前自检清单

- [ ] `WhaleDesk.exe --selftest` → **0 失败**（0.25.0 实测：150 通过 + 5 条环境受限跳过；那 5 条会在可写目录下照常执行，否则明确记 SKIP 而不是假装通过）
- [ ] `WhaleDesk.exe --rehearsal` → 33/33（0.25.0 实测 `33 pass / 0 fail / 0 degraded`）
- [ ] `WhaleDesk.exe --probe-dsh-api` → 核心端点 OK（有登录才有的端点显示 AUTH 属正常）
- [ ] `WhaleDesk.exe --probe-menu` / `--probe-cleanup-panel` / `--probe-launcher` → 无异常
- [ ] 两个 exe 的 FileVersion 一致，且等于 csproj 里的 `<FileVersion>`
- [ ] `release\SHA256.txt` 里两行哈希与实际 zip 一致
- [ ] 安装器在一台干净机器（或新用户）上装一次，快捷方式能创建、能启动
- [ ] zip 内不含 `state\`、`build\`、日志（这些是用户数据）

## 4. 发布后核对

```bat
gh release view v<版本> --repo MuXue927/whaledesk
gh release list --repo MuXue927/whaledesk --limit 3
```

期望：`Latest` 指向新版本，附件两个、`state` 均为 `uploaded`，大小与本地 zip 一致。

## 5. 需要如实写进发布说明的限制

- 未签名 exe：SmartScreen 首次可能警告
- 框架依赖版：目标机需要 **.NET 9 桌面运行时**；自包含版不需要
- 精灵素材版权归原作者（`@linxin666`），发布包内需保留署名
- 依赖 DSH 插件（`@linxin666/dsh-pet`、`dsh-usage`）；缺插件时功能降级（见技术档案 §8.2）

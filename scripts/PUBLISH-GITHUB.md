# 发布到 GitHub 的步骤（P4 收尾）

本机不会自动推送到 GitHub（需要你的仓库与凭据）。桌宠这边负责**把发布物准备好**，你负责最后一步。

## 1. 打包

```bat
whaledesk\scripts\build-dist.cmd      :: 先发布 + 汇编 dist
whaledesk\scripts\package-release.cmd :: 生成 release\WhaleDesk-<版本>.zip + 发布说明 + SHA256
```

产物（`whaledesk\release\`）：

| 文件 | 用途 |
|---|---|
| `WhaleDesk-0.16.0.zip` | 发布包（含 exe/dll/assets/install.cmd/make-shortcut.ps1/VERSION.txt） |
| `RELEASE_NOTES-0.16.0.md` | 发布说明（自动生成，可手动补充"本版亮点"） |
| `SHA256.txt` | 校验和（下载者可自证未被篡改） |

## 2. 发布到 GitHub（需要 `gh` 已登录，或手动在网页上传）

```bat
cd /d <你的仓库目录>
git add -A
git commit -m "WhaleDesk 0.16.0"
git tag v0.16.0
git push origin main --tags
gh release create v0.16.0 "whaledesk\release\WhaleDesk-0.16.0.zip" ^
   --title "WhaleDesk 0.16.0" ^
   --notes-file "whaledesk\release\RELEASE_NOTES-0.16.0.md"
```

没有 `gh` 也可以：GitHub 网页 → Releases → Draft a new release → 选 tag `v0.16.0` → 上传 zip → 粘贴说明。

## 3. 发布前自检清单

- [ ] `WhaleDesk.exe --selftest` → 全部通过
- [ ] `WhaleDesk.exe --rehearsal` → 33/33
- [ ] `WhaleDesk.exe --probe-dsh-api` → 核心端点 OK（有登录才有的端点显示 AUTH 属正常）
- [ ] 安装器在一台干净机器（或新用户）上装一次，快捷方式能创建、能启动
- [ ] zip 内不含 `state\`、`build\`、日志（这些是用户数据）

## 4. 需要如实写进发布说明的限制

- 未签名 exe：SmartScreen 首次可能警告
- 框架依赖：目标机需要 **.NET 9 桌面运行时**
- 精灵素材版权归原作者（`@linxin666`），发布包内需保留署名
- 依赖 DSH 插件（`@linxin666/dsh-pet`、`dsh-usage`）；缺插件时功能降级（见技术档案 §8.2）

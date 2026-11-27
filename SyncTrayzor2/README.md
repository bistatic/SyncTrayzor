# SyncTrayzor2

下一代 [Syncthing](https://syncthing.net/) Windows 托盘程序 — 完整支持 **Syncthing 2.x**，保留原版 [SyncTrayzor](https://github.com/canton7/SyncTrayzor) 全部核心功能。

> 原版 SyncTrayzor 1.1.29（2021 年）使用 Syncthing 1.x 的单横线参数语法（`-no-browser`）。当 Syncthing 自动升级到 2.x 后，参数解析器更换为双横线语法（`--no-browser`），导致托盘启动失败并反复报错：
>
> ```
> syncthing.exe: error: unknown flag -n, did you mean one of "-h", "-C", "-D", "-H"?
> ```
>
> SyncTrayzor2 通过**版本自适应参数引擎**彻底解决该问题。

## 2.0 版本核心特点

### 🔄 1.x / 2.x 版本自适应（核心）

启动前先探测 Syncthing 版本，再按对应语法生成命令行，一套程序同时兼容两代 Syncthing：

| Syncthing 版本 | 生成的启动参数 |
|---|---|
| **2.x** | `serve --no-browser --no-console --no-restart --no-upgrade --home=... --gui-address=...` |
| **1.x** | `-no-browser -no-console -no-restart -no-upgrade -home=... -gui-address=...` |

额外兜底：设置中误填单横线长参数（`-no-browser`）时自动修正为 `--no-browser`；默认禁止 Syncthing 自动升级，避免再次触发版本兼容问题。

### ⬇️ 自动下载 Syncthing

系统中找不到 syncthing.exe 时，自动从官方 GitHub Release 下载最新版到本地：

- 三级下载源回退：镜像加速 → API 资产端点 → 官方直连（单源 5 分钟超时自动切换）
- **SHA256 校验**（比对官方 `digest` 字段），不一致即丢弃，防篡改/传输损坏
- 原子替换就位（`.new` → 正式文件，旧版保留 `.old` 备份）
- 托盘气泡 + 实时进度（"正在下载 Syncthing… x/13.6 MB"）
- 也可在托盘菜单手动触发"下载最新版 Syncthing…"

### 🖥️ 现代化界面

- **WebView2 内置浏览器**（替代原版 CEF/Chromium Embedded）——随系统更新，无需捆绑上百 MB 的浏览器内核
- 自动带 API Key 登录 Syncthing 网页界面，主窗口打开即用
- 三行 TableLayoutPanel 固定布局，彻底避免原版工具栏遮挡网页内容的问题
- 状态栏常驻显示当前 Syncthing 版本与实际数据目录

### 📊 状态感知托盘

- 动态绘制托盘图标：空闲（蓝）/ 同步中（绿，旋转动画）/ 暂停（灰）/ 错误（红）
- 托盘菜单逐文件夹显示同步状态与剩余字节
- 悬停即可查看当前使用的 syncthing.exe 完整路径与数据目录
- 设备连接/断开、同步完成、错误、冲突文件气泡通知

### 🛠️ 其他保留与增强的功能

- Syncthing 子进程全生命周期管理：优雅停止（REST shutdown → 等待 → 兜底 Kill）、异常退出自动重启（1 分钟 4 次限流防风暴）
- 冲突文件扫描（`*.sync-conflict-*`）：定位到文件、移入回收站（非永久删除）
- 三标签页设置（常规 / Syncthing / 高级）：路径自动探测、进程优先级、GUI 地址、额外参数、API Key 只读显示、"实际使用"路径透明化
- 单实例互斥（二次启动自动唤出已有窗口）
- 登录自启（HKCU Run）、关闭到托盘、启动最小化
- 日志体系：程序日志 + Syncthing 输出分文件，内存环形缓冲 + 内置实时日志窗口

## 与原版 SyncTrayzor 的差异

| 功能 | 原版 1.1.29 | SyncTrayzor2 |
|---|---|---|
| Syncthing 2.x 支持 | ❌（报 unknown flag -n） | ✅ 版本自适应 |
| 内置浏览器 | CEF（捆绑 150+ MB） | WebView2（系统自带） |
| 自动下载 Syncthing | ❌ 仅自动升级 | ✅ 官方源 + SHA256 校验 |
| 传输进度小窗/速率曲线 | ✅ | ❌（可在 Syncthing 网页查看） |
| 自动更新自身 | ✅ 私有服务器 | ❌（可从 Release 手动更新） |
| 冲突文件处理 | 扫描+对比工具 | 定位 / 回收站删除 |
| .NET 运行时 | .NET Framework 4.7.2 | .NET 8 Desktop Runtime |

## 安装

### 前置要求

- Windows 10 1809+ / Windows 11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Win11 及多数 Win10 已预装）

### 使用

1. 下载并解压 `SyncTrayzor2-x64.zip` 到任意可写目录
2. 运行 `SyncTrayzor2.exe`
3. **无需手动准备 syncthing.exe** —— 首次运行会自动下载官方最新版到 `data\` 目录
4. 如需沿用已有 Syncthing 设备/文件夹配置：设置 → Syncthing → "配置/数据目录" 填原 Syncthing home 路径（如 `%LOCALAPPDATA%\Syncthing`）

### 从原版 SyncTrayzor 迁移

| 原版形态 | 迁移动作 |
|---|---|
| 便携版 | 配置目录填 `SyncTrayzorPortable\data\syncthing`，设备/文件夹/API Key 自动沿用 |
| 安装版 | 配置目录填 `%LOCALAPPDATA%\Syncthing` |
| 手动迁移 | 复制原版 `data\config.xml` 中的 `<apikey>` 亦可 |

## 构建

```powershell
dotnet publish SyncTrayzor2.csproj -c Release -o dist
```

Release 配置已启用：确定性构建、路径脱敏（`PathMap`）、关闭 PDB，发布产物不含构建机本地路径。

## 技术栈

- C# / .NET 8 / WinForms
- [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2)（内置浏览器）
- System.Net.Http.Json / System.Text.Json（REST 客户端，零第三方依赖）
- GDI+ 动态托盘图标

## 许可证

遵循上游 [SyncTrayzor](https://github.com/canton7/SyncTrayzor) 的开源许可（MIT）。

> AI生成

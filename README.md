# Modern Recycle Bin

**一个更现代、更快、更好用的 Windows 回收站。** —— 用 WebView2 + HTML/CSS 重写了回收站界面，
提供 Windows 11 风格的观感，并补上了系统回收站一直缺失的实用功能。

> A modern, faster, better Recycle Bin for Windows 11 — built with WebView2 + HTML/CSS.

![screenshot](docs/screenshot-main.png)

---

## 为什么做这个

Windows 自带的回收站有几个长期被诟病的问题：

| 系统回收站的问题 | Modern Recycle Bin |
| --- | --- |
| ❌ 只能还原到**原位置**，没法还原到别处 | ✅ **还原到…**：自选任意文件夹 |
| ❌ 不能把文件**复制出来**（只能连根拔走） | ✅ **复制到…**：复制一份出去，原件保留 |
| ❌ 看不到内容，只能靠文件名猜 | ✅ **图片缩略图预览**（选中即显示） |
| ❌ 搜索慢、不能按类型找 | ✅ **按类型筛选**（图片/视频/音频/文档/压缩包/代码/文件夹）+ 实时搜索 |
| ❌ 界面是十年前的样式 | ✅ **Windows 11 风格**：暗色主题、40px 行高、圆角悬停、流畅动效 |
| ❌ 同名文件冲突处理含糊 | ✅ **覆盖 / 跳过 / 保留两者** 三种策略 |
| ❌ 无法调整信息密度 | ✅ **舒适 / 紧凑** 两种行距 + **Ctrl+滚轮缩放** |
| ❌ 清空后桌面图标有时不刷新 | ✅ 主动通知 shell，图标始终同步 |

## 功能一览

- **还原**：还原到原位置 / 还原到任意文件夹 / 还原全部
- **删除**：永久删除选中项 / 清空回收站（走系统 API，桌面图标同步更新）
- **复制到…**：把文件复制出来而不从回收站移除
- **详情面板**：类型、大小、原位置、删除时间、修改时间，图片显示缩略图
- **筛选与排序**：按类型筛选、点列头排序、实时搜索（名称/原位置/类型）
- **右键菜单**：还原 / 还原到… / 复制到… / 打开原位置 / 属性 / 复制路径 / 永久删除
- **快捷键**：`Enter` 还原、`Delete` 删除、`Ctrl+A` 全选、`Ctrl+F` 搜索、`F5` 刷新、`Ctrl+0/+/-` 缩放
- **未选中任何项时**显示"回收站为空"，与系统一致

## 安装

### 方式一：一键安装（推荐）

1. 下载 [`ModernRecycleBinSetup.exe`](../../releases/latest)
2. 双击运行 —— 无需管理员权限
3. 勾选「让桌面上的回收站用它打开」，安装完成

安装器会把程序放到 `%LOCALAPPDATA%\ModernRecycleBin`，并做两件事（都可选、都可撤销）：

- 创建桌面快捷方式
- 让桌面上的「回收站」双击后用本程序打开（写入**当前用户**注册表）

想卸载：运行安装目录里的 `Uninstall.cmd`，或在「设置 → 应用」里卸载。
卸载会**自动把桌面回收站恢复为系统默认**。

### 方式二：绿色便携版

下载 Release 里的 `ModernRecycleBin-portable.zip`，解压后直接运行 `RecycleBin.exe`。
不写注册表、不留痕迹，删掉文件夹即卸载。

### 系统要求

- Windows 10 / 11（64 位）
- **WebView2 运行时** —— Windows 11 与较新的 Windows 10 已内置；
  若提示缺失，装一下微软官方组件即可：<https://go.microsoft.com/fwlink/p/?LinkId=2124703>

## 技术实现

- **界面**：WebView2 承载 HTML/CSS/JS，宿主是我们自己的 WinForms 窗口
  （因此任务栏图标依然是回收站图标，而不是浏览器图标）
- **数据与操作**：C# 直接解析 `$Recycle.Bin` 里的 `$R`（数据）/ `$I`（元数据）配对
  - 还原、删除、清空都成对处理并在完成后**校验**
  - 每次操作后清理残留的孤儿 `$I`，避免"回收站空了但桌面图标还是满的"
  - 清空走 `SHEmptyRecycleBin`，让 shell 自己执行，桌面图标才会同步
- **图标**：从 shell 的 256px（jumbo）图标列表取图后缩放；窗口图标自建**多尺寸 ICO**
  （含 20/24/40 等 DPI 对应尺寸），保证标题栏与任务栏都清晰
- **启动优化**：WebView2 运行时与窗口创建并行启动；页面用 `NavigateToString` 直接注入
  （避免虚拟域名走网络栈导致的 2 秒延迟）。实测窗口 **~100ms** 出现、**~300ms** 完整渲染

## 从源码编译

只需要 .NET Framework 自带的编译器，**不需要安装任何 SDK**：

```cmd
build.cmd
```

脚本会：

1. 用 `csc.exe` 编译 `src/RbWeb.cs`
2. 组装 `dist/`（可运行的便携版）
3. 打包 payload 并生成单文件安装器 `dist/ModernRecycleBinSetup.exe`

依赖的 `Microsoft.Web.WebView2` 官方 SDK 已随仓库放在 `lib/`（含其许可证）。

## 项目结构

```
src/RbWeb.cs              宿主程序（C#，界面逻辑与文件操作）
src/ui/index.html         界面（HTML/CSS/JS，自绘列表与详情面板）
installer/Setup.cs        单文件安装器 / 卸载器
lib/                      WebView2 官方 SDK（net462 + x64 原生库）
build.cmd                 一键构建
dist/                     构建产物
docs/                     截图
```

## 已知限制

- 回收站里的文件在 `$Recycle.Bin` 下是 `$R` 内部名，因此**不提供"剪切"**
  （自己实现剪切会粘出错误的文件名；系统靠内部处理才正确）
- 还原/删除依赖直接操作 `$R`/`$I`，遇到权限异常的文件会跳过并在结果里报告

## 许可证

本项目代码采用 **MIT** 许可。第三方组件许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

---

## English

A modern Recycle Bin for Windows, built with WebView2 + HTML/CSS inside its own
WinForms window (so the taskbar keeps the Recycle Bin icon).

Highlights: restore to any folder, copy files out without removing them, image
thumbnails, type filter, Windows 11 dark styling, per-user install with no
admin rights, and a clean uninstall that restores the system default.

See the sections above for install and build instructions.

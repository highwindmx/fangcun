# 方寸 (Fangcun)

> 方寸之间，图标各安其位。

一个面向 Windows 桌面的**图标围栏（Icon Fences）工具**，类似 Stardock Fences。用多个可自定义的“围栏”把桌面图标分门别类收纳：半透明圆角、可拖动缩放、随桌面壁纸自适应、不受“显示桌面(Win+D)”影响。

![.NET](https://img.shields.io/badge/.NET-10.0--windows-blue) ![Lang](https://img.shields.io/badge/Lang-C%23%20%2F%20WPF-green) ![License](https://img.shields.io/badge/License-MIT-yellow)

* * *

## 功能特性

-   **图标围栏分组收纳**：创建多个围栏，把常用快捷方式、文件拖进去，桌面从此整洁清晰。
-   **真实图标渲染**：读取目标自身的真实文件/快捷方式图标，双击即用系统 `ShellExecute` 打开。
-   **半透明圆角**：围栏为 per-pixel alpha 分层窗口，支持圆角与任意半透明度。
-   **Win+D 免疫**：围栏是独立顶层窗口但把**所有者(owner)** 挂到桌面 `SHELLDLL_DefView`——按 Win+D 显示桌面时围栏**不会隐藏**，且可被普通窗口正常遮挡（不置顶）。
-   **全功能围栏操作**：
    -   标题栏拖动移动；双击标题栏重命名。
    -   四边/四角原生缩放（缩放光标跟随）。
    -   条目拖放、右键菜单。
    -   溢出模式：**滚动** 或 **省略**（超出容量折叠为“还有 N 项”）。
    -   显示模式：**图标** 或 **列表**。
-   **随桌面壁纸自适应**：可选“随桌面自适应”——按围栏所在壁纸区域的**明暗**生成**中性半透明玻璃**背景（RGB 与壁纸色相无关、保留 ~40% 透出桌面，观感始终透明可辨，不会因“同色叠自己”而像一块不透明色板），换壁纸自动刷新；栏底(标题栏)玻璃同源略暗，保持可分辨；**拖动/缩放围栏时也会按所在桌面区域实时重算**（200ms 防抖，避免拖动每帧采样卡顿）。条目与标题字体颜色按**玻璃灰度**自动在**黑/白**间切换（标题字单独按更暗的栏底玻璃判定，深栏底必出白字），保证任何壁纸下都清晰可读。
-   **系统托盘常驻**：开机自启、新建围栏、一键\*\*隐藏/显示所有围栏(N)\*\*、退出。
-   **多实例免疫**：单实例守卫，双击 exe 不会重复叠加启动。

## 界面

配置窗口按“背景样式 → 标题栏样式 → 条目样式”组织（预设主题在右键「预设主题」子菜单，不在配置窗内）；围栏右键「预设主题」子菜单含「自适应/浅色/深色/自定义」，前三项立即应用、「自定义」进入配置窗手动调色，且当前主题会在子菜单对应项上打勾（自定义态勾在「自定义」上）。手动改任意自定义配色会自动退出自适应（自定义色不被后续壁纸重算覆盖）。菜单中点选「⋯」按钮或右键标题栏可快速切换显示模式与溢出模式。

## 快速开始

### 直接使用（发行版）

下载 Release 的单文件 `Fangcun.exe`，双击即可运行，无需安装 .NET（`--no-self-contained` 版需本机装有 .NET 10 Desktop Runtime）。

### 从源码构建

环境：Windows + [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```bash
git clone git@github.com:highwindmx/fangcun.git
cd fangcun

# Debug 运行
dotnet run --project Fangcun

# 发布单文件 exe（win-x64，框架依赖）
dotnet publish Fangcun -c Release -r win-x64 --no-self-contained \
  -p:PublishSingleFile=true -p:PublishReadyToRun=false
```

产物位于 `Fangcun/bin/Release/net10.0-windows/win-x64/publish/Fangcun.exe`。

## 数据与日志

-   配置：`%LocalAppData%\Fangcun\config.json`（围栏列表、位置尺寸、样式）。
-   运行日志：`%LocalAppData%\Fangcun\fangcun.log`（排障用，尤其 Win+D 免疫的 owner 挂载状态）。

## 架构要点（技术速览）

主题

方案

窗口模型

顶层 `Window` + `AllowsTransparency`（per-pixel alpha），非 reparent 子窗

Win+D 免疫

`SetWindowLongPtr(hwnd, GWL_HWNDPARENT, SHELLDLL_DefView)` 设 **owner**，非 `SetParent` 子窗关系 → 半透明圆角正常 + 不随 Win+D 隐藏 + 坐标零污染

缩放/移动

`WM_NCHITTEST` 返回 `HT*`/`HTCAPTION` 交系统原生处理（根 `ResizeMode=CanResize`）

圆角

WPF `CornerRadius` + `Clip`

图标

`SHGetFileInfo` 取目标自身真实图标

自适应背景

`WallpaperTint` 采样壁纸区域明暗 → 生成中性玻璃色（RGB 与壁纸色相无关），`SystemEvents.UserPreferenceChanged` 即时刷新；心跳另做壁纸签名（路径+时间戳）轮询兜底，覆盖第三方换壁纸不触发系统事件的情况；`LocationChanged`/`SizeChanged` 在自适应开启时以 200ms 防抖重算，使围栏移到不同亮度的桌面区域时背景随位置变化。字体色由 `ComputeInk` 基于玻璃灰度 `v`（栏底用 `v*0.82`）判定黑/白，根除"栏底压暗后标题黑字看不清"的问题。

## 鸣谢

本项目在“让 Windows 围栏既保持半透明圆角、又能免疫 `Win+D`”这一难点上，深受 \*\*[openFence](https://github.com/weiweigogo/openFrence)\*\*（C++/Win32 桌面围栏）的思路启发——尤其是“贴桌面 + 分层合成”的窗口宿主机制，以及“显示桌面只最小化顶层窗口、归入桌面归属即可逃逸”这一关键原理的印证。在此对 openFence 项目的作者与贡献者表示诚挚感谢。

Powered by WorkBuddy

## 许可证

[MIT](LICENSE) © 2026 highwindmx
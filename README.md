# 🎬 Mosaic MKV Muxer（3m_tool）

> **从零到一的完整项目** —— Git 父子仓库 + HTTP Bridge + 多语言协作，一个 MKV 批量合成工具。

[![Rust](https://img.shields.io/badge/backend-Rust-orange?logo=rust)](https://www.rust-lang.org/)
[![C#](https://img.shields.io/badge/frontend-C%23%20WinUI%203-blue?logo=dotnet)](https://learn.microsoft.com/en-us/windows/apps/winui/)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-lightgrey?logo=windows)](https://www.microsoft.com/windows)

---

## 📖 项目简介

**3m_tool** 是一个 Windows 桌面应用，将动漫/影视资源的 MP4（视频）、M4A（音频）和 JSON（字幕）批量合成为 MKV。

### ✨ 核心特性

- 🧩 **批量扫描**：按文件名自动匹配 MP4 + M4A + JSON 字幕
- 🚀 **高性能合并**：FFmpeg 驱动，SSE 流式进度推送
- 🖥️ **WinUI 3 原生界面**：NavigationView、系统托盘、Mica 材质
- 🌐 **前后端分离**：GUI ↔ HTTP REST + SSE ↔ Rust 后端
- 🛠️ **CLI 独立模式**：纯命令行跨平台运行（Windows / macOS / Linux）

---

## 🏗️ 项目架构

```
MosaicMKVMuxer/          ← 根 Git 仓库
├── 3m_gui/              ← C# WinUI 3 前端
│   ├── MainWindow.xaml
│   ├── MainWindow.xaml.cs
│   ├── BackendClient.cs  # HTTP 客户端
│   ├── Models.cs
│   └── Logger.cs
│
├── 3m_core/             ← Git Submodule
│   └── src/
│       ├── main.rs       # CLI 入口（File / Folder / Serve 三模式）
│       ├── server.rs     # axum HTTP API + SSE
│       ├── merger.rs     # FFmpeg 合并引擎
│       ├── detector.rs   # FFmpeg 跨平台自动检测
│       └── workspace.rs  # 文件扫描与 stem 匹配
│
└── scripts/
    └── build.ps1
```

### 🔗 为什么用父子仓库？

`git submodule` 让 Rust 核心引擎作为独立仓库存在——可单独发版、打 tag、被其他项目引用，同时 GUI 仓库通过 submodule 指针锁定兼容版本。两个仓库各自独立演进，互不耦合。

---

## 🔌 HTTP Bridge 架构

GUI 与后端**不共享任何内存**，完全通过本地 HTTP 通信：

```
┌──────────────┐    HTTP REST + SSE    ┌──────────────┐
│  3m_gui      │ ◄──────────────────► │  m3_core     │
│  (C# WinUI)  │   localhost:随机端口   │  (Rust CLI)  │
└──────┬───────┘                       └──────┬───────┘
       │ 启动 m3_core serve --port N          │
       │ POST /api/scan                       │ 调用 FFmpeg
       │ POST /api/mux/start (SSE)            │ 扫描文件
       │ POST /api/convert                    │
       │ GET  /health                         │
```

过去写的项目前后端高耦合，这次刻意用 HTTP Bridge 做**进程级解耦**——前端崩溃不影响后端进程，两端各自可独立测试、替换、部署。这是从单体思维走向微服务思维的一次实践。

---

## 🚀 快速开始

### 环境要求

- Windows 10 (Build 19041+) 或 Windows 11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Rust](https://rustup.rs/)
- [FFmpeg](https://ffmpeg.org/)（运行时自动检测）

### 构建

```powershell
# 单架构 ZIP 打包
.\scripts\build.ps1 -Arch x64

# 全量构建：ZIP + MSIX + MSI（x64 + ARM64）
.\scripts\build_all.ps1

# 单独构建 MSIX / MSI
.\scripts\build_msix.ps1
.\scripts\build_msi.ps1   # 需要 WiX: winget install WiXToolset.WiXToolset
```

### 运行

```powershell
.\build\x64\publish\ThreeMGui.exe
```

### CLI 独立使用

```bash
m3_core file --mp4 video.mp4 --m4a audio.m4a
m3_core folder -i ./MyAnime -o ./output
m3_core serve --port 18900
```

---

## 🎯 这个项目的意义

### 技术栈背景

作为 OI 退役选手，C 系列是我的母语。Rust 继承了 C/CPP 的零成本抽象哲学，Python 是趁手的数学胶水，Go 填补了 Python 在并发和部署侧的短板。过去写过 Rust、C、Go、Python、TS/JS、Vue、React、Flutter、Dart 的项目，但一直缺一个**完整闭环的端到端项目**。

### 为什么选 WinUI 3？

跨平台方案我过去只用过 WebView 壳和 Flutter。Flutter 虽好，但 Skia 渲染层与原生控件的割裂感一直存在。WinUI 3 是微软原生 UI 框架的当代方案——用 C# 写 Windows 原生体验，这对我未来用 C 系做跨平台是一个很好的跳板（至少不用碰 Java/Kotlin）。

### 这个项目的"第一次"

| 领域 | 说明 |
|------|------|
| 🟢 **WinUI 3** | 第一次用原生 Windows UI 框架，而非 WebView/Flutter |
| 🟢 **C#** | 第一次用 C# 写完整 GUI 应用 |
| 🟢 **多进程解耦** | 第一次设计 External Controller + CLI + GUI 三层架构 |
| 🟢 **HTTP Bridge** | 第一次让前后端通过 HTTP 而非共享内存通信 |
| 🟢 **Git Submodule** | 第一次在项目中实践父子仓库管理 |
| 🟢 **端到端交付** | 第一个从零到完整可用的全栈桌面应用 |

---

## 📄 许可证

MIT License


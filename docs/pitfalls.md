# 🐛 踩坑记录

WinUI 3 + Rust HTTP Bridge 开发中遇到的实际问题及解决方案。

---

## WinUI 3 / C#

### 1. `async void` + `await` 在 UI 线程导致死锁

**现象**：点击按钮后界面卡死。

**原因**：WinUI 事件处理器运行在 UI 线程，`await` 返回后尝试回到 UI 线程时如果 UI 线程被阻塞就会死锁。

**解决方案**：`Task.Run` 把工作丢到线程池，结果通过 `DispatcherQueue.TryEnqueue` 回到 UI：

```csharp
var dq = DispatcherQueue;
await Task.Run(async () =>
{
    var result = await HeavyWork().ConfigureAwait(false);
    dq.TryEnqueue(() => TextBox.Text = result);
});
```

### 2. RPC_E_WRONG_THREAD

跨线程触碰 WinRT 控件抛 `COMException`。所有控件值必须在 `Task.Run` 前捕获为局部变量。

### 3. WMC1509 / WMC9999 XAML 编译错误

常见原因：`x:DataType` 类型不存在、命名空间缺失、XAML 中嵌入了中文弯引号 `""`（需改用 `「」` 或转义）。

### 4. REGDB_E_CLASSNOTREG

发布后在其他机器无法启动 → `.csproj` 加 `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>`。

### 5. H.NotifyIcon 图标

2.x 版本 `IconSource` 需 `ImageSource` 类型（`BitmapImage`），不能直接给路径字符串。

---

## Rust / axum

### 1. `Fn` vs `FnMut`

闭包需要修改捕获变量时，trait bound 必须用 `FnMut` 而非 `Fn`。

### 2. SSE 流式响应 + 连接追踪中间件

axum 中间件在响应头发出后即 `conn_active -= 1`，但 SSE 连接还活着。需在 handler 中手动管理计数：响应前 `+1`，流结束时 `-1`。

### 3. Windows 上 `Command` 的 Unicode 路径

Rust 1.57+ 的 `std::process::Command` 内部调用宽字符 API，可直接传中文路径，无需手动编码转换。

---

## 前后端通信

### 1. SSE `data:` 前缀截取

`"data: "` 是 6 个字符，常被误写成 `line[2..]`。

### 2. `HttpClient.Timeout` 默认 30s

长时间操作（大文件合并）不能依赖默认超时，应使用 SSE 流式连接或 `CancellationToken`。

---

## Git Submodule

```bash
git clone --recursive ...      # 克隆时拉取子模块
git submodule update --remote  # 更新子模块到最新
```

父仓库记录的是子模块的 commit hash，更新子模块后需在父仓库 `git add` 并 commit。


# 🏗️ 架构设计文档

## 为什么用 HTTP Bridge 而非 FFI / P/Invoke？

过去写的前后端项目普遍高耦合——共享内存、共享类型、一处改动牵动全局。这次刻意选择**进程级隔离**：

| 方案 | 耦合度 | 类型映射 | 调试 | 可替换性 |
|------|--------|---------|------|---------|
| P/Invoke (FFI) | 高 | 手动编组，Rust↔C# 类型桥接 | 困难 | 差 |
| HTTP Bridge ✅ | 无 | JSON，语言无关 | 可抓包 | 优 |

### 工作流

```
GUI 启动 → 分配随机端口 → spawn m3_core serve --port N
→ 等待 stdout "M3_CORE_READY" → GET /health 确认存活
→ 全部操作走 POST JSON → 长时间操作用 SSE 流式推送
→ GUI 关闭 → 后端检测闲置 10s → 自动退出
```

## 为什么用 Git Submodule？

Rust 核心引擎可独立发版、被其他项目引用，GUI 仓库通过 submodule 指针锁定兼容版本。两个仓库各自独立 CI、独立 changelog，边界清晰。


using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace MosaicMKVMuxer;

/// <summary>
/// 程序内部日志系统。同时写入文件和 VS 调试输出。
/// 日志目录: %LOCALAPPDATA%\3m_tool\logs\
/// </summary>
public static class Logger
{
    private static readonly string LogDir;
    private static readonly object _lock = new();

    static Logger()
    {
        LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "3m_tool", "logs");
        try { Directory.CreateDirectory(LogDir); }
        catch (Exception ex) { Debug.WriteLine($"[3m] Logger init failed: {ex.Message}"); }
    }

    public static string LogFilePath =>
        Path.Combine(LogDir, $"3m_tool_{DateTime.Now:yyyyMMdd}.log");

    // ── 公共方法 ────────────────────────────────────────────────

    public static void Info(string msg, [CallerMemberName] string caller = "")
        => Write("INFO", msg, caller);

    public static void Warn(string msg, [CallerMemberName] string caller = "")
        => Write("WARN", msg, caller);

    public static void Error(string msg, [CallerMemberName] string caller = "")
        => Write("ERROR", msg, caller);

    public static void Error(Exception ex, string context = "", [CallerMemberName] string caller = "")
        => Write("ERROR", $"[{context}] {ex.GetType().Name}: {ex.Message}", caller);

    // ── 底层写入 ────────────────────────────────────────────────

    private static void Write(string level, string msg, string caller)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"{timestamp} [{level}] [{caller}] {msg}";

        // 1. VS 调试输出
        Debug.WriteLine($"[3m] {line}");

        // 2. 写入日志文件
        lock (_lock)
        {
            try
            {
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[3m] Log write failed: {ex.Message}");
            }
        }
    }
}

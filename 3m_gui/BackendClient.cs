using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MosaicMKVMuxer;

public sealed partial class BackendClient : IDisposable
{
    private readonly HttpClient _http;
    private Process? _backendProcess;
    private int _port;
    private bool _disposed;
    private bool _started;
    private readonly CancellationTokenSource _shutdownCts = new();

    public bool IsRunning
    {
        get
        {
            if (!_started || _backendProcess == null) return false;
            try { _backendProcess.Refresh(); } catch { return false; }
            return !_backendProcess.HasExited;
        }
    }
    public int Port => _port;

    public async Task<bool> PingAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var resp = await _http.GetAsync(Url("/health"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (TaskCanceledException) { return false; }
        catch (HttpRequestException) { return false; }
        catch (System.Net.Sockets.SocketException) { return false; }
        catch { return false; }
    }

    // ── 数据模型 ────────────────────────────────────────────────

    public class DetectResult
    {
        [JsonPropertyName("path")] public string? Path { get; set; }
    }

    public class EpisodeInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("mp4_path")] public string Mp4Path { get; set; } = "";
        [JsonPropertyName("m4a_path")] public string? M4aPath { get; set; }
        [JsonPropertyName("json_path")] public string? JsonPath { get; set; }
        [JsonPropertyName("has_subtitle")] public bool HasSubtitle { get; set; }
        [JsonPropertyName("mkv_exists")] public bool MkvExists { get; set; }
    }

    public class ScanResult
    {
        [JsonPropertyName("episodes")] public List<EpisodeInfo> Episodes { get; set; } = [];
        [JsonPropertyName("total")] public int Total { get; set; }
    }

    public class ProgressMsg
    {
        [JsonPropertyName("type")] public string MsgType { get; set; } = "";
        [JsonPropertyName("current")] public int Current { get; set; }
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("success_count")] public int SuccessCount { get; set; }
    }

    // ── 构造 ────────────────────────────────────────────────────

    public string Version { get; } = "0.1.0";

    public BackendClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.Zero, // 禁用连接池
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        Logger.Info("BackendClient 已创建");
    }

    // ── 启动后端 ────────────────────────────────────────────────

    public async Task<bool> StartAsync(int manualPort = 0, CancellationToken ct = default)
    {
        Logger.Info("开始启动 Rust 后端...");

        _port = manualPort > 0 ? manualPort : FindFreePort();
        Logger.Info($"分配端口: {_port}");

        var exePath = FindBackendExe();
        Logger.Info($"后端 exe 路径: {exePath}");

        if (!File.Exists(exePath))
        {
            Logger.Error($"找不到后端 exe: {exePath}");
            return false;
        }

        var psi = new ProcessStartInfo(exePath, $"serve --port {_port}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            _backendProcess = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "启动后端进程失败");
            return false;
        }

        if (_backendProcess == null)
        {
            Logger.Error("Process.Start 返回 null");
            return false;
        }

        Logger.Info($"后端进程已启动 (PID: {_backendProcess.Id})");

        _ = ReadStderrAsync(_backendProcess);

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var found = false;

            while (!found)
            {
                var line = await _backendProcess.StandardOutput.ReadLineAsync(linked.Token);
                if (line == null) break;

                Logger.Info($"backend stdout: {line.Trim()}");
                if (line.Trim() == "M3_CORE_READY")
                    found = true;
            }

            if (!found)
            {
                Logger.Error("未收到 M3_CORE_READY 信号");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Error("等待后端就绪超时 (10s)");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "读取后端就绪信号失败");
            return false;
        }

        try
        {
            var healthUrl = $"http://127.0.0.1:{_port}/health";
            Logger.Info($"健康检查: {healthUrl}");
            var resp = await _http.GetAsync(healthUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Error($"健康检查返回 {resp.StatusCode}");
                return false;
            }
            Logger.Info("健康检查通过");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "健康检查失败");
            return false;
        }

        _started = true;
        Logger.Info("✅ Rust 后端启动成功");
        return true;
    }

    private static async Task ReadStderrAsync(Process proc)
    {
        try
        {
            while (!proc.HasExited)
            {
                var line = await proc.StandardError.ReadLineAsync();
                if (line == null) break;
                Logger.Warn($"[backend stderr] {line}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "读取 stderr 异常");
        }
    }

    // ── 端口 / 路径 ────────────────────────────────────────────

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindBackendExe()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "m3_core.exe");
        if (File.Exists(beside))
        {
            Logger.Info($"在输出目录找到: {beside}");
            return beside;
        }

        string[] configs = ["release", "debug"];
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8; i++)
            {
                dir = dir.Parent;
                if (dir == null) break;

                var coreDir = Path.Combine(dir.FullName, "3m_core");
                if (Directory.Exists(coreDir))
                {
                    foreach (var cfg in configs)
                    {
                        var exe = Path.Combine(coreDir, "target", cfg, "m3_core.exe");
                        if (File.Exists(exe))
                        {
                            Logger.Info($"向上搜索找到: {exe}");
                            return exe;
                        }
                    }
                    Logger.Warn($"3m_core 目录存在但未找到 exe: {coreDir}");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "向上搜索 3m_core 失败");
        }

        string[] hardcoded =
        [
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\3m_core\target\release\m3_core.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\3m_core\target\debug\m3_core.exe")),
        ];

        foreach (var hc in hardcoded)
        {
            if (File.Exists(hc))
            {
                Logger.Info($"硬编码路径找到: {hc}");
                return hc;
            }
        }

        Logger.Warn("所有路径均未找到 m3_core.exe");
        return hardcoded[0];
    }

    // ═══════════════════════════════════════════════════════════════
    //  API 方法
    // ═══════════════════════════════════════════════════════════════

    private string Url(string path)
    {
        if (_port == 0) throw new InvalidOperationException("后端端口未分配");
        return $"http://127.0.0.1:{_port}{path}";
    }

    private static ByteArrayContent EmptyBody() => new([]);

    public async Task<string?> DetectFFmpegAsync(CancellationToken ct = default)
    {
        Logger.Info("POST /api/detect/ffmpeg");
        var resp = await _http.PostAsync(Url("/api/detect/ffmpeg"), EmptyBody(), ct);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<DetectResult>(ct);
        Logger.Info($"FFmpeg 检测结果: {result?.Path ?? "(null)"}");
        return result?.Path;
    }

    public async Task<string?> DeepDetectFFmpegAsync(CancellationToken ct = default)
    {
        Logger.Info("POST /api/detect/ffmpeg-deep");
        var resp = await _http.PostAsync(Url("/api/detect/ffmpeg-deep"), EmptyBody(), ct);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<DetectResult>(ct);
        return result?.Path;
    }

    public async Task<List<EpisodeInfo>> ScanWorkspaceAsync(string dir, CancellationToken ct = default)
    {
        Logger.Info($"POST /api/scan dir={dir}");
        var resp = await _http.PostAsJsonAsync(Url("/api/scan"), new { dir }, ct);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<ScanResult>(ct);
        Logger.Info($"扫描结果: {result?.Total ?? 0} 集");
        return result?.Episodes ?? [];
    }

    public async IAsyncEnumerable<ProgressMsg> StartMuxAsync(
        string ffmpegPath, string inputDir, string outputDir,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Logger.Info($"POST /api/mux/start ffmpeg={ffmpegPath} input={inputDir} output={outputDir}");

        var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/mux/start"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { ffmpeg = ffmpegPath, input = inputDir, output = outputDir }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (IOException)
            {
                yield break; // 连接关闭，正常终止
            }

            if (line == null) yield break;
            if (!line.StartsWith("data: ")) continue;

            var json = line[6..];
            var msg = JsonSerializer.Deserialize<ProgressMsg>(json);
            if (msg != null)
            {
                Logger.Info($"Mux 进度: [{msg.MsgType}] {msg.Current}/{msg.Total} {msg.Status}");
                yield return msg;
                if (msg.MsgType is "done" or "cancelled") yield break;
            }
        }
    }

    public async Task CancelMuxAsync(CancellationToken ct = default)
    {
        try
        {
            Logger.Info("POST /api/mux/cancel");
            await _http.PostAsync(Url("/api/mux/cancel"), EmptyBody(), ct);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "CancelMuxAsync");
        }
    }

    public async Task<string?> ConvertSingleAsync(string ffmpeg, string mp4, string m4a, string? sub, string output, CancellationToken ct = default)
    {
        Logger.Info("POST /api/convert");
        var resp = await _http.PostAsJsonAsync(Url("/api/convert"), new { ffmpeg, mp4, m4a, sub, output }, ct);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return result.TryGetProperty("output", out var o) ? o.GetString() : null;
    }

    // ── 释放 ────────────────────────────────────────────────────

    private const uint PROCESS_TERMINATE = 0x0001;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Logger.Info("BackendClient 释放中...");

        _shutdownCts.Cancel();

        if (_backendProcess is { HasExited: false })
        {
            Logger.Info($"终止后端进程 (PID: {_backendProcess.Id})");
            try
            {
                IntPtr h = OpenProcess(PROCESS_TERMINATE, false, _backendProcess.Id);
                if (h == IntPtr.Zero) return;
                try { TerminateProcess(h, 0); }
                finally { CloseHandle(h); }
            }
            catch { }
        }

        // 不 Dispose HttpClient — 避免 IOCP 回调异常
        // 进程通过 Environment.Exit 终止，资源由 OS 回收
        try { _shutdownCts.Dispose(); } catch { }
        _backendProcess?.Dispose();
        Logger.Info("BackendClient 已释放");
    }
}

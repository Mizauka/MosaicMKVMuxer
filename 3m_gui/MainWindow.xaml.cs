using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace MosaicMKVMuxer;

public sealed partial class MainWindow : Window
{
    private readonly BackendClient _backend = new();
    private List<EpisodeDisplay> _episodes = [];
    private CancellationTokenSource? _muxCts;
    private nint _hwnd;

    // ── P/Invoke 窗口操作 ────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern int SendMessage(nint hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HT_CAPTION = 0x2;

    private const int SW_MINIMIZE = 6;
    private const int SW_MAXIMIZE = 3;
    private const int SW_RESTORE = 9;

    // ═══════════════════════════════════════════════════════════════
    //  构造
    // ═══════════════════════════════════════════════════════════════

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // 扩展内容到标题栏区域
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // 标题栏拖拽
        TitleBarDragRegion.PointerPressed += (_, _) =>
        {
            ReleaseCapture();
            SendMessage(_hwnd, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
        };

        StatusText.Text = "正在启动 Rust 后端...";

        Activated += OnWindowFirstActivated;
    }

    // ── 标题栏窗口控制 ──────────────────────────────────────────

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => ShowWindow(_hwnd, SW_MINIMIZE);

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
    {
        if (IsZoomed(_hwnd))
        {
            ShowWindow(_hwnd, SW_RESTORE);
            BtnMaximize.Content = "\uE922"; // □
        }
        else
        {
            ShowWindow(_hwnd, SW_MAXIMIZE);
            BtnMaximize.Content = "\uE923"; // ❐
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _backend.Dispose();
        Close();
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint hWnd);

    // ── 侧栏导航 ────────────────────────────────────────────────

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // XAML 元素可能尚未加载完毕
        if (ToolsPage == null || LogsPage == null) return;

        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            ToolsPage.Visibility = tag == "tools" ? Visibility.Visible : Visibility.Collapsed;
            LogsPage.Visibility = tag == "logs" ? Visibility.Visible : Visibility.Collapsed;

            if (tag == "logs")
                LoadLogContent();
        }
    }

    // ── 后端启动 / 自动检测 ─────────────────────────────────────

    private async void OnWindowFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnWindowFirstActivated;
        Logger.Info("窗口首次激活，开始启动后端...");

        try
        {
            var started = await _backend.StartAsync();
            if (started)
            {
                TitleBarStatus.Foreground = new SolidColorBrush(Colors.LimeGreen);
                TitleBarStatus.Text = "● 后端就绪";
                StatusText.Text = "后端已就绪 (HTTP API)";
                Logger.Info("后端启动成功");
                AutoDetect();
            }
            else
            {
                TitleBarStatus.Foreground = new SolidColorBrush(Colors.Red);
                TitleBarStatus.Text = "● 后端离线";
                StatusText.Text = "⚠ 无法启动 Rust 后端，请确认 m3_core.exe 存在";
                Logger.Error("后端启动失败");
            }
        }
        catch (Exception ex)
        {
            TitleBarStatus.Foreground = new SolidColorBrush(Colors.Red);
            TitleBarStatus.Text = "● 后端错误";
            Logger.Error(ex, "启动后端异常");
            StatusText.Text = "⚠ 后端启动失败: " + ex.Message;
        }
    }

    private void AutoDetect()
    {
        var dq = DispatcherQueue; // ⚠️ 必须在 UI 线程捕获！
        Task.Run(async () =>
        {
            try
            {
                var ffmpegTask = _backend.DetectFFmpegAsync();
                var sevenZipTask = _backend.Detect7zAsync();
                await Task.WhenAll(ffmpegTask, sevenZipTask);

                dq.TryEnqueue(() =>
                {
                    if (!string.IsNullOrEmpty(ffmpegTask.Result))
                    {
                        FFmpegPathBox.Text = ffmpegTask.Result;
                        FFmpegStatus.Text = "✅ 已自动检测到 FFmpeg";
                    }
                    if (!string.IsNullOrEmpty(sevenZipTask.Result))
                    {
                        SevenZipPathBox.Text = sevenZipTask.Result;
                        SevenZipStatus.Text = "✅ 已自动检测到 7-Zip";
                    }
                });
            }
            catch (Exception ex)
            {
                dq.TryEnqueue(() => StatusText.Text = "⚠ 自动检测出错: " + ex.Message);
            }
        });
    }

    // ── FFmpeg ────────────────────────────────────────────────────

    private async void OnBrowseFFmpeg(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe"); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file != null) { FFmpegPathBox.Text = file.Path; FFmpegStatus.Text = "✅ 已手动选择 FFmpeg"; }
    }

    private void OnDetectFFmpeg(object sender, RoutedEventArgs e)
    {
        if (!_backend.IsRunning) { StatusText.Text = "⚠ 后端未运行"; return; }
        var dq = DispatcherQueue;
        FFmpegStatus.Text = "🔍 正在检测 FFmpeg...";

        Task.Run(async () =>
        {
            try
            {
                var path = await _backend.DetectFFmpegAsync().ConfigureAwait(false);
                dq.TryEnqueue(() =>
                {
                    if (!string.IsNullOrEmpty(path))
                    { FFmpegPathBox.Text = path; FFmpegStatus.Text = "✅ 已检测到 FFmpeg"; StatusText.Text = "FFmpeg 检测成功: " + path; }
                    else { FFmpegStatus.Text = "❌ 未找到，请手动指定"; }
                });
            }
            catch (Exception ex)
            { dq.TryEnqueue(() => { FFmpegStatus.Text = "❌ 检测出错"; StatusText.Text = "⚠ " + ex.Message; }); }
        });
    }

    // ── 7-Zip ────────────────────────────────────────────────────

    private async void OnBrowse7z(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe"); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file != null) { SevenZipPathBox.Text = file.Path; SevenZipStatus.Text = "✅ 已手动选择 7-Zip"; }
    }

    private void OnDetect7z(object sender, RoutedEventArgs e)
    {
        if (!_backend.IsRunning) { StatusText.Text = "⚠ 后端未运行"; return; }
        var dq = DispatcherQueue;
        SevenZipStatus.Text = "🔍 正在检测 7-Zip...";

        Task.Run(async () =>
        {
            try
            {
                var path = await _backend.Detect7zAsync().ConfigureAwait(false);
                dq.TryEnqueue(() =>
                {
                    if (!string.IsNullOrEmpty(path))
                    { SevenZipPathBox.Text = path; SevenZipStatus.Text = "✅ 已检测到 7-Zip"; }
                    else { SevenZipStatus.Text = "❌ 未找到，请手动指定"; }
                });
            }
            catch (Exception ex)
            { dq.TryEnqueue(() => { SevenZipStatus.Text = "❌ 检测出错"; StatusText.Text = "⚠ " + ex.Message; }); }
        });
    }

    // ── 工作区 ────────────────────────────────────────────────────

    private async void OnBrowseInput(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) { InputDirBox.Text = folder.Path; StatusText.Text = "输入目录已选择: " + folder.Path; }
    }

    private async void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) { OutputDirBox.Text = folder.Path; StatusText.Text = "输出目录已选择: " + folder.Path; }
    }

    private void OnAutoOutput(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(InputDirBox.Text)) { StatusText.Text = "⚠ 请先选择输入目录"; return; }
        OutputDirBox.Text = Path.Combine(InputDirBox.Text, "MKV");
        StatusText.Text = "输出目录已自动设置";
    }

    private void OnScanWorkspace(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(InputDirBox.Text)) { StatusText.Text = "⚠ 请先选择输入目录"; return; }
        if (!_backend.IsRunning) { StatusText.Text = "⚠ 后端未运行，请等待启动完成"; return; }
        var dq = DispatcherQueue;
        var dir = InputDirBox.Text; // ⚠️ 必须在 UI 线程捕获 WinRT 控件值！
        ScanStatus.Text = "🔍 正在扫描工作区（Rust 后端）...";
        BtnStart.IsEnabled = false;

        Task.Run(async () =>
        {
            try
            {
                var episodes = await _backend.ScanWorkspaceAsync(dir).ConfigureAwait(false);
                var displayList = episodes.Select(EpisodeDisplay.FromBackend).ToList();

                dq.TryEnqueue(() =>
                {
                    _episodes = displayList;
                    if (_episodes.Count == 0)
                    { ScanStatus.Text = "未找到可合并的 MP4 文件"; BtnStart.IsEnabled = false; }
                    else
                    {
                        int hasM4a = _episodes.Count(ep => !string.IsNullOrEmpty(ep.M4aPath));
                        int hasSub = _episodes.Count(ep => ep.HasSubtitle);
                        int hasMkv = _episodes.Count(ep => ep.MkvExists);
                        ScanStatus.Text = $"找到 {_episodes.Count} 集 | M4A: {hasM4a} | 字幕: {hasSub} | 已有 MKV: {hasMkv}";
                        StatusText.Text = "扫描完成"; BtnStart.IsEnabled = true;
                        RefreshEpisodeList();
                    }
                });
            }
            catch (Exception ex)
            {
                dq.TryEnqueue(() => { ScanStatus.Text = "扫描出错"; StatusText.Text = "⚠ " + ex.Message; BtnStart.IsEnabled = false; });
            }
        });
    }

    // ── 剧集列表（纯代码生成，无 XAML 绑定）─────────────────────

    private List<CheckBox> _episodeCheckboxes = [];

    private void RefreshEpisodeList()
    {
        EpisodePanel.Children.Clear();
        _episodeCheckboxes.Clear();
        EpisodeCount.Text = $"共 {_episodes.Count} 集";

        foreach (var ep in _episodes)
        {
            var item = new Grid { Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(8, 6, 8, 6),
                Background = (Brush)Application.Current.Resources["SystemControlBackgroundChromeMediumBrush"],
                CornerRadius = new CornerRadius(4) };
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cb = new CheckBox { IsChecked = ep.Selected, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            cb.Checked += (_, _) => ep.Selected = true;
            cb.Unchecked += (_, _) => ep.Selected = false;
            Grid.SetColumn(cb, 0);
            item.Children.Add(cb);
            _episodeCheckboxes.Add(cb);

            var name = new TextBlock { Text = ep.Name, Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            Grid.SetColumn(name, 1);
            item.Children.Add(name);

            if (ep.HasSubtitle)
            {
                var badge = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 2, 4, 2),
                    Background = (Brush)Application.Current.Resources["SystemAccentColorLightBrush"] };
                badge.Child = new TextBlock { Text = "SRT", FontSize = 10,
                    Foreground = (Brush)Application.Current.Resources["SystemAccentColorBrush"] };
                Grid.SetColumn(badge, 2);
                item.Children.Add(badge);
            }

            if (ep.MkvExists)
            {
                var badge = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(4, 0, 0, 0),
                    Background = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"] };
                badge.Child = new TextBlock { Text = "MKV✓", FontSize = 10 };
                Grid.SetColumn(badge, 3);
                item.Children.Add(badge);
            }

            EpisodePanel.Children.Add(item);
        }
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var ep in _episodes) ep.Selected = true;
        foreach (var cb in _episodeCheckboxes) cb.IsChecked = true;
    }

    private void OnDeselectAll(object sender, RoutedEventArgs e)
    {
        foreach (var ep in _episodes) ep.Selected = false;
        foreach (var cb in _episodeCheckboxes) cb.IsChecked = false;
    }

    // ── 合并 ──────────────────────────────────────────────────────

    private void OnStartMux(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FFmpegPathBox.Text)) { StatusText.Text = "⚠ 请先设置 FFmpeg 路径"; return; }
        if (string.IsNullOrEmpty(InputDirBox.Text)) { StatusText.Text = "⚠ 请先选择输入目录"; return; }
        if (!_backend.IsRunning) { StatusText.Text = "⚠ 后端未运行"; return; }

        var outputPath = OutputDirBox.Text;
        if (string.IsNullOrEmpty(outputPath)) { outputPath = InputDirBox.Text; OutputDirBox.Text = outputPath; }
        var dq = DispatcherQueue;
        var ffmpeg = FFmpegPathBox.Text;   // ⚠️ 必须在 UI 线程捕获！
        var inputDir = InputDirBox.Text;   // ⚠️ 必须在 UI 线程捕获！
        BtnStart.IsEnabled = false; BtnCancel.IsEnabled = true;
        MuxProgress.Value = 0; ProgressPercent.Text = "0%";
        StatusText.Text = "正在准备合并（Rust 后端 + SSE）...";
        _muxCts = new CancellationTokenSource();
        var ct = _muxCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in _backend.StartMuxAsync(
                    ffmpeg, inputDir, outputPath, ct).ConfigureAwait(false))
                {
                    dq.TryEnqueue(() =>
                    {
                        if (msg.Total > 0)
                        { double pct = (double)msg.Current / msg.Total * 100.0; MuxProgress.Value = pct; ProgressPercent.Text = $"{(int)pct}%"; }
                        StatusText.Text = msg.Status;
                    });
                    if (msg.MsgType is "done" or "cancelled") break;
                }
                dq.TryEnqueue(() => { BtnStart.IsEnabled = true; BtnCancel.IsEnabled = false; });
            }
            catch (OperationCanceledException)
            { dq.TryEnqueue(() => { StatusText.Text = "⏹ 合并已取消"; BtnStart.IsEnabled = true; BtnCancel.IsEnabled = false; }); }
            catch (Exception ex)
            { dq.TryEnqueue(() => { StatusText.Text = "⚠ 合并出错: " + ex.Message; BtnStart.IsEnabled = true; BtnCancel.IsEnabled = false; }); }
        });
    }

    private async void OnCancelMux(object sender, RoutedEventArgs e)
    {
        _muxCts?.Cancel();
        try { await _backend.CancelMuxAsync(); } catch { }
        StatusText.Text = "⏹ 正在取消...";
        BtnStart.IsEnabled = true; BtnCancel.IsEnabled = false;
    }

    // ── 日志页面 ──────────────────────────────────────────────────

    private void LoadLogContent()
    {
        try
        {
            var path = Logger.LogFilePath;
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                LogContent.Text = string.IsNullOrWhiteSpace(text) ? "日志文件为空，请执行操作后刷新。" : text;
            }
            else
            {
                LogContent.Text = "暂无日志文件。\n\n预期路径: " + path;
            }
        }
        catch (Exception ex)
        {
            LogContent.Text = "无法读取日志: " + ex.Message;
        }
    }

    private void OnRefreshLog(object sender, RoutedEventArgs e) => LoadLogContent();
    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Logger.LogFilePath;
            if (File.Exists(path)) File.WriteAllText(path, "");
            LogContent.Text = "日志已清空。";
        }
        catch (Exception ex) { LogContent.Text = "清空失败: " + ex.Message; }
    }

    private async void OnExportLog(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("日志文件", [".log"]);
        picker.SuggestedFileName = $"3m_tool_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            ExportLogTo(file.Path);
        }
    }

    private void OnOpenLogDir(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(Logger.LogFilePath);
        if (dir == null) return;

        // 先导出到日志目录（确保文件存在）
        var exportPath = Path.Combine(dir, $"3m_tool_export_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        ExportLogTo(exportPath);

        // 打开目录
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = dir,
            UseShellExecute = true,
        });
    }

    private void ExportLogTo(string targetPath)
    {
        try
        {
            var src = Logger.LogFilePath;
            if (File.Exists(src))
                File.Copy(src, targetPath, overwrite: true);
            else
                File.WriteAllText(targetPath, "暂无日志内容。");
            Logger.Info($"日志已导出: {targetPath}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "导出日志失败");
        }
    }
}

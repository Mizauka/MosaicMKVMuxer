using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Pickers;
using WinRT.Interop;
using H.NotifyIcon;

namespace MosaicMKVMuxer;

public sealed partial class MainWindow : Window
{
    private readonly BackendClient _backend = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _healthTimer;
    private bool _backendConnected;
    private readonly List<EpisodeDisplay> _episodes = [];
    private readonly List<CheckBox> _episodeCheckboxes = [];
    private CancellationTokenSource? _muxCts;
    private readonly nint _hwnd;
    private bool _ffmpegFound;
    private readonly AppConfig _config = AppConfig.Load();
    private readonly AppWindow _appWindow = null!;

    // ── P/Invoke ──────────────────────────────────────────────────
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")] private static partial int SendMessage(nint h, int m, nint w, nint l);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ReleaseCapture();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(nint h, int n);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsZoomed(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetForegroundWindow(nint h);
    const int WM_NCLBUTTONDOWN = 0xA1, HT_CAPTION = 0x2;
    const int SW_MINIMIZE = 6, SW_MAXIMIZE = 3, SW_RESTORE = 9, SW_HIDE = 0, SW_SHOW = 5;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(IntPtr hProcess, uint uExitCode);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);
    const uint PROCESS_TERMINATE = 0x0001;

    // ── H.NotifyIcon 托盘（XAML 定义） ─────────────────────────────

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        TitleBarDragRegion.PointerPressed += (_, _) => { ReleaseCapture(); _ = SendMessage(_hwnd, WM_NCLBUTTONDOWN, HT_CAPTION, 0); };

        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _appWindow = appWindow;
        _appWindow.Closing += OnAppWindowClosing;
        _appWindow.Changed += OnAppWindowChanged;

        // 隐藏系统标题栏按钮（全透明），由自定义按钮接管
        var tb = _appWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveForegroundColor = Colors.Transparent;
        tb.ButtonHoverBackgroundColor = Colors.Transparent;
        tb.ButtonHoverForegroundColor = Colors.Transparent;
        tb.ButtonPressedBackgroundColor = Colors.Transparent;
        tb.ButtonPressedForegroundColor = Colors.Transparent;

        // 标题栏图标
        var pngPath = FindPngPath();
        if (File.Exists(pngPath))
            TitleBarIcon.Source = new BitmapImage(new Uri(pngPath));

        NavView.Loaded += (_, _) => {
            NavView.SelectedItem = NavHome;
            _currentPage = "home";
            NavView.IsBackEnabled = false;
            SwitchPage("home");
            NavLogs.Visibility = Visibility.Collapsed; // 日志选项卡默认隐藏
            ApplyConfig();
            if (_config.ShowHelperOnStartup) ShowFirstRunDialog();
        };
        Activated += OnWindowFirstActivated;
        Closed += (_, _) => _healthTimer?.Stop();
    }

    private void SetupHealthTimer()
    {
        if (_healthTimer != null) return;
        try
        {
            _healthTimer = this.DispatcherQueue.CreateTimer();
            if (_healthTimer == null)
            {
                Logger.Warn("CreateTimer 返回 null，1秒后重试");
                DispatcherQueue.TryEnqueue(() =>
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            _healthTimer = this.DispatcherQueue.CreateTimer();
                            if (_healthTimer != null)
                            {
                                _healthTimer.Interval = TimeSpan.FromSeconds(3);
                                _healthTimer.Tick += OnHealthCheckTick;
                                _healthTimer.Start();
                                Logger.Info("健康检查定时器已创建(延迟)");
                            }
                            else Logger.Error("CreateTimer 仍为 null");
                        });
                    });
                });
                return;
            }
            _healthTimer.Interval = TimeSpan.FromSeconds(3);
            _healthTimer.Tick += OnHealthCheckTick;
            Logger.Info("健康检查定时器已创建");
        }
        catch (Exception ex)
        {
            Logger.Error($"创建定时器异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>在 OnLaunched 的 Activate() 之后调用，设置窗口尺寸和启动初始化</summary>
    public void ApplyWindowSize()
    {
        Logger.Info("APPLY_WINDOW_SIZE 开始");
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var aw = AppWindow.GetFromWindowId(windowId);
            aw.Resize(new Windows.Graphics.SizeInt32(800, 800));

            var presenter = aw.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
                presenter.IsMinimizable = true;
            }

            Logger.Info($"APPLY_WINDOW_SIZE: OK {aw.Size.Width}x{aw.Size.Height}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"设置窗口尺寸失败: {ex.Message}");
        }
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        e.Cancel = true; // 拦截所有关闭请求，走自定义逻辑
        await Task.Delay(50); // 让 UI 线程先处理完
        DispatcherQueue.TryEnqueue(() => _ = HandleCloseAsync());
    }

    private bool _closing;
    private async Task HandleCloseAsync()
    {
        if (_closing) return;
        _closing = true;
        try
        {
            switch (_config.ExitBehavior)
            {
                case "tray":
                    ShowWindow(_hwnd, SW_HIDE); break;
                case "close":
                    await DoExit(); break;
                default: // ask
                    var dlg = new ContentDialog { Title = "关闭 3m_tool", Content = "请选择退出方式", PrimaryButtonText = "最小化到托盘", SecondaryButtonText = "直接退出", CloseButtonText = "取消", XamlRoot = Content.XamlRoot };
                    var r = await dlg.ShowAsync();
                    if (r == ContentDialogResult.Primary) { _config.ExitBehavior = "tray"; _config.Save(); ShowWindow(_hwnd, SW_HIDE); break; }
                    if (r == ContentDialogResult.Secondary) { await DoExit(); break; }
                    break;
            }
        }
        finally { _closing = false; }
    }

    private async Task DoExit()
    {
        try { TrayIcon?.Dispose(); } catch { }
        try { _backend.Dispose(); } catch { }
        await Task.Delay(50);
        try
        {
            foreach (var p in Process.GetProcessesByName("m3_core"))
            {
                IntPtr h = OpenProcess(PROCESS_TERMINATE, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try { TerminateProcess(h, 0); }
                finally { CloseHandle(h); }
                p.Dispose();
            }
        }
        catch (System.ComponentModel.Win32Exception) { }
        catch (InvalidOperationException) { }
        Environment.Exit(0);
    }

    // ── 托盘事件 ──────────────────────────────────────────────────
    private void OnTrayHome(object s, RoutedEventArgs e)
    { ShowWindow(_hwnd, SW_SHOW); SetForegroundWindow(_hwnd); _navHistory.Clear(); _currentPage = "home"; NavView.SelectedItem = NavHome; NavView.IsBackEnabled = false; SwitchPage("home"); }
    private void OnTrayWorkspace(object s, RoutedEventArgs e)
    { ShowWindow(_hwnd, SW_SHOW); SetForegroundWindow(_hwnd); _navHistory.Clear(); _currentPage = "workspace"; NavView.SelectedItem = NavWorkspace; NavView.IsBackEnabled = false; SwitchPage("workspace"); }
    private void OnTraySettings(object s, RoutedEventArgs e)
    { ShowWindow(_hwnd, SW_SHOW); SetForegroundWindow(_hwnd); _navHistory.Clear(); _currentPage = "settings"; NavView.SelectedItem = NavSettings; NavView.IsBackEnabled = false; SwitchPage("settings"); }
    private async void OnTrayExit(object s, RoutedEventArgs e) => await DoExit();

    // 查找图标文件（兼容打包/非打包两种运行模式）
    private static string FindIconPath()
    {
        // 1. 输出目录（非打包模式）
        var beside = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(beside)) return beside;
        // 兼容旧版 tray.ico
        var beside2 = Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
        if (File.Exists(beside2)) return beside2;
        // 2. AppX 目录（打包模式）
        var appx = Path.Combine(AppContext.BaseDirectory, "AppX", "Assets", "app.ico");
        if (File.Exists(appx)) return appx;
        var appx2 = Path.Combine(AppContext.BaseDirectory, "AppX", "Assets", "tray.ico");
        if (File.Exists(appx2)) return appx2;
        // 3. 项目源码目录
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "app.ico"));
        if (File.Exists(src)) return src;
        var src2 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "tray.ico"));
        if (File.Exists(src2)) return src2;
        // 4. 回退：用系统图标生成一个临时文件
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), "3m_tray.ico");
            if (!File.Exists(tmp))
            {
                using var ico = System.Drawing.SystemIcons.Application;
                using var fs = File.Create(tmp);
                ico.Save(fs);
            }
            return tmp;
        }
        catch { return beside; }
    }

    // 查找 PNG 图标（标题栏使用）
    private static string FindPngPath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Assets", "app.png");
        if (File.Exists(beside)) return beside;
        var appx = Path.Combine(AppContext.BaseDirectory, "AppX", "Assets", "app.png");
        if (File.Exists(appx)) return appx;
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "app.png"));
        if (File.Exists(src)) return src;
        // 回退到 icon.svg
        var svg = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.svg");
        if (File.Exists(svg)) return svg;
        var srcSvg = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "icon.svg"));
        if (File.Exists(srcSvg)) return srcSvg;
        return beside; // fallback
    }

    // ── 窗口控制 ──────────────────────────────────────────────────
    private void OnMinimizeClick(object s, RoutedEventArgs e) => ShowWindow(_hwnd, SW_MINIMIZE);
    private void OnMaximizeRestoreClick(object s, RoutedEventArgs e)
    {
        if (IsZoomed(_hwnd)) { ShowWindow(_hwnd, SW_RESTORE); BtnMaximize.Content = "\uE922"; }
        else { ShowWindow(_hwnd, SW_MAXIMIZE); BtnMaximize.Content = "\uE923"; }
    }
    private async void OnCloseClick(object s, RoutedEventArgs e) => await HandleCloseAsync();

    private void ApplyConfig()
    {
        RbExitAsk.IsChecked = _config.ExitBehavior == "ask";
        RbExitTray.IsChecked = _config.ExitBehavior == "tray";
        RbExitClose.IsChecked = _config.ExitBehavior == "close";
        TglShowHelper.IsOn = _config.ShowHelperOnStartup;
        ApplyPortConfig();
    }
    private void OnExitBehaviorChanged(object s, RoutedEventArgs e)
    {
        if (RbExitAsk.IsChecked == true) _config.ExitBehavior = "ask";
        else if (RbExitTray.IsChecked == true) _config.ExitBehavior = "tray";
        else if (RbExitClose.IsChecked == true) _config.ExitBehavior = "close";
        _config.Save();
    }

    // ── 导航切换 ──────────────────────────────────────────────────
    private string _currentPage = "home";
    private readonly Stack<string> _navHistory = new();
    private static readonly string[] PageOrder = ["home", "workspace", "helper", "logs", "settings"];

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            NavigateTo(tag);
    }

    private void OnNavBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (_navHistory.Count > 0)
        {
            var prev = _navHistory.Pop();
            _currentPage = prev;
            SwitchPage(prev, directionUp: false); // 返回=从上到下
            NavView.SelectedItem = prev switch
            {
                "home" => NavHome, "workspace" => NavWorkspace,
                "helper" => NavHelper, "logs" => NavLogs, "settings" => NavSettings, _ => null
            };
        }
        NavView.IsBackEnabled = _navHistory.Count > 0;
    }

    private void NavigateTo(string tag)
    {
        if (_currentPage != tag)
        {
            _navHistory.Push(_currentPage);
            var oldIdx = Array.IndexOf(PageOrder, _currentPage);
            var newIdx = Array.IndexOf(PageOrder, tag);
            _currentPage = tag;
            SwitchPage(tag, directionUp: newIdx > oldIdx);
        }
        else
        {
            SwitchPage(tag, directionUp: true);
        }
        NavView.IsBackEnabled = _navHistory.Count > 0;
    }

    private UIElement? GetPage(string tag) => tag switch
    {
        "home" => HomePage, "workspace" => WorkspacePage,
        "helper" => (UIElement)HelperPage, "settings" => (UIElement)SettingsPage, "logs" => LogsPage,
        _ => null
    };

    private void SwitchPage(string tag, bool directionUp = true)
    {
        var target = GetPage(tag);
        if (target == null) return;

        foreach (string t in PageOrder)
        {
            var p = GetPage(t);
            if (p != null && p != target)
                p.Visibility = Visibility.Collapsed;
        }

        if (target.Visibility != Visibility.Visible)
        {
            target.Visibility = Visibility.Visible;
            PlayEntranceAnimation(target, directionUp);
        }

        if (tag == "home") UpdateHomePage();
        if (tag == "logs") LoadLogContent();
    }

    private static void PlayEntranceAnimation(UIElement element, bool fromBottom)
    {
        element.Opacity = 0.25;
        var yOffset = fromBottom ? 250 : -250;
        element.RenderTransform = new TranslateTransform { X = 0, Y = yOffset };

        var spline = new KeySpline
        {
            ControlPoint1 = new Point(0.1, 0.9),
            ControlPoint2 = new Point(0.2, 1.0)
        };
        var dur = TimeSpan.FromMilliseconds(375);

        var fadeAnim = new DoubleAnimationUsingKeyFrames();
        fadeAnim.KeyFrames.Add(new SplineDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(dur), Value = 1, KeySpline = spline });
        Storyboard.SetTarget(fadeAnim, element);
        Storyboard.SetTargetProperty(fadeAnim, "Opacity");

        var spline2 = new KeySpline
        {
            ControlPoint1 = new Point(0.1, 0.9),
            ControlPoint2 = new Point(0.2, 1.0)
        };
        var slideAnim = new DoubleAnimationUsingKeyFrames();
        slideAnim.KeyFrames.Add(new SplineDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(dur), Value = 0, KeySpline = spline2 });
        Storyboard.SetTarget(slideAnim, element);
        Storyboard.SetTargetProperty(slideAnim, "(UIElement.RenderTransform).(TranslateTransform.Y)");

        var sb = new Storyboard();
        sb.Children.Add(fadeAnim);
        sb.Children.Add(slideAnim);
        try { sb.Begin(); }
        catch (Exception ex) { Logger.Warn($"动画失败: {ex.Message}"); element.Opacity = 1; }
    }

    private void OnGoSettings(object s, RoutedEventArgs e) { _navHistory.Clear(); _currentPage = "settings"; NavView.SelectedItem = NavSettings; SwitchPage("settings"); NavView.IsBackEnabled = false; }

    private async void ShowFirstRunDialog()
    {
        var dialog = new ContentDialog
        {
            Title = "🎬 欢迎使用 Mosaic MKV Muxer",
            CloseButtonText = "确认",
            PrimaryButtonText = "不再展示",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
            Content = new TextBlock
            {
                Text = "① 确保已安装 FFmpeg（设置页可自动检测）\n" +
                       "② 选择包含 MP4 + M4A 的文件夹作为工作区\n" +
                       "③ 扫描后勾选剧集，点击「开始合并」即可\n" +
                       "④ 更多帮助请查看「帮助」页面",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
            },
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _config.ShowHelperOnStartup = false;
            _config.Save();
            TglShowHelper.IsOn = false;
        }
    }

    private void OnShowLogsToggled(object s, RoutedEventArgs e)
    {
        NavLogs.Visibility = TglShowLogs.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnShowHelperToggled(object s, RoutedEventArgs e)
    {
        _config.ShowHelperOnStartup = TglShowHelper.IsOn;
        _config.Save();
    }

    private async void OnQuickOpenWorkspace(object s, RoutedEventArgs e)
    {
        var f = await PickFolder();
        if (f != null) { InputDirBox.Text = f; OutputDirBox.Text = Path.Combine(f, "MKV"); _navHistory.Clear(); _currentPage = "workspace"; NavView.SelectedItem = NavWorkspace; NavView.IsBackEnabled = false; SwitchPage("workspace"); }
    }

    // ── 后端启动 ──────────────────────────────────────────────────
    private async void OnWindowFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnWindowFirstActivated;
        Logger.Info("启动后端...");
        await TryStartBackend();
    }

    private async Task TryStartBackend()
    {
        try
        {
            var port = _config.PortManual && _config.PortValue > 0 ? _config.PortValue : 0;
            var ok = await _backend.StartAsync(port);
            if (ok)
            {
                SetBackendConnected(true);
                BackendPortText.Text = $":{_backend.Port}";
                StatusIcon.Glyph = "\uE73E";
                StatusText.Text = "就绪";
                BtnReconnectBackend.Visibility = Visibility.Collapsed;
                var iconPath = FindIconPath();
                if (File.Exists(iconPath))
                    TrayIcon.Icon = new System.Drawing.Icon(iconPath);
                TrayIcon.LeftClickCommand = new SimpleCommand(() =>
                {
                    ShowWindow(_hwnd, SW_SHOW);
                    SetForegroundWindow(_hwnd);
                });
                SetupHealthTimer();
                _healthTimer?.Start();
                AutoDetect();
            }
            else
            {
                SetBackendConnected(false);
                BackendPortText.Text = "";
                StatusIcon.Glyph = "\uE783";
                StatusText.Text = "未启动";
                BtnReconnectBackend.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            SetBackendConnected(false);
            BackendStatusLabel.Text = "异常";
            StatusIcon.Glyph = "\uE783";
            StatusText.Text = ex.Message;
            BtnReconnectBackend.Visibility = Visibility.Visible;
        }
    }

    private void SetBackendConnected(bool connected)
    {
        _backendConnected = connected;
        TitleBarStatus.Foreground = new SolidColorBrush(connected ? Colors.LimeGreen : Colors.Red);
        BackendStatusDot.Background = new SolidColorBrush(connected ? Colors.LimeGreen : Colors.Red);
        BackendStatusLabel.Text = "后端";
    }

    private bool EnsureBackend(string action)
    {
        if (!_backendConnected)
        {
            StatusIcon.Glyph = "\uE783";
            StatusText.Text = $"后端未连接，无法{action}";
            Logger.Warn($"操作被阻止 ({action}): 后端未连接");
            return false;
        }
        return true;
    }

    private int _healthFailCount;
    private async void OnHealthCheckTick(object? s, object? e)
    {
        if (!_backendConnected) return;
        try
        {
            var ok = await _backend.PingAsync().ConfigureAwait(true);
            if (ok)
            {
                _healthFailCount = 0;
            }
            else
            {
                _healthFailCount++;
                if (_healthFailCount >= 2)
                    DispatcherQueue.TryEnqueue(HandleBackendExit);
            }
        }
        catch
        {
            _healthFailCount++;
            if (_healthFailCount >= 2)
                DispatcherQueue.TryEnqueue(HandleBackendExit);
        }
    }

    private async void HandleBackendExit()
    {
        if (!_backendConnected) return;
        _backendConnected = false;
        _healthTimer?.Stop();
        SetBackendConnected(false);
        BackendPortText.Text = "";
        StatusIcon.Glyph = "\uE783";
        StatusText.Text = "后端连接断开";
        BtnReconnectBackend.Visibility = Visibility.Visible;
        Logger.Warn("后端连接断开");

        try
        {
            var dlg = new ContentDialog
            {
                Title = "后端断开",
                Content = "后端连接已断开，请点击重连按钮恢复。",
                CloseButtonText = "知道了",
                XamlRoot = Content.XamlRoot
            };
            await dlg.ShowAsync();
        }
        catch { }
    }

    private async void OnReconnectBackend(object s, RoutedEventArgs e)
    {
        BtnReconnectBackend.IsEnabled = false;
        StatusIcon.Glyph = "\uE72C";
        StatusText.Text = "正在重连...";
        await TryStartBackend();
        BtnReconnectBackend.IsEnabled = true;
    }

    private void ApplyPortConfig()
    {
        if (RbPortRandom == null || RbPortManual == null) return;
        RbPortRandom.IsChecked = !_config.PortManual;
        RbPortManual.IsChecked = _config.PortManual;
        ManualPortBox.Visibility = _config.PortManual ? Visibility.Visible : Visibility.Collapsed;
        if (_config.PortValue > 0) ManualPortBox.Text = _config.PortValue.ToString();
    }

    private void OnPortModeChanged(object s, RoutedEventArgs e)
    {
        if (RbPortManual == null || ManualPortBox == null) return;
        _config.PortManual = RbPortManual.IsChecked == true;
        ManualPortBox.Visibility = _config.PortManual ? Visibility.Visible : Visibility.Collapsed;
        SavePortConfig();
    }

    private void OnManualPortTextChanged(object s, TextChangedEventArgs e)
    {
        SavePortConfig();
    }

    private void SavePortConfig()
    {
        if (int.TryParse(ManualPortBox.Text, out var v) && v is >= 1024 and <= 65535)
            _config.PortValue = v;
        _config.Save();
    }

    // ── 窗口尺寸设置 ────────────────────────────────────────────

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange) return;
        var sz = sender.Size;
        if (sz.Width < 780 || sz.Height < 780)
        {
            var newW = Math.Max(sz.Width, 780);
            var newH = Math.Max(sz.Height, 780);
            sender.Resize(new Windows.Graphics.SizeInt32(newW, newH));
        }
    }

    // ── 自动检测 ──────────────────────────────────────────────────
    private void AutoDetect()
    {
        var dq = DispatcherQueue;
        _ = Task.Run(async () =>
        {
            try
            {
                var p = await _backend.DetectFFmpegAsync();
                dq.TryEnqueue(() =>
                {
                    if (!string.IsNullOrEmpty(p)) { FFmpegPathBox.Text = p; FFmpegStatus.Text = "✅ " + p; _ffmpegFound = true; BtnDeepDetectFFmpeg.Visibility = BtnInstallFFmpeg.Visibility = Visibility.Collapsed; }
                    else { BtnDeepDetectFFmpeg.Visibility = BtnInstallFFmpeg.Visibility = Visibility.Visible; }
                    UpdateHomePage();
                });
            }
            catch { }
        });
    }

    // ── 首页逻辑 ──────────────────────────────────────────────────
    private void UpdateHomePage()
    {
        if (_ffmpegFound)
        {
            HomeStatus.Text = $"✅ FFmpeg: {FFmpegPathBox.Text}";
            HomeActions.Visibility = Visibility.Visible;
            BtnGoSettingsHint.Visibility = Visibility.Collapsed;
        }
        else
        {
            HomeStatus.Text = "❌ 未检测到 FFmpeg\n请前往设置页配置或安装";
            HomeActions.Visibility = Visibility.Collapsed;
            BtnGoSettingsHint.Visibility = Visibility.Visible;
        }
    }

    // ── 设置页：检测按钮 ──────────────────────────────────────────

    private async void OnDetectFFmpeg(object s, RoutedEventArgs e)
    {
        if (!EnsureBackend("检测 FFmpeg")) return;
        FFmpegStatus.Text = "🔍 检测中...";
        var dq = DispatcherQueue;
        _ = Task.Run(async () =>
        {
            try
            {
                var p = await _backend.DetectFFmpegAsync().ConfigureAwait(false);
                dq.TryEnqueue(() =>
                {
                    if (!string.IsNullOrEmpty(p)) { FFmpegPathBox.Text = p; FFmpegStatus.Text = $"✅ {p}"; _ffmpegFound = true; BtnDeepDetectFFmpeg.Visibility = BtnInstallFFmpeg.Visibility = Visibility.Collapsed; }
                    else { FFmpegStatus.Text = "❌ 未找到"; BtnDeepDetectFFmpeg.Visibility = BtnInstallFFmpeg.Visibility = Visibility.Visible; }
                    UpdateHomePage();
                });
            }
            catch (Exception ex) { dq.TryEnqueue(() => FFmpegStatus.Text = "❌ " + ex.Message); }
        });
    }

    private async void OnDeepDetectFFmpeg(object s, RoutedEventArgs e)
    {
        if (!EnsureBackend("深层搜索 FFmpeg")) return;
        FFmpegStatus.Text = "🔎 深层搜索(7层)...";
        var dq = DispatcherQueue;
        _ = Task.Run(async () =>
        {
            try
            {
                var p = await _backend.DeepDetectFFmpegAsync().ConfigureAwait(false);
                dq.TryEnqueue(() =>
                {
                    if (!string.IsNullOrEmpty(p)) { FFmpegPathBox.Text = p; FFmpegStatus.Text = $"✅ {p}"; _ffmpegFound = true; BtnDeepDetectFFmpeg.Visibility = BtnInstallFFmpeg.Visibility = Visibility.Collapsed; }
                    else { FFmpegStatus.Text = "❌ 深层未找到"; }
                });
            }
            catch (Exception ex) { dq.TryEnqueue(() => FFmpegStatus.Text = "❌ " + ex.Message); }
        });
    }

    private void OnInstallFFmpeg(object s, RoutedEventArgs e) => InstallWinget("Gyan.FFmpeg");
    private static void InstallWinget(string id) { Process.Start(new ProcessStartInfo("cmd.exe", $"/k winget install {id} --accept-package-agreements") { UseShellExecute = true }); }

    private async void OnBrowseFFmpeg(object s, RoutedEventArgs e) { var f = await PickFile(); if (f != null) { FFmpegPathBox.Text = f; _ffmpegFound = true; UpdateHomePage(); } }

    private async Task<string?> PickFile()
    {
        var p = new FileOpenPicker(); p.FileTypeFilter.Add(".exe"); p.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(p, _hwnd);
        var f = await p.PickSingleFileAsync();
        return f?.Path;
    }

    // ── 工作区 ────────────────────────────────────────────────────
    private async void OnBrowseInput(object s, RoutedEventArgs e) { var f = await PickFolder(); if (f != null) InputDirBox.Text = f; }
    private async void OnBrowseOutput(object s, RoutedEventArgs e) { var f = await PickFolder(); if (f != null) OutputDirBox.Text = f; }
    private void OnAutoOutput(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(InputDirBox.Text)) { StatusText.Text = "⚠ 请先选输入目录"; return; }
        OutputDirBox.Text = Path.Combine(InputDirBox.Text, "MKV");
    }

    private async Task<string?> PickFolder()
    {
        var p = new FolderPicker(); p.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(p, _hwnd);
        var f = await p.PickSingleFolderAsync();
        return f?.Path;
    }

    private void OnScanWorkspace(object s, RoutedEventArgs e)
    {
        if (!EnsureBackend("扫描工作区")) return;
        if (string.IsNullOrEmpty(InputDirBox.Text)) { StatusText.Text = "⚠ 请先选输入目录"; return; }
        var dq = DispatcherQueue; var dir = InputDirBox.Text;
        ScanStatus.Text = "🔍 扫描中..."; BtnStart.IsEnabled = false;
        Task.Run(async () =>
        {
            try
            {
                var eps = await _backend.ScanWorkspaceAsync(dir).ConfigureAwait(false);
                var list = eps.Select(EpisodeDisplay.FromBackend).ToList();
                dq.TryEnqueue(() =>
                {
                    _episodes.Clear(); _episodes.AddRange(list);
                    if (list.Count == 0) { ScanStatus.Text = "未找到 MP4"; }
                    else { ScanStatus.Text = $"{list.Count} 集 | M4A:{list.Count(x=>!string.IsNullOrEmpty(x.M4aPath))} 字幕:{list.Count(x=>x.HasSubtitle)} MKV:{list.Count(x=>x.MkvExists)}"; BtnStart.IsEnabled = true; RefreshEpisodeList(); }
                });
            }
            catch (Exception ex) { dq.TryEnqueue(() => { ScanStatus.Text = "扫描出错"; StatusText.Text = "⚠ " + ex.Message; }); }
        });
    }

    // ── 剧集列表 ──────────────────────────────────────────────────
    private void RefreshEpisodeList()
    {
        EpisodePanel.Children.Clear(); _episodeCheckboxes.Clear();
        EpisodeCount.Text = $"共 {_episodes.Count} 集";
        foreach (var ep in _episodes)
        {
            var item = new Grid { Margin = new Thickness(0, 1, 0, 1), Padding = new Thickness(8, 4, 8, 4),
                Background = (Brush)Application.Current.Resources["SystemControlBackgroundChromeMediumBrush"], CornerRadius = new CornerRadius(4) };
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cb = new CheckBox { IsChecked = ep.Selected, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            cb.Checked += (_, _) => ep.Selected = true; cb.Unchecked += (_, _) => ep.Selected = false;
            Grid.SetColumn(cb, 0); item.Children.Add(cb); _episodeCheckboxes.Add(cb);
            var n = new TextBlock { Text = ep.Name, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            Grid.SetColumn(n, 1); item.Children.Add(n);
            if (ep.HasSubtitle) { var b = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 2, 4, 2), Background = (Brush)Application.Current.Resources["SystemAccentColorLightBrush"], Child = new TextBlock { Text = "SRT", FontSize = 10, Foreground = (Brush)Application.Current.Resources["SystemAccentColorBrush"] } }; Grid.SetColumn(b, 2); item.Children.Add(b); }
            if (ep.MkvExists) { var b = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(4, 0, 0, 0), Background = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"], Child = new TextBlock { Text = "MKV✓", FontSize = 10 } }; Grid.SetColumn(b, 3); item.Children.Add(b); }
            EpisodePanel.Children.Add(item);
        }
    }
    private void OnSelectAll(object s, RoutedEventArgs e) { foreach (var ep in _episodes) ep.Selected = true; foreach (var cb in _episodeCheckboxes) cb.IsChecked = true; }
    private void OnDeselectAll(object s, RoutedEventArgs e) { foreach (var ep in _episodes) ep.Selected = false; foreach (var cb in _episodeCheckboxes) cb.IsChecked = false; }

    // ── 合并 ──────────────────────────────────────────────────────
    private DateTime _muxStart; private string _muxCur = "";
    private void MuxLogLine(string text, string color)
    {
        var c = color switch { "green" => new SolidColorBrush(Colors.LimeGreen), "red" => new SolidColorBrush(Colors.Red), _ => new SolidColorBrush(Colors.Gray) };
        var p = new Paragraph { Margin = new Thickness(0), Foreground = c };
        p.Inlines.Add(new Run { Text = text }); MuxLog.Blocks.Add(p);
    }

    private void OnStartMux(object s, RoutedEventArgs e)
    {
        if (!EnsureBackend("开始合并")) return;
        if (string.IsNullOrEmpty(FFmpegPathBox.Text)) { StatusText.Text = "⚠ 请设置 FFmpeg"; return; }
        if (string.IsNullOrEmpty(InputDirBox.Text)) { StatusText.Text = "⚠ 请选输入目录"; return; }
        var outPath = OutputDirBox.Text; if (string.IsNullOrEmpty(outPath)) { outPath = InputDirBox.Text; OutputDirBox.Text = outPath; }
        var dq = DispatcherQueue; var ff = FFmpegPathBox.Text; var inp = InputDirBox.Text;
        BtnStart.IsEnabled = false; BtnCancel.IsEnabled = true;
        MuxProgress.Value = 0; ProgressPercent.Text = "0%"; MuxLog.Blocks.Clear();
        _muxStart = DateTime.Now; _muxCur = "";
        _muxCts = new CancellationTokenSource(); var ct = _muxCts.Token;
        StatusText.Text = "合并中...";

        Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in _backend.StartMuxAsync(ff, inp, outPath, ct).ConfigureAwait(false))
                {
                    var now = DateTime.Now;
                    dq.TryEnqueue(() =>
                    {
                        if (msg.Total > 0) { MuxProgress.Value = (double)msg.Current / msg.Total * 100; ProgressPercent.Text = $"{(int)((double)msg.Current/msg.Total*100)}%"; }
                        if (msg.MsgType == "progress" && msg.Status.StartsWith("正在合并:")) { _muxCur = msg.Status.Replace("正在合并:", "").Split('(')[0].Trim(); _muxStart = now; MuxLogLine($"[..] 合成 {msg.Current+1}/{msg.Total} \"{_muxCur}\" 中...", "gray"); }
                        else if (msg.MsgType == "progress" && msg.Status.Contains("OK")) { var el = (now - _muxStart).TotalSeconds; MuxLogLine($"[OK] 第{msg.Current}项合成成功 \"{_muxCur}\" {el:F1}s", "green"); }
                        else if (msg.MsgType == "error") { var el = (now - _muxStart).TotalSeconds; MuxLogLine($"[FAIL] 第{msg.Current+1}项合成失败 \"{_muxCur}\" {el:F1}s | {msg.Status}", "red"); }
                        else if (msg.MsgType == "done") { MuxLogLine($"[DONE] 完成 {msg.SuccessCount}/{msg.Total} 集", "green"); StatusText.Text = $"完成 {msg.SuccessCount}/{msg.Total} 集"; StatusIcon.Glyph = "\uE73E"; }
                        else if (msg.MsgType == "cancelled") MuxLogLine("[STOP] 已取消", "gray");
                    });
                    if (msg.MsgType is "done" or "cancelled")
                    {
                        break;
                    }
                }
                dq.TryEnqueue(() => { BtnStart.IsEnabled = true; BtnCancel.IsEnabled = false; });
            }
            catch (OperationCanceledException) { dq.TryEnqueue(() => { MuxLogLine("⏹ 已取消", "gray"); BtnStart.IsEnabled = true; BtnCancel.IsEnabled = false; }); }
            catch (Exception ex) { dq.TryEnqueue(() => { MuxLogLine("❌ " + ex.Message, "red"); BtnStart.IsEnabled = true; BtnCancel.IsEnabled = false; }); }
        });
    }

    private async void OnCancelMux(object s, RoutedEventArgs e) { _muxCts?.Cancel(); try { await _backend.CancelMuxAsync(); } catch { } BtnStart.IsEnabled = true; BtnCancel.IsEnabled = false; }

    // ── 转换页（单文件） ──────────────────────────────────────────

    private async void OnPickConvertMp4(object s, RoutedEventArgs e) { var f = await PickFile(); if (f != null) { ConvertMp4Box.Text = f; AutoConvertOut(); } }
    private async void OnPickConvertM4a(object s, RoutedEventArgs e) { var f = await PickFile(); if (f != null) ConvertM4aBox.Text = f; }
    private async void OnPickConvertJson(object s, RoutedEventArgs e) { var f = await PickJsonFile(); if (f != null) ConvertJsonBox.Text = f; }
    private async void OnPickConvertOut(object s, RoutedEventArgs e) { var p = new FileSavePicker(); p.FileTypeChoices.Add("MKV", [".mkv"]); p.SuggestedFileName = "output.mkv"; WinRT.Interop.InitializeWithWindow.Initialize(p, _hwnd); var f = await p.PickSaveFileAsync(); if (f != null) ConvertOutBox.Text = f.Path; }

    private async Task<string?> PickJsonFile()
    {
        var p = new FileOpenPicker(); p.FileTypeFilter.Add(".json"); p.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(p, _hwnd);
        var f = await p.PickSingleFileAsync(); return f?.Path;
    }

    private void AutoConvertOut()
    {
        if (!string.IsNullOrEmpty(ConvertMp4Box.Text) && string.IsNullOrEmpty(ConvertOutBox.Text))
            ConvertOutBox.Text = Path.Combine(Path.GetDirectoryName(ConvertMp4Box.Text)!, Path.GetFileNameWithoutExtension(ConvertMp4Box.Text) + ".mkv");
    }

    private void OnConvertStart(object s, RoutedEventArgs e)
    {
        if (!EnsureBackend("快速转换")) return;
        if (string.IsNullOrEmpty(ConvertMp4Box.Text)) { ConvertStatus.Text = "⚠ 请选择 MP4"; return; }
        if (string.IsNullOrEmpty(ConvertM4aBox.Text)) { ConvertStatus.Text = "⚠ 请选择 M4A"; return; }
        if (string.IsNullOrEmpty(FFmpegPathBox.Text)) { ConvertStatus.Text = "⚠ 请先在设置页配置 FFmpeg"; return; }

        var dq = DispatcherQueue; var ff = FFmpegPathBox.Text; var mp4 = ConvertMp4Box.Text; var m4a = ConvertM4aBox.Text;
        var json = ConvertJsonBox.Text; var output = ConvertOutBox.Text;
        if (string.IsNullOrEmpty(output)) output = Path.Combine(Path.GetDirectoryName(mp4)!, Path.GetFileNameWithoutExtension(mp4) + ".mkv");

        BtnConvertStart.IsEnabled = false; ConvertStatus.Text = "⏳ 转换中...";
        var st = DateTime.Now;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _backend.ConvertSingleAsync(ff, mp4, m4a, string.IsNullOrEmpty(json) ? null : json, output).ConfigureAwait(false);
                var el = (DateTime.Now - st).TotalSeconds;
                dq.TryEnqueue(() => { ConvertStatus.Text = $"✅ 转换成功！{el:F1}s\n{result}"; BtnConvertStart.IsEnabled = true; });
            }
            catch (Exception ex) { dq.TryEnqueue(() => { ConvertStatus.Text = "❌ " + ex.Message; BtnConvertStart.IsEnabled = true; }); }
        });
    }

    // ── 日志页 ────────────────────────────────────────────────────
    private void LoadLogContent()
    {
        try
        {
            LogContent.Blocks.Clear();
            var path = Logger.LogFilePath;
            if (!File.Exists(path))
            {
                LogContent.Blocks.Add(CreateLogParagraph("无日志: " + path, "WARN"));
                return;
            }
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                LogContent.Blocks.Add(CreateLogParagraph("日志为空", "INFO"));
                return;
            }
            foreach (var line in text.Split('\n', StringSplitOptions.None))
            {
                var trimmed = line.TrimEnd('\r', '\n');
                if (string.IsNullOrEmpty(trimmed)) continue;
                LogContent.Blocks.Add(CreateLogParagraph(trimmed, ParseLogLevel(trimmed)));
            }
        }
        catch (Exception ex)
        {
            LogContent.Blocks.Clear();
            LogContent.Blocks.Add(CreateLogParagraph(ex.Message, "ERROR"));
        }
    }

    private static string ParseLogLevel(string line)
    {
        // 格式: HH:mm:ss.fff [LEVEL] ...
        var bracketIdx = line.IndexOf('[');
        if (bracketIdx < 0) return "INFO";
        var endBracketIdx = line.IndexOf(']', bracketIdx);
        if (endBracketIdx < 0) return "INFO";
        return line[(bracketIdx + 1)..endBracketIdx];
    }

    private static Paragraph CreateLogParagraph(string text, string level)
    {
        var color = level switch
        {
            "ERROR" => Microsoft.UI.Colors.Red,
            "WARN" => Microsoft.UI.Colors.Orange,
            _ => Microsoft.UI.Colors.Gray
        };
        return new Paragraph
        {
            Margin = new Thickness(0),
            Foreground = new SolidColorBrush(color),
            Inlines = { new Run { Text = text } }
        };
    }

    private void OnRefreshLog(object s, RoutedEventArgs e) => LoadLogContent();
    private void OnClearLog(object s, RoutedEventArgs e)
    {
        try
        {
            File.WriteAllText(Logger.LogFilePath, "");
            LogContent.Blocks.Clear();
            LogContent.Blocks.Add(CreateLogParagraph("已清空", "INFO"));
        }
        catch (Exception ex)
        {
            LogContent.Blocks.Clear();
            LogContent.Blocks.Add(CreateLogParagraph(ex.Message, "ERROR"));
        }
    }
    private async void OnExportLog(object s, RoutedEventArgs e)
    {
        var p = new FileSavePicker(); p.FileTypeChoices.Add("日志", [".log"]); p.SuggestedFileName = $"3m_tool_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        WinRT.Interop.InitializeWithWindow.Initialize(p, _hwnd); var f = await p.PickSaveFileAsync();
        if (f != null) { try { File.Copy(Logger.LogFilePath, f.Path, true); } catch { try { File.WriteAllText(f.Path, File.ReadAllText(Logger.LogFilePath)); } catch { } } }
    }
    private void OnOpenLogDir(object s, RoutedEventArgs e) { var d = Path.GetDirectoryName(Logger.LogFilePath); if (Directory.Exists(d)) Process.Start("explorer.exe", d); }
}

// ── 简易 ICommand，用于托盘左键等 ────────────────────────────────
internal sealed partial class SimpleCommand(Action action) : System.Windows.Input.ICommand
{
#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => action();
}

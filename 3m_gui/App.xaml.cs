using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;

namespace MosaicMKVMuxer;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
#if DEBUG
        AttachParentConsole();
#endif
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
        _window.ApplyWindowSize();
    }

#if DEBUG
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    private static void AttachParentConsole()
    {
        AttachConsole(ATTACH_PARENT_PROCESS);
        Console.WriteLine("=== 3m_tool Debug ===");
        Console.WriteLine($"PID: {Environment.ProcessId}");
        Console.WriteLine("=====================");
    }
#endif
}

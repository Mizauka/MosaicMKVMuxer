using System;
using System.IO;
using System.Text.Json;

namespace MosaicMKVMuxer;

/// <summary>应用配置（JSON 持久化到 %APPDATA%\3m_tool\config.json）</summary>
public class AppConfig
{
    public string ExitBehavior { get; set; } = "ask";
    public bool PortManual { get; set; }
    public int PortValue { get; set; }
    public bool ShowHelperOnStartup { get; set; } = true;

    private static readonly JsonSerializerOptions _saveOptions = new() { WriteIndented = true };

    private static string Path =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "3m_tool", "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, _saveOptions));
        }
        catch { }
    }
}

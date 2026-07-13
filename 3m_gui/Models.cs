using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MosaicMKVMuxer;

// ── 数据绑定模型 ──────────────────────────────────────────────────

public class EpisodeDisplay : INotifyPropertyChanged
{
    private bool _selected = true;
    public string Name { get; set; } = "";
    public string Mp4Path { get; set; } = "";
    public string? M4aPath { get; set; }
    public bool HasSubtitle { get; set; }
    public bool MkvExists { get; set; }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static EpisodeDisplay FromBackend(BackendClient.EpisodeInfo ep) => new()
    {
        Name = ep.Name,
        Mp4Path = ep.Mp4Path,
        M4aPath = ep.M4aPath,
        HasSubtitle = ep.HasSubtitle,
        MkvExists = ep.MkvExists,
        Selected = true,
    };
}

// ── Bool→Visibility 转换器 ───────────────────────────────────────

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is true) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

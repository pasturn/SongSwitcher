using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SongSwitcher.Models;

/// <summary>一首导入的歌曲。</summary>
public class Song : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _filePath = string.Empty;
    private string _hotkey = string.Empty;
    private string? _durationText;
    private bool _isDragPlaceholder;

    /// <summary>曲目标题，默认取文件名。</summary>
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnPropertyChanged(); } }
    }

    /// <summary>音频文件的绝对路径。</summary>
    public string FilePath
    {
        get => _filePath;
        set { if (_filePath != value) { _filePath = value; OnPropertyChanged(); } }
    }

    /// <summary>全局快捷键，如 "Ctrl+Alt+1" 或单键 "Q"。空字符串表示未设置。</summary>
    public string Hotkey
    {
        get => _hotkey;
        set
        {
            if (_hotkey == value) return;
            _hotkey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSingleKeyHotkey));
        }
    }

    /// <summary>读取出来的时长文本，形如 03:25。</summary>
    [JsonIgnore]
    public string? DurationText
    {
        get => _durationText;
        set { if (_durationText != value) { _durationText = value; OnPropertyChanged(); } }
    }

    [JsonIgnore]
    public string FileName => Path.GetFileName(FilePath);

    [JsonIgnore]
    public bool Exists => File.Exists(FilePath);

    /// <summary>是否为不带修饰键的单键快捷键（界面据此高亮提示）。</summary>
    [JsonIgnore]
    public bool IsSingleKeyHotkey => Services.HotkeyFormatter.IsSingleKey(Hotkey);

    /// <summary>
    /// 拖拽排序中该歌曲是否正被拖着。界面据此把卡片显示成虚线空槽，
    /// 表示"这里会放下来"。
    /// </summary>
    [JsonIgnore]
    public bool IsDragPlaceholder
    {
        get => _isDragPlaceholder;
        set { if (_isDragPlaceholder != value) { _isDragPlaceholder = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
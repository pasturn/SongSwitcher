using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SongSwitcher.Models;
using SongSwitcher.Services;

namespace SongSwitcher.ViewModels;

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public class MainViewModel : INotifyPropertyChanged
{
    private readonly LibraryService _library = new();
    private readonly AudioPlayerService _player = new();
    private readonly GlobalHotkeyService _hotkeys = new();
    private readonly SettingsService _settings = new();
    private readonly Dictionary<Song, int> _hotkeyIds = new();

    // 均在构造函数里经 _settings 读取（不再各自 new 一个 SettingsService，
    // 避免同一份磁盘设置被读三遍、以及多实例缓存可能分叉的隐患）
    private bool _useGlobalSpaceHotkey;

    private Song? _selectedSong;
    private Song? _capturingSong;
    private string _statusText = "就绪";
    private string _nowPlayingText = "未播放";
    private bool _isPlaying;
    private bool _isCapturingHotkey;

    // 同上：构造函数里经 _settings 读取
    private float _volume;
    private IReadOnlyList<AudioOutputDevice> _outputDevices = Array.Empty<AudioOutputDevice>();
    private AudioOutputDevice? _selectedOutputDevice;
    private bool _isRefreshingDevices;

    public MainViewModel()
    {
        // 先读设置（音量、空格开关），后续构造流程依赖这些值
        _volume = _settings.LoadVolume();
        _useGlobalSpaceHotkey = _settings.LoadSpaceGlobalPlayPause();

        Songs = new ObservableCollection<Song>(_library.Load());

        ImportCommand = new RelayCommand(_ => Import());
        RemoveCommand = new RelayCommand(p => Remove(p as Song),
                                        p => (p as Song ?? SelectedSong) is not null);
        ToggleCommand = new RelayCommand(p => Toggle(p as Song ?? SelectedSong),
                                        p => (p as Song ?? SelectedSong) is not null);
        StartCaptureCommand = new RelayCommand(p => StartCapture(p as Song ?? SelectedSong),
                                        p => (p as Song ?? SelectedSong) is not null);
        ClearHotkeyCommand = new RelayCommand(p => ClearHotkey(p as Song ?? SelectedSong),
                                        p => (p as Song ?? SelectedSong) is not null);
        PlayPauseCommand = new RelayCommand(_ => Toggle(SelectedSong ?? FirstSong()), _ => Songs.Count > 0);
        SaveCommand = new RelayCommand(_ => Save());
        RemoveAllCommand = new RelayCommand(_ => RemoveAll(), _ => Songs.Count > 0);
        MoveEarlierCommand = new RelayCommand(_ => ShiftSelected(-1), _ => Songs.Count > 1);
        MoveLaterCommand = new RelayCommand(_ => ShiftSelected(+1), _ => Songs.Count > 1);
        MoveToStartCommand = new RelayCommand(_ => MoveToEdge(true), _ => Songs.Count > 1);
        MoveToEndCommand = new RelayCommand(_ => MoveToEdge(false), _ => Songs.Count > 1);
        RefreshDevicesCommand = new RelayCommand(_ => RefreshOutputDevices());
        ToggleMuteCommand = new RelayCommand(_ => IsMuted = !IsMuted);
        VolumeUpCommand = new RelayCommand(_ => AdjustVolume(0.05f));
        VolumeDownCommand = new RelayCommand(_ => AdjustVolume(-0.05f));
        TogglePlayModeCommand = new RelayCommand(_ => TogglePlayMode());

        _player.StateChanged += state =>
        {
            IsPlaying = state == PlayState.Playing;
            NowPlayingText = state switch
            {
                PlayState.Playing => $"正在播放：{Path.GetFileName(_player.CurrentPath ?? string.Empty)}",
                PlayState.Paused => $"已暂停：{Path.GetFileName(_player.CurrentPath ?? string.Empty)}",
                _ => "未播放"
            };
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(PositionText));
        };
        _player.StateChanged += _ => PlayPauseCommand.RaiseCanExecuteChanged();
        _player.PlaybackFinished += OnPlaybackFinished;

        // 套用上次保存的音量（播放器尚未创建，Volume 会在下次播放时生效）
        _player.Volume = _volume;
        _playMode = _settings.LoadPlayMode();

        _hotkeys.HotkeyPressed += OnHotkeyPressed;

        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) =>
        {
            // 拖动进度条期间挂起推送：否则 OneWay 绑定会把拇指拉回当前播放位置
            if (SuppressProgressPush) return;
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(PositionText));
        };
        _timer.Start();

        _hotkeys.EnsureInitialized();
        RegisterAllHotkeys();
        UpdateHotkeySummary();

        // 输出设备：先枚举，再套用上次保存的选择
        RefreshOutputDevices();
        var savedGuid = _settings.LoadOutputDevice();
        if (savedGuid != Guid.Empty && OutputDevices.Any(d => d.Guid == savedGuid))
        {
            SelectedOutputDevice = OutputDevices.First(d => d.Guid == savedGuid);
        }
    }

    private readonly System.Windows.Threading.DispatcherTimer _timer;

    public ObservableCollection<Song> Songs { get; }

    public RelayCommand ImportCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand StartCaptureCommand { get; }
    public RelayCommand ClearHotkeyCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand RemoveAllCommand { get; }
    public RelayCommand MoveEarlierCommand { get; }
    public RelayCommand MoveLaterCommand { get; }
    public RelayCommand MoveToStartCommand { get; }
    public RelayCommand MoveToEndCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }
    public RelayCommand VolumeUpCommand { get; }
    public RelayCommand VolumeDownCommand { get; }
    public RelayCommand TogglePlayModeCommand { get; }

    /// <summary>音量 0~1，界面绑定到Slider。</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            var v = (float)Math.Clamp(value, 0, 1);
            if (Math.Abs(_volume - v) < 0.0001f) return;
            _volume = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));

            _player.Volume = v;
            // 拖动滑块时会高频触发，节流后再落盘
            ScheduleVolumeSave();
        }
    }

    /// <summary>是否静音。静音时音量值本身保留，只把实际增益压到 0。</summary>
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;
            _isMuted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));

            _player.Muted = value;
            _settings.SaveVolume(_volume);
        }
    }
    private bool _isMuted;

    public string VolumeText => IsMuted ? "静音" : $"{Math.Round(_volume * 100):0}%";

    private void AdjustVolume(float delta)
    {
        // 从静音状态调音量时自动取消静音，否则用户会以为滑块没反应
        if (IsMuted && delta > 0) IsMuted = false;

        var target = _volume + delta;
        if (target > 1f)
        {
            Volume = 1f;
            if (!IsMuted) StatusText = "音量已最大";
        }
        else if (target < 0f)
        {
            Volume = 0f;
            if (!IsMuted) { IsMuted = true; StatusText = "已静音"; }
        }
        else
        {
            Volume = target;
        }
    }

    private System.Windows.Threading.DispatcherTimer? _volumeSaveTimer;

    /// <summary>音量滑块拖动很频繁，延迟 500ms 再写盘，避免每像素都写文件。</summary>
    private void ScheduleVolumeSave()
    {
        _volumeSaveTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Tick -= OnVolumeSaveTick;
        _volumeSaveTimer.Tick += OnVolumeSaveTick;
        _volumeSaveTimer.Start();
    }

    private void OnVolumeSaveTick(object? sender, EventArgs e)
    {
        _volumeSaveTimer!.Stop();
        _volumeSaveTimer.Tick -= OnVolumeSaveTick;
        _settings.SaveVolume(_volume);
    }

    /// <summary>立即把当前音量写入设置文件（供滑块拖动结束时调用）。</summary>
    public void FlushVolume()
    {
        _volumeSaveTimer?.Stop();
        if (_volumeSaveTimer != null) _volumeSaveTimer.Tick -= OnVolumeSaveTick;
        _settings.SaveVolume(_volume);
    }

    /// <summary>滚轮调节音量。滚轮一格约 3 个细档，取 0.06 一步手感较合适。</summary>
    public void AdjustVolumeByWheel(int wheelDelta)
    {
        if (wheelDelta == 0) return;
        AdjustVolume(Math.Sign(wheelDelta) * 0.06f);
        // 滚轮调完立即落盘，避免节流窗口内关程序丢音量
        FlushVolume();
    }

    /// <summary>拖动进度条期间挂起 200ms 进度推送，由窗口在 Thumb 拖动起止时切换。</summary>
    public bool SuppressProgressPush { get; set; }

    /// <summary>可选的输出设备列表，至少包含"系统默认输出设备"。</summary>
    public IReadOnlyList<AudioOutputDevice> OutputDevices
    {
        get => _outputDevices;
        private set { _outputDevices = value; OnPropertyChanged(); }
    }

    /// <summary>当前选择的输出设备。切换会中断当前播放并在新设备上继续。</summary>
    public AudioOutputDevice? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            // 列表刷新时 ComboBox 会回写 null，这不算用户操作
            if (value == null || ReferenceEquals(_selectedOutputDevice, value)) return;

            _selectedOutputDevice = value;
            OnPropertyChanged();

            _player.SetDevice(value.Guid);
            _settings.SaveOutputDevice(value.Guid);
            StatusText = $"输出设备：{value.Name}";
        }
    }

    /// <summary>正在刷新设备列表（界面上禁用下拉框，避免中途被改值）。</summary>
    public bool IsRefreshingDevices
    {
        get => _isRefreshingDevices;
        private set { if (_isRefreshingDevices != value) { _isRefreshingDevices = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// 重新枚举输出设备。用于插拔耳机/声卡后刷新列表。
    /// 刷新后尽量保留原来的选择，选中的设备若已拔出则退回系统默认。
    /// </summary>
    public void RefreshOutputDevices()
    {
        IsRefreshingDevices = true;
        try
        {
            var previousGuid = _selectedOutputDevice?.Guid ?? _player.CurrentDeviceGuid;
            var devices = AudioDeviceService.GetOutputDevices();

            OutputDevices = devices;

            var restored = devices.FirstOrDefault(d => d.Guid == previousGuid);
            var target = restored ?? devices[0];   // 列表首项恒为"系统默认输出设备"

            _selectedOutputDevice = null;         // 阻止 setter 重复触发切设备
            SelectedOutputDevice = target;
        }
        catch (Exception ex)
        {
            StatusText = $"读取输出设备失败：{ex.Message}";
        }
        finally
        {
            IsRefreshingDevices = false;
        }
    }

    public Song? SelectedSong
    {
        get => _selectedSong;
        set
        {
            _selectedSong = value;
            OnPropertyChanged();
            RemoveCommand.RaiseCanExecuteChanged();
            ToggleCommand.RaiseCanExecuteChanged();
            StartCaptureCommand.RaiseCanExecuteChanged();
            ClearHotkeyCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
    }

    public string NowPlayingText
    {
        get => _nowPlayingText;
        set { if (_nowPlayingText != value) { _nowPlayingText = value; OnPropertyChanged(); } }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set { if (_isPlaying != value) { _isPlaying = value; OnPropertyChanged(); } }
    }

    /// <summary>正在录入快捷键的歌曲；非空时界面显示"按下按键…"提示。</summary>
    public Song? CapturingSong
    {
        get => _capturingSong;
        set
        {
            _capturingSong = value;
            IsCapturingHotkey = value is not null;
            OnPropertyChanged();
        }
    }

    public bool IsCapturingHotkey
    {
        get => _isCapturingHotkey;
        set { if (_isCapturingHotkey != value) { _isCapturingHotkey = value; OnPropertyChanged(); } }
    }

    public double ProgressPercent
    {
        get
        {
            if (_player.DurationSeconds <= 0) return 0;
            return Math.Clamp(_player.PositionSeconds / _player.DurationSeconds * 100, 0, 100);
        }
    }

    /// <summary>"00:12 / 03:25" 形式的进度文本。</summary>
    public string PositionText
    {
        get
        {
            if (_player.DurationSeconds <= 0) return "--:-- / --:--";
            return $"{FormatTime(_player.PositionSeconds)} / {FormatTime(_player.DurationSeconds)}";
        }
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>由窗口在 PreviewKeyDown 时转发过来的按键（仅录入快捷键时有效）。</summary>
    public void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsCapturingHotkey) return;

        // Esc 固定为"取消"，即使单键模式也保留该约定
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CapturingSong = null;
            StatusText = "已取消快捷键录入";
            return;
        }

        var hotkey = HotkeyFormatter.FromKeyEvent(e);
        if (hotkey == null)
        {
            StatusText = "请按字母、数字或 F1-F12；也可用 Ctrl / Alt / Shift 组合";
            return;
        }

        e.Handled = true;
        ApplyHotkey(CapturingSong!, hotkey);
        CapturingSong = null;
    }

    private void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择音频文件",
            Filter = "音频文件|*.mp3;*.wav;*.flac;*.aac;*.m4a;*.wma;*.ogg;*.opus|所有文件|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;

        int added = 0, skipped = 0;
        foreach (var file in dialog.FileNames)
        {
            if (!MetadataService.IsSupported(file)) { skipped++; continue; }
            if (Songs.Any(s => string.Equals(s.FilePath, file, StringComparison.OrdinalIgnoreCase))) { skipped++; continue; }
            Songs.Add(MetadataService.CreateSong(file));
            added++;
        }

        // 先重注册热键（其内部可能写"被占用"警告），再写导入结果，
        // 否则导入反馈会被 RegisterAllHotkeys 的状态文案覆盖掉
        RegisterAllHotkeys();
        RaiseCollectionCommands();
        Save();

        StatusText = $"已导入 {added} 首" + (skipped > 0 ? $"，跳过 {skipped} 个（不支持或已存在）" : "");
    }

    /// <summary>曲库条数变化后，刷新依赖"是否有歌曲"的命令可用状态。</summary>
    private void RaiseCollectionCommands()
    {
        PlayPauseCommand.RaiseCanExecuteChanged();
        RemoveAllCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        RaiseOrderCommands();
    }

    private void RaiseOrderCommands()
    {
        MoveEarlierCommand.RaiseCanExecuteChanged();
        MoveLaterCommand.RaiseCanExecuteChanged();
        MoveToStartCommand.RaiseCanExecuteChanged();
        MoveToEndCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 拖拽过程中调用：仅调整顺序，不落盘、不打扰状态栏。
    /// 拖拽结束时由 <see cref="CommitOrder"/> 统一保存。
    /// </summary>
    public bool MoveSongTo(Song song, int newIndex)
        => SongOrderService.Move(Songs, song, newIndex);

    /// <summary>拖拽结束，保存新顺序。</summary>
    public void CommitOrder(int count)
    {
        if (count > 0) StatusText = $"已调整顺序，共 {count} 首";
        Save();
    }

    private void ShiftSelected(int offset)
    {
        if (SelectedSong == null) return;
        int before = Songs.IndexOf(SelectedSong);
        if (!SongOrderService.Shift(Songs, SelectedSong, offset)) return;

        RaiseOrderCommands();
        StatusText = $"《{SelectedSong.Title}》移动到第 {before + offset + 1} 位";
        Save();
    }

    private void MoveToEdge(bool toStart)
    {
        if (SelectedSong == null) return;
        int before = Songs.IndexOf(SelectedSong);
        if (!SongOrderService.MoveToEdge(Songs, SelectedSong, toStart)) return;

        RaiseOrderCommands();
        StatusText = $"《{SelectedSong.Title}》已移到{(toStart ? "最前" : "最后")}";
        Save();
    }

    private void Remove(Song? song)
    {
        if (song == null) return;

        // 若删除的正是当前播放中的歌曲，必须先停掉，
        // 否则播放器会继续出声，而列表里已经找不到这首歌了。
        bool wasPlaying = _player.CurrentPath is not null &&
                          string.Equals(_player.CurrentPath, song.FilePath, StringComparison.OrdinalIgnoreCase);
        if (wasPlaying) _player.Stop();

        UnregisterHotkey(song);
        Songs.Remove(song);

        if (SelectedSong == song) SelectedSong = null;

        StatusText = $"已移除《{song.Title}》（仅从列表移除，磁盘文件未删除）";
        UpdateHotkeySummary();
        RaiseCollectionCommands();
        Save();
    }

    private void RemoveAll()
    {
        if (Songs.Count == 0) return;

        var answer = System.Windows.MessageBox.Show(
            $"确定要移除全部 {Songs.Count} 首歌曲吗？\n\n仅从列表移除，磁盘上的音频文件不会被删除。",
            "清空曲库",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.OK) return;

        int count = Songs.Count;
        _player.Stop();
        _hotkeys.UnregisterAll();
        _hotkeyIds.Clear();
        Songs.Clear();
        SelectedSong = null;
        StatusText = $"已清空曲库（{count} 首，磁盘文件未删除）";
        UpdateHotkeySummary();
        RaiseCollectionCommands();
        Save();
    }

    /// <summary>
/// 一首歌自然播完后的处理。由播放模式决定：
/// 一次播放 -> 停下；单曲循环 -> 重播这首；列表循环 -> 切下一首（末首回到首首）。
/// </summary>
    private void OnPlaybackFinished()
    {
        switch (PlayMode)
        {
            case PlayMode.RepeatOne:
                // 重播当前这首。此时播放器已停在末尾，Toggle 会重建并从头播。
                StatusText = $"单曲循环：{Path.GetFileName(_player.CurrentPath ?? "")}";
                ReplayCurrent();
                break;

            case PlayMode.RepeatAll:
                // 以当前选中的歌为起点找下一首；选中为空时从当前播放路径反查
                var from = SelectedSong
                           ?? Songs.FirstOrDefault(s => string.Equals(
                               s.FilePath, _player.CurrentPath, StringComparison.OrdinalIgnoreCase));
                var next = NextPlayable(from);
                if (next is null)
                {
                    // 没有下一首可播（比如就一首且文件丢失），退回停止
                    StatusText = "播放结束";
                    OnPropertyChanged(nameof(ProgressPercent));
                    OnPropertyChanged(nameof(PositionText));
                    break;
                }
                StatusText = $"列表循环：下一首《{next.Title}》";
                SelectedSong = next;
                Toggle(next);
                break;

            default:
                StatusText = "播放结束";
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(PositionText));
                break;
        }
    }

    /// <summary>重播当前正在播放（或刚播完）的那首歌。</summary>
    private void ReplayCurrent()
    {
        var path = _player.CurrentPath;
        if (path == null || !File.Exists(path)) return;

        try
        {
            // 播放器此刻是 Stopped 状态，Toggle 会重建播放器并从 0 开始
            _player.Toggle(path);
        }
        catch (Exception ex)
        {
            StatusText = $"重播失败：{ex.Message}";
        }
    }

    /// <summary>取下一首可播放的歌（文件存在）；列表末尾时回到开头。</summary>
    private Song? NextPlayable(Song? current)
    {
        var playable = Songs.Where(s => s.Exists).ToList();
        if (playable.Count == 0) return null;
        if (playable.Count == 1) return current != null && ReferenceEquals(playable[0], current) ? playable[0] : null;

        int idx = current == null ? -1 : playable.IndexOf(current);
        // 只有一首可选时重复它本身；否则按列表顺序取下一首并循环
        return playable[(idx + 1) % playable.Count];
    }

    /// <summary>当前播放模式。</summary>
    public PlayMode PlayMode
    {
        get => _playMode;
        set
        {
            if (_playMode == value) return;
            _playMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlayModeText));
            OnPropertyChanged(nameof(PlayModeIcon));
            _settings.SavePlayMode(value);
            StatusText = PlayModeInfo.Description(value);
        }
    }
    private PlayMode _playMode = PlayMode.Once;

    /// <summary>按钮上显示的图标（▶ / 🔂 / 🔁）。</summary>
    public string PlayModeIcon => PlayModeInfo.Icon(_playMode);

    /// <summary>按钮上显示的简短文字。</summary>
    public string PlayModeText => _playMode switch
    {
        PlayMode.RepeatOne => "单曲循环",
        PlayMode.RepeatAll => "列表循环",
        _ => "一次播放"
    };

    /// <summary>点击同一个开关，在三种模式间循环切换。</summary>
    public void TogglePlayMode() => PlayMode = PlayModeInfo.Next(_playMode);

    private void Toggle(Song? song)
    {
        if (song == null) return;
        if (!song.Exists)
        {
            StatusText = $"文件已丢失：{song.FileName}";
            return;
        }
        try
        {
            _player.Toggle(song.FilePath);
        }
        catch (Exception ex)
        {
            StatusText = $"播放失败：{ex.Message}";
        }
    }

    private void StartCapture(Song? song)
    {
        if (song == null) return;
        SelectedSong = song;
        CapturingSong = song;
        StatusText = "请按下组合键（Esc 取消）";
    }

    private void ClearHotkey(Song? song)
    {
        if (song == null) return;
        UnregisterHotkey(song);
        song.Hotkey = string.Empty;
        StatusText = $"已清除《{song.Title}》的快捷键";
        UpdateHotkeySummary();
        Save();
    }

    private void ApplyHotkey(Song song, string hotkey)
    {
        // 规范化写法，避免 "Ctrl+1" 与 "1+Ctrl" 之类被当成不同键
        var canonical = HotkeyFormatter.Canonicalize(hotkey);

        // 空格键是保留给播放/暂停的全局快捷键，不允许绑到单曲上
        if (HotkeyFormatter.IsSpace(canonical))
        {
            StatusText = "空格键已保留为「播放 / 暂停」的全局快捷键，请为这首歌选择其他按键";
            return;
        }

        // 先移除占用同一组合键的其他歌曲
        var conflict = Songs.FirstOrDefault(s => !ReferenceEquals(s, song) &&
            string.Equals(s.Hotkey, canonical, StringComparison.OrdinalIgnoreCase));
        if (conflict is not null)
        {
            UnregisterHotkey(conflict);
            conflict.Hotkey = string.Empty;
            StatusText = $"快捷键 {canonical} 原属于《{conflict.Title}》，已重新分配";
        }

        UnregisterHotkey(song);
        var newId = NextHotkeyId();
        if (_hotkeys.Register(newId, canonical))
        {
            _hotkeyIds[song] = newId;
            song.Hotkey = canonical;
            if (!StatusText.Contains("原属于")) StatusText = $"《{song.Title}》的快捷键已设为 {canonical}";
            if (HotkeyFormatter.IsRiskySingleKey(canonical))
                StatusText += "（注意：单键会全局抢占该按键，其他程序将无法使用它，建议改用 Ctrl/Alt 组合）";
        }
        else
        {
            // 注册失败时不要把无效组合键写进曲库，否则下次启动仍是个死键
            StatusText = $"快捷键 {canonical} 已被系统或其他程序占用，设置未生效";
        }

        UpdateHotkeySummary();
        Save();
    }

    private int _hotkeyIdSeed = 1;

    private int NextHotkeyId() => _hotkeyIdSeed++;

    private void UnregisterHotkey(Song song)
    {
        if (_hotkeyIds.Remove(song, out var id)) _hotkeys.Unregister(id);
    }

    private void RegisterAllHotkeys()
    {
        _hotkeys.UnregisterAll();
        _hotkeyIds.Clear();
        int failed = 0;

        // 空格键固定作为"播放/暂停"的全局快捷键，占用保留 ID（歌曲热键从 1 开始）
        if (UseGlobalSpaceHotkey && _hotkeys.Register(ReservedPlayPauseId, SpaceHotkey))
        {
            IsSpaceHotkeyActive = true;
        }
        else
        {
            // 被其他程序占用时不阻断启动，只是空格键全局不可用
            IsSpaceHotkeyActive = false;
            if (UseGlobalSpaceHotkey) failed++;
        }

        foreach (var song in Songs)
        {
            if (string.IsNullOrWhiteSpace(song.Hotkey)) continue;
            var canonical = HotkeyFormatter.Canonicalize(song.Hotkey);

            // 空格键已被保留给播放/暂停，这里跳过避免两处注册互相顶掉
            if (HotkeyFormatter.IsSpace(canonical)) continue;

            var id = NextHotkeyId();
            if (_hotkeys.Register(id, canonical))
            {
                _hotkeyIds[song] = id;
                song.Hotkey = canonical;
            }
            else
            {
                failed++;
            }
        }

        var risky = Songs.Count(s => HotkeyFormatter.IsRiskySingleKey(s.Hotkey));
        if (risky > 0)
            StatusText = $"有 {risky} 个单键快捷键正在全局抢占按键（其他程序将无法使用），建议改用 Ctrl/Alt 组合";
        else if (failed > 0)
            StatusText = $"有 {failed} 个快捷键被其他程序占用，未能注册（请更换组合键）";

        UpdateHotkeySummary();
    }

    /// <summary>保留给"播放/暂停"的热键 ID，歌曲热键 ID 从 1 开始以避免冲突。</summary>
    private const int ReservedPlayPauseId = 0;
    private const string SpaceHotkey = "Space";

    private bool _isSpaceHotkeyActive = true;

    /// <summary>
    /// 是否把空格键注册为全局快捷键。关闭后仅在本窗口有焦点时生效。
    /// </summary>
    public bool UseGlobalSpaceHotkey
    {
        get => _useGlobalSpaceHotkey;
        set
        {
            if (_useGlobalSpaceHotkey == value) return;
            _useGlobalSpaceHotkey = value;
            OnPropertyChanged();
            _settings.SaveSpaceGlobalPlayPause(value);
            RegisterAllHotkeys();
            StatusText = value
                ? "空格键已注册为全局播放/暂停（其他程序将无法输入空格，可随时在此关闭）"
                : "空格键已取消全局注册，仅在本窗口有焦点时播放/暂停";
        }
    }

    /// <summary>
    /// 空格键是否已成功注册为全局播放/暂停快捷键。
    /// 被其他程序抢占时为 false，此时只有窗口有焦点时按空格才有效。
    /// </summary>
    public bool IsSpaceHotkeyActive
    {
        get => _isSpaceHotkeyActive;
        private set { if (_isSpaceHotkeyActive != value) { _isSpaceHotkeyActive = value; OnPropertyChanged(); } }
    }

    private string _hotkeySummary = "尚未注册任何快捷键";

    /// <summary>工具栏上展示的快捷键注册概况，便于确认是否真的注册成功。</summary>
    public string HotkeySummary
    {
        get => _hotkeySummary;
        private set { if (_hotkeySummary != value) { _hotkeySummary = value; OnPropertyChanged(); } }
    }

    /// <summary>热键消息窗口句柄，供诊断工具向其投递 WM_HOTKEY 以验证链路。</summary>
    public IntPtr HotkeyMessageHandle => _hotkeys.MessageWindowHandle;

    private void UpdateHotkeySummary()
    {
        // RegisteredHotkeys 只包含注册成功的项（失败的不会被记入），
        // 因此这里的"已注册"与真实状态一致。
        var registered = _hotkeys.RegisteredHotkeys.OrderBy(x => x).ToList();
        if (registered.Count == 0)
        {
            HotkeySummary = "尚未注册任何快捷键";
            return;
        }

        var display = registered.Select(h => HotkeyFormatter.IsSpace(h) ? "Space（播放/暂停）" : h);
        HotkeySummary = $"已注册 {registered.Count} 个全局快捷键：{string.Join("、", display)}";
    }

    private void OnHotkeyPressed(string hotkey)
    {
        // 空格键是保留的"播放/暂停"快捷键，不绑定任何单曲。
        // 它全局生效，因此即使程序在后台、当前播放着别的歌，也能切换其播放状态。
        if (HotkeyFormatter.IsSpace(hotkey))
        {
            TogglePlayPause();
            return;
        }

        var song = Songs.FirstOrDefault(s => string.Equals(s.Hotkey, hotkey, StringComparison.OrdinalIgnoreCase));
        if (song is null) return;
        SelectedSong = song;
        Toggle(song);
    }

    /// <summary>播放/暂停当前选中歌曲；没有选中时退回到第一首可用的歌。</summary>
    private void TogglePlayPause() => Toggle(SelectedSong ?? FirstSong());

    private Song? FirstSong() => Songs.FirstOrDefault(s => s.Exists);

    public void Save() => _library.Save(Songs);

    /// <summary>拖拽进度条。</summary>
    public void SeekTo(double percent)
    {
        if (_player.DurationSeconds <= 0) return;
        _player.Seek(_player.DurationSeconds * Math.Clamp(percent, 0, 100) / 100);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
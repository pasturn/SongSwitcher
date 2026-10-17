using System.IO;
using NAudio.Wave;

namespace SongSwitcher.Services;

public enum PlayState
{
    Stopped,
    Playing,
    Paused
}

/// <summary>
/// 单实例播放器。切换歌曲时自动停止上一首并从头播放新的一首。
/// 线程安全：所有公开成员都在 UI 线程调用，内部用锁保护底层播放器替换。
/// </summary>
public sealed class AudioPlayerService : IDisposable
{
    private readonly object _gate = new();
    private IWavePlayer? _output;
    private AudioFileReader? _reader;
    private string? _currentPath;
    private Guid _deviceGuid = Guid.Empty;
    private float _volume = 1f;
    private bool _muted;

    public PlayState State { get; private set; } = PlayState.Stopped;
    public string? CurrentPath => _currentPath;
    public double DurationSeconds { get; private set; }

    /// <summary>当前使用的输出设备 Guid。</summary>
    public Guid CurrentDeviceGuid
    {
        get { lock (_gate) return _deviceGuid; }
    }

    /// <summary>设备切换时触发（播放会中断并重建）。</summary>
    public event Action? DeviceChanged;

    /// <summary>音量或静音状态变化时触发。</summary>
    public event Action? VolumeChanged;

    /// <summary>
    /// 音量，0~1。播放器未创建时也能设置，下次播放时生效。
    /// </summary>
    public float Volume
    {
        get { lock (_gate) return _volume; }
        set
        {
            lock (_gate)
            {
                var v = Math.Clamp(float.IsNaN(value) ? 1f : value, 0f, 1f);
                if (Math.Abs(_volume - v) < 0.0001f) return;
                _volume = v;
                // 静音状态下实际增益仍为 0，只记住用户设定的音量
                ApplyVolumeCore();
            }
            VolumeChanged?.Invoke();
        }
    }

    /// <summary>是否静音。静音只是把增益压到 0，音量值本身会被保留。</summary>
    public bool Muted
    {
        get { lock (_gate) return _muted; }
        set
        {
            lock (_gate)
            {
                if (_muted == value) return;
                _muted = value;
                ApplyVolumeCore();
            }
            VolumeChanged?.Invoke();
        }
    }

    /// <summary>把当前音量/静音状态施加到底层播放器；必须在锁内调用。</summary>
    private void ApplyVolumeCore()
    {
        // 未创建播放器时无处可设，下次 CreateOutput 后由 OnOutputReady 补上
        if (_output == null) return;

        float gain = _muted ? 0f : _volume;
        try
        {
            switch (_output)
            {
                case WaveOutEvent w: w.Volume = gain; break;
                case DirectSoundOut d: d.Volume = gain; break;
                default: _output.Volume = gain; break;
            }
        }
        catch
        {
            // 设备刚被拔出等情况下设音量可能失败，忽略即可
        }
    }

    /// <summary>
    /// 当前播放位置（秒）。直接读取底层解码器，播放过程中由 NAudio 持续推进。
    /// 暂停时解码器停止，读数自然停在原地，因此无需额外缓存。
    /// </summary>
    public double PositionSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_reader == null) return 0;
                try
                {
                    // CurrentTime 在流切换的瞬间可能抛，视为 0 即可
                    double seconds = _reader.CurrentTime.TotalSeconds;
                    return double.IsNaN(seconds) ? 0 : seconds;
                }
                catch
                {
                    return 0;
                }
            }
        }
    }

    public event Action<PlayState>? StateChanged;

    /// <summary>播放自然结束（读到文件末尾）时触发。</summary>
    public event Action? PlaybackFinished;

    /// <summary>打开并播放指定歌曲；重复点击同一首则视为播放/暂停切换。</summary>
    public void Toggle(string filePath)
    {
        lock (_gate)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("音频文件不存在", filePath);

            if (State != PlayState.Stopped && string.Equals(_currentPath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                TogglePause();
                return;
            }

            StopCore();
            _reader = new AudioFileReader(filePath);
            _output = CreateOutput(_deviceGuid);
            ApplyVolumeCore();      // 必须在 Init 之前，新建的播放器默认满音量
            _output.Init(_reader);
            _output.PlaybackStopped += OnPlaybackStopped;
            _currentPath = filePath;
            DurationSeconds = _reader.TotalTime.TotalSeconds;
            _output.Play();
            SetState(PlayState.Playing);
        }
    }

    /// <summary>
    /// 按指定设备创建播放器。Guid 为空表示系统默认。
    /// 指定设备打开失败时（设备被拔出/独占），退回系统默认，保证不会静默没声音。
    /// </summary>
    private static IWavePlayer CreateOutput(Guid deviceGuid)
    {
        if (deviceGuid == Guid.Empty) return new WaveOutEvent();

        try
        {
            return new DirectSoundOut(deviceGuid);
        }
        catch
        {
            return new WaveOutEvent();
        }
    }

    /// <summary>
    /// 切换输出设备。若正在播放，会从当前位置继续在新设备上播放。
    /// </summary>
    public void SetDevice(Guid deviceGuid)
    {
        double resumeAt;
        string? path;

        lock (_gate)
        {
            if (deviceGuid == _deviceGuid) return;
            _deviceGuid = deviceGuid;

            resumeAt = PositionSeconds;
            path = _currentPath;
            bool wasActive = State != PlayState.Stopped && path != null;

            if (wasActive)
            {
                // 旧设备上的播放必须先完整拆掉，再在新设备上重建
                StopCore();
                try
                {
                    _reader = new AudioFileReader(path!);
                    _output = CreateOutput(deviceGuid);
                    ApplyVolumeCore();      // 新设备上也要恢复音量
                    _output.Init(_reader);
                    _output.PlaybackStopped += OnPlaybackStopped;
                    DurationSeconds = _reader.TotalTime.TotalSeconds;
                    _reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(resumeAt, 0, DurationSeconds));
                    _output.Play();
                    SetState(PlayState.Playing);
                }
                catch
                {
                    // 新设备打不开，退回停止状态而不是让程序崩掉
                    StopCore();
                    SetState(PlayState.Stopped);
                }
            }
        }

        DeviceChanged?.Invoke();
    }

    /// <summary>
    /// WaveOut 在读完、被打断或被主动 Stop 时都会触发此事件，
    /// 必须靠"用户是否主动停止"来区分自然结束与人为操作，否则会误报。
    /// </summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        bool naturalEnd;
        lock (_gate)
        {
            // StopCore 主动 Stop 时会把 _reader 置空，因此这里为空即表示非自然结束
            naturalEnd = _reader != null
                         && State == PlayState.Playing
                         && DurationSeconds > 0
                         && _reader.CurrentTime.TotalSeconds >= DurationSeconds - 0.25;
        }

        if (!naturalEnd) return;

        lock (_gate)
        {
            if (State == PlayState.Stopped) return;
            SetState(PlayState.Stopped);
        }
        PlaybackFinished?.Invoke();
    }

    public void TogglePause()
    {
        lock (_gate)
        {
            if (_output == null || State == PlayState.Stopped)
                return;
            if (State == PlayState.Playing)
            {
                _output.Stop();
                SetState(PlayState.Paused);
            }
            else
            {
                _reader!.CurrentTime = TimeSpan.FromSeconds(Math.Min(PositionSeconds, DurationSeconds));
                _output.Play();
                SetState(PlayState.Playing);
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
            SetState(PlayState.Stopped);
        }
    }

    /// <summary>供界面拖动进度条使用。</summary>
    public void Seek(double seconds)
    {
        lock (_gate)
        {
            if (_reader == null || State == PlayState.Stopped)
                return;
            seconds = Math.Clamp(seconds, 0, DurationSeconds);
            _reader.CurrentTime = TimeSpan.FromSeconds(seconds);
        }
    }

    private void StopCore()
    {
        if (_output != null)
        {
            // 先摘掉回调，避免释放后仍收到 PlaybackStopped
            _output.PlaybackStopped -= OnPlaybackStopped;
            try { _output.Stop(); } catch { /* 设备可能已被拔出 */ }
            _output.Dispose();
            _output = null;
        }
        _reader?.Dispose();
        _reader = null;
        DurationSeconds = 0;
    }

    private void SetState(PlayState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopCore();
            SetState(PlayState.Stopped);
        }
    }
}
using System;
using System.IO;
using System.Text.Json;

namespace SongSwitcher.Services;

/// <summary>
/// 应用级设置持久化（输出设备、音量、空格全局快捷键开关）。
/// 内部缓存一份快照，读写都走缓存，避免"保存某一项时把其他项覆盖成默认值"。
/// </summary>
public sealed class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SongSwitcher", "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private sealed class Settings
    {
        public Guid OutputDeviceGuid { get; set; } = Guid.Empty;

        /// <summary>
        /// 是否把空格键注册为全局播放/暂停快捷键。
        /// 默认开启；因为它会全局抢占空格键（其他程序将打不出空格），所以做成可关闭的开关。
        /// </summary>
        public bool SpaceGlobalPlayPause { get; set; } = true;

        /// <summary>上次设置的音量，0~1。默认 0.8（不宜默认满音量，避免突然大音量）。</summary>
        public double Volume { get; set; } = 0.8;

        /// <summary>播放模式。缺省按"一次播放"处理。</summary>
        public PlayMode PlayMode { get; set; } = PlayMode.Once;
    }

    // 缓存快照，所有读改写都基于它，读写之间不会互相覆盖
    private Settings _cache;
    private bool _loaded;

    public SettingsService()
    {
        _cache = LoadFromDisk();
        _loaded = true;   // 构造时已加载，避免首个访问器再读一遍磁盘
    }

    private Settings LoadFromDisk()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new Settings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<Settings>(json, Options) ?? new Settings();
        }
        catch
        {
            // 文件不存在或损坏都用默认值，不阻塞启动
            return new Settings();
        }
    }

    /// <summary>把当前缓存写回磁盘。失败不影响使用。</summary>
    private void Flush()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_cache, Options);

            // 与 LibraryService 相同的原子写入策略，避免半截 JSON
            var tmpPath = SettingsPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            if (File.Exists(SettingsPath))
                File.Replace(tmpPath, SettingsPath, null);
            else
                File.Move(tmpPath, SettingsPath);
        }
        catch
        {
            // 设置保存失败不影响使用
        }
    }

    /// <summary>读取上次选择的输出设备。文件不存在或损坏时返回系统默认。</summary>
    public Guid LoadOutputDevice()
    {
        if (!_loaded) { _cache = LoadFromDisk(); _loaded = true; }
        return _cache.OutputDeviceGuid;
    }

    public void SaveOutputDevice(Guid guid)
    {
        if (!_loaded) { _cache = LoadFromDisk(); _loaded = true; }
        _cache.OutputDeviceGuid = guid;
        Flush();
    }

    /// <summary>是否启用"空格键全局播放/暂停"。</summary>
    public bool LoadSpaceGlobalPlayPause()
    {
        if (!_loaded) { _cache = LoadFromDisk(); _loaded = true; }
        return _cache.SpaceGlobalPlayPause;
    }

    public void SaveSpaceGlobalPlayPause(bool enabled)
    {
        if (!_loaded) { _cache = LoadFromDisk(); _loaded = true; }
        _cache.SpaceGlobalPlayPause = enabled;
        Flush();
    }

    /// <summary>读取上次保存的音量。越界或损坏时回退到默认 0.8。</summary>
    public float LoadVolume()
    {
        if (!_loaded) { _cache = LoadFromDisk(); _loaded = true; }
        var v = _cache.Volume;
        return double.IsNaN(v) || v < 0 || v > 1 ? 0.8f : (float)v;
    }

    public void SaveVolume(float volume)
    {
        if (!_loaded) { _cache = LoadFromDisk(); _loaded = true; }
        _cache.Volume = Math.Clamp(volume, 0f, 1f);
        Flush();
    }

    /// <summary>读取播放模式。</summary>
    public PlayMode LoadPlayMode()
    {
        if (!_loaded) { _cache = LoadFromDisk(); _loaded = true; }
        return _cache.PlayMode;
    }

    public void SavePlayMode(PlayMode mode)
    {
        if (!_loaded) { _cache = LoadFromDisk(); _loaded = true; }
        _cache.PlayMode = mode;
        Flush();
    }
}
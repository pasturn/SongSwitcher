namespace SongSwitcher.Services;

/// <summary>播放模式。</summary>
public enum PlayMode
{
    /// <summary>播完当前这首就停。</summary>
    Once,

    /// <summary>循环播放当前这一首。</summary>
    RepeatOne,

    /// <summary>播完自动切到下一首，最后一首播完回到第一首。</summary>
    RepeatAll
}

/// <summary>播放模式的展示信息（界面据此显示文字/图标）。</summary>
public static class PlayModeInfo
{
    /// <summary>按"一次播放 → 单曲循环 → 列表循环 → 一次播放"的顺序切换。</summary>
    public static PlayMode Next(PlayMode mode) => mode switch
    {
        PlayMode.Once => PlayMode.RepeatOne,
        PlayMode.RepeatOne => PlayMode.RepeatAll,
        _ => PlayMode.Once
    };

    /// <summary>按钮上显示的图标。</summary>
    public static string Icon(PlayMode mode) => mode switch
    {
        PlayMode.Once => "▶",
        PlayMode.RepeatOne => "🔂",
        _ => "🔁"
    };

    /// <summary>状态栏/提示用的说明文字。</summary>
    public static string Description(PlayMode mode) => mode switch
    {
        PlayMode.Once => "一次播放（播完即停）",
        PlayMode.RepeatOne => "单曲循环（重复当前这首）",
        _ => "列表循环（播完自动切下一首）"
    };
}
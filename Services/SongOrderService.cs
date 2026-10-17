using System.Collections.ObjectModel;
using SongSwitcher.Models;

namespace SongSwitcher.Services;

/// <summary>
/// 曲库排序：把"把某首歌移动到目标位置"这件事集中处理，
/// 供界面拖拽与键盘移动共用。
/// </summary>
public static class SongOrderService
{
    /// <summary>
    /// 将歌曲移动到指定的新索引。越界或无变化时返回 false。
    /// </summary>
    public static bool Move(ObservableCollection<Song> songs, Song song, int newIndex)
    {
        int oldIndex = songs.IndexOf(song);
        if (oldIndex < 0) return false;

        newIndex = Math.Clamp(newIndex, 0, songs.Count - 1);
        if (newIndex == oldIndex) return false;

        songs.Move(oldIndex, newIndex);
        return true;
    }

    /// <summary>相对当前索引偏移若干位（-1 前移，+1 后移），用于 Ctrl+方向键。</summary>
    public static bool Shift(ObservableCollection<Song> songs, Song song, int offset)
        => Move(songs, song, songs.IndexOf(song) + offset);

    /// <summary>移动到最前 / 最后。</summary>
    public static bool MoveToEdge(ObservableCollection<Song> songs, Song song, bool toStart)
        => Move(songs, song, toStart ? 0 : songs.Count - 1);
}
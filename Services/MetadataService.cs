using System.IO;
using NAudio.Wave;
using SongSwitcher.Models;

namespace SongSwitcher.Services;

/// <summary>读取音频时长与标题元信息。</summary>
public static class MetadataService
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".wma", ".ogg", ".opus", ".aiff", ".mp4"
    };

    public static bool IsSupported(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    /// <summary>尽量读取时长；读取失败（例如需要额外解码器）返回 null，不影响主流程。</summary>
    public static string? TryReadDuration(string path)
    {
        try
        {
            using var reader = new AudioFileReader(path);
            var t = reader.TotalTime;
            return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        }
        catch
        {
            return null;
        }
    }

    public static Song CreateSong(string path)
    {
        var song = new Song
        {
            FilePath = path,
            Title = Path.GetFileNameWithoutExtension(path)
        };
        song.DurationText = TryReadDuration(path);
        return song;
    }
}
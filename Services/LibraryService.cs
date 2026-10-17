using System.IO;
using System.Text.Json;
using SongSwitcher.Models;

namespace SongSwitcher.Services;

/// <summary>曲库持久化：把歌曲列表与快捷键保存到 AppData 下的 json 文件。</summary>
public sealed class LibraryService
{
    private static readonly string LibraryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SongSwitcher", "library.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public List<Song> Load()
    {
        try
        {
            if (!File.Exists(LibraryPath)) return new List<Song>();
            var json = File.ReadAllText(LibraryPath);
            return JsonSerializer.Deserialize<List<Song>>(json, Options) ?? new List<Song>();
        }
        catch
        {
            // 曲库文件损坏时不阻塞启动，退回空列表
            return new List<Song>();
        }
    }

    public void Save(IEnumerable<Song> songs)
    {
        var dir = Path.GetDirectoryName(LibraryPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(songs, Options);

        // 原子写入：先写临时文件再替换。直接 WriteAllText 是"截断后重写"，
        // 若中途崩溃/断电会留下半截 JSON，下次启动整份曲库就没了。
        var tmpPath = LibraryPath + ".tmp";
        File.WriteAllText(tmpPath, json);
        if (File.Exists(LibraryPath))
            File.Replace(tmpPath, LibraryPath, null);
        else
            File.Move(tmpPath, LibraryPath);
    }
}
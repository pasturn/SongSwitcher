using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;

namespace SongSwitcher.Services;

/// <summary>一个可选的音频输出设备。</summary>
public sealed class AudioOutputDevice
{
    public AudioOutputDevice(Guid guid, string name, string? description = null)
    {
        Guid = guid;
        Name = name;
        Description = description;
    }

    /// <summary>DirectSound 设备唯一标识。系统默认设备为Guid.Empty。</summary>
    public Guid Guid { get; }

    /// <summary>界面显示名称，已做去重处理（同名设备会追加序号）。</summary>
    public string Name { get; }

    public string? Description { get; }

    public bool IsDefault => Guid == Guid.Empty;

    /// <summary>
    /// 是否为"系统默认输出设备"这一项。
    /// 注意不能只看 Guid：DirectSound 里"主声音驱动程序"也是 Guid.Empty，
    /// 它和我们的默认项指向同一个端点，必须单独标记，否则会重复显示两项却行为一样。
    /// </summary>
    public bool IsSystemDefault { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// 输出设备枚举。优先用 WASAPI 拿可读设备名（DirectSound 的Description 常是硬件名，
/// 不够友好），拿不到时退回 DirectSound 列表，保证至少有一项可选。
/// </summary>
public static class AudioDeviceService
{
    /// <summary>
    /// 枚举可用的输出设备。始终至少包含一项"系统默认"。
    /// 任何一步失败都降级处理，绝不抛异常——设备枚举失败不该让整个程序起不来。
    /// </summary>
    public static IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        var result = new List<AudioOutputDevice>
        {
            new(Guid.Empty, "系统默认输出设备") { IsSystemDefault = true },
        };

        // 收集 DirectSound 设备（实际可用的播放端点）
        var dsDevices = new List<DirectSoundDeviceInfo>();
        try
        {
            dsDevices.AddRange(DirectSoundOut.Devices);
        }
        catch
        {
            // DirectSound 不可用（少见，如极简系统），后面只能退回 waveOut 默认设备
        }

        if (dsDevices.Count == 0) return Dedupe(result);

        // "主声音驱动程序"是系统默认端点本身的 DirectSound 条目（Guid 也是 Empty），
        // 它和我们的默认项重复且行为完全相同，跳过以免下拉框里出现两个一样的项。
        var systemDefaultNames = new[] { "主声音驱动程序", "Primary Sound Driver" };
        var usable = dsDevices
            .Where(d => !systemDefaultNames.Any(n =>
                string.Equals(n, d.Description?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (usable.Count == 0) return Dedupe(result);

        // 尝试用 WASAPI 的友好名称覆盖 DirectSound 的硬件描述名
        var friendlyNames = BuildFriendlyNameMap(usable);

        foreach (var ds in usable)
        {
            var name = friendlyNames.TryGetValue(ds.Guid, out var friendly) && !string.IsNullOrWhiteSpace(friendly)
                ? friendly
                : string.IsNullOrWhiteSpace(ds.Description) ? "未知输出设备" : ds.Description.Trim();

            result.Add(new AudioOutputDevice(ds.Guid, name, ds.Description));
        }

        return Dedupe(result);
    }

    /// <summary>
    /// 建立 DirectSound Guid -> WASAPI 友好名 的映射。
    /// 两者无法直接按 Guid 等值关联，只能靠设备名做模糊匹配，所以匹配失败就保留原名。
    /// </summary>
    private static Dictionary<Guid, string> BuildFriendlyNameMap(List<DirectSoundDeviceInfo> dsDevices)
    {
        var map = new Dictionary<Guid, string>();
        try
        {
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var endpoint in enumerator.EnumerateAudioEndPoints(
                         NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
            {
                // 去掉 WASAPI 名里的"(数字输出)"这类后缀，便于和 DirectSound 名对齐
                var friendly = endpoint.FriendlyName;
                foreach (var ds in dsDevices)
                {
                    if (map.ContainsKey(ds.Guid)) continue;
                    if (NamesMatch(friendly, ds.Description))
                    {
                        map[ds.Guid] = friendly;
                        break;
                    }
                }
            }
        }
        catch
        {
            // WASAPI 不可用不影响主流程
        }
        return map;
    }

    /// <summary>判断 WASAPI 名与 DirectSound 描述是否指向同一个物理设备。</summary>
    private static bool NamesMatch(string wasapiName, string? dsDescription)
    {
        if (string.IsNullOrWhiteSpace(dsDescription)) return false;

        static string Normalize(string s)
        {
            // 去掉括号内容、空格、连字符，并转小写
            var sb = new System.Text.StringBuilder();
            int depth = 0;
            foreach (var c in s)
            {
                if (c == '(' || c == '（') { depth++; continue; }
                if (c == ')' || c == '）') { depth--; continue; }
                if (depth > 0) continue;
                if (char.IsWhiteSpace(c) || c == '-' || c == '_') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        var a = Normalize(wasapiName);
        var b = Normalize(dsDescription);
        if (a.Length == 0 || b.Length == 0) return false;

        // 互相包含即认为是同一设备（"耳机 (K69 Audio Device)" vs "K69 Audio Device"）
        return a.Contains(b) || b.Contains(a);
    }

    /// <summary>去掉重复名称：同名时追加 (2)、(3)，避免下拉框里出现多个一模一样的项。</summary>
    private static List<AudioOutputDevice> Dedupe(List<AudioOutputDevice> devices)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices)
        {
            if (!seen.TryGetValue(d.Name, out int n))
            {
                seen[d.Name] = 1;
                continue;
            }
            seen[d.Name] = ++n;
            var renamed = new AudioOutputDevice(d.Guid, $"{d.Name} ({n})", d.Description);
            // 借助列表索引替换名称
            devices[devices.IndexOf(d)] = renamed;
            seen[renamed.Name] = 1;
        }
        return devices;
    }
}
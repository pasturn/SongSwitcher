using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace SongSwitcher.Services;

/// <summary>
/// 全局快捷键注册器。基于 Win32 RegisterHotKey + 消息专用窗口，
/// 因此程序在后台时快捷键依然生效。
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private static readonly IntPtr HwndMessage = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Dictionary<int, string> _registered = new();
    private HwndSource? _source;

    /// <summary>某个快捷键被按下。参数为该快捷键的规范字符串。</summary>
    public event Action<string>? HotkeyPressed;

    /// <summary>
    /// 消息窗口句柄，未初始化时为 IntPtr.Zero。
    /// 消息专用窗口不参与顶层枚举，外部工具无法直接查找，故在此暴露以便诊断。
    /// </summary>
    public IntPtr MessageWindowHandle => _source?.Handle ?? IntPtr.Zero;

    /// <summary>当前已成功注册的快捷键（用于界面展示与排障）。</summary>
    public IReadOnlyCollection<string> RegisteredHotkeys => _registered.Values;

    /// <summary>确保消息窗口已创建（必须在 UI 线程调用一次）。</summary>
    public void EnsureInitialized()
    {
        if (_source != null) return;

        var parameters = new HwndSourceParameters("SongSwitcherHotkeySink")
        {
            ParentWindow = HwndMessage,
            Width = 0,
            Height = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>注册一个快捷键。id 由外部提供以便统一注销。</summary>
    public bool Register(int id, string hotkey)
    {
        EnsureInitialized();
        if (!HotkeyFormatter.TryParse(hotkey, out var modifiers, out var key))
            return false;
        if (_registered.ContainsKey(id)) Unregister(id);

        if (!RegisterHotKey(_source!.Handle, id, modifiers, key))
            return false;

        // 记录 id -> 规范字符串。WM_HOTKEY 的 lParam 只带 id回来，
        // 必须靠这张表还原出是哪个组合键被按下。
        _registered[id] = HotkeyFormatter.Canonicalize(hotkey);
        return true;
    }

    public void Unregister(int id)
    {
        if (!_registered.Remove(id)) return;
        if (_source != null) UnregisterHotKey(_source.Handle, id);
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys.ToList()) Unregister(id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;

        handled = true;
        // WM_HOTKEY: wParam 是热键 ID，lParam 未使用。
        var id = wParam.ToInt32();
        if (_registered.TryGetValue(id, out var hotkey))
            HotkeyPressed?.Invoke(hotkey);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }
}

/// <summary>把 "Ctrl+Alt+1" 这样的字符串与 Win32 modifier/vk 互相转换。</summary>
public static class HotkeyFormatter
{
    /// <summary>空格键的 Win32 虚拟键码。</summary>
    public const uint VkSpace = 0x20;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    /// <summary>
    /// 解析快捷键字符串。支持带修饰键（Ctrl+Alt+1）与单键（如 "Q"、"F5"）两种形式。
    /// 单键形式会全局抢占该按键，仅在用户明确需要时使用。
    /// </summary>
    public static bool TryParse(string? hotkey, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        if (string.IsNullOrWhiteSpace(hotkey)) return false;

        foreach (var part in hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; continue;
                case "alt": modifiers |= ModAlt; continue;
                case "shift": modifiers |= ModShift; continue;
                case "win" or "windows": modifiers |= ModWin; continue;
            }

            if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
            {
                key = (uint)char.ToUpperInvariant(part[0]);
                continue;
            }
            if (part.StartsWith("Num", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(part[3..], out int nk) && nk is >= 0 and <= 9)
            {
                key = (uint)(0x60 + nk);
                continue;
            }
            if (part.StartsWith('F') && int.TryParse(part[1..], out int fk) && fk is >= 1 and <= 24)
            {
                key = (uint)(0x70 + fk - 1);
                continue;
            }
            // 空格键单独支持：作为播放/暂停的默认全局快捷键
            if (part.Equals("Space", StringComparison.OrdinalIgnoreCase))
            {
                key = VkSpace;
                continue;
            }
            return false;
        }

        // 至少要有一个有效主键；修饰键可以为空（单键模式）
        if (key == 0) return false;
        modifiers |= ModNoRepeat;
        return true;
    }

    /// <summary>该快捷键是否为不带修饰键的单键形式。</summary>
    public static bool IsSingleKey(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return false;
        if (!TryParse(hotkey, out var mods, out _)) return false;
        const uint realMods = ModAlt | ModControl | ModShift | ModWin;
        return (mods & realMods) == 0;
    }

    /// <summary>
    /// 单键形式中不建议使用的按键：这些键被系统广泛用于日常操作，
    /// 全局抢占后会导致其他程序无法正常使用（例如按 Q 会在所有软件里打不出 q）。
    /// </summary>
    public static bool IsRiskySingleKey(string? hotkey)
    {
        if (!IsSingleKey(hotkey)) return false;
        if (!TryParse(hotkey, out _, out var key)) return false;

        // 字母、数字、小键盘数字、常用编辑键（Backspace/Delete/Enter/Tab/Space/Esc）
        if (key is >= 0x30 and <= 0x39) return true;   // 0-9
        if (key is >= 0x41 and <= 0x5A) return true;   // A-Z
        if (key is >= 0x60 and <= 0x69) return true;   // 小键盘 0-9
        if (key is 0x08 or 0x0D or 0x09 or 0x20 or 0x1B or 0x2E) return true; // Backspace Enter Tab Space Esc Delete
        return false;
    }

    /// <summary>规整为 "Ctrl+Alt+Shift+Win+Key" 的标准顺序；单键则原样返回主键名。</summary>
    public static string Canonicalize(string hotkey)
    {
        if (!TryParse(hotkey, out var modifiers, out var key))
            return hotkey;

        var parts = new List<string>();
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(KeyToString(key));
        return string.Join('+', parts);
    }

    public static string KeyToString(uint key) => key switch
    {
        VkSpace => "Space",
        >= 0x70 and <= 0x87 => "F" + (key - 0x70 + 1).ToString(),
        _ when key is >= 0x30 and <= 0x39 => ((char)key).ToString(),
        _ when key is >= 0x41 and <= 0x5A => ((char)key).ToString(),
        _ when key is >= 0x60 and <= 0x69 => "Num" + (key - 0x60).ToString(),
        _ => "?"
    };

    /// <summary>判断该字符串是否表示空格键（不区分大小写、忽略修饰键）。</summary>
    public static bool IsSpace(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return false;
        if (!TryParse(hotkey, out _, out var key)) return false;
        return key == VkSpace;
    }

    /// <summary>从 KeyEventArgs 生成规范快捷键字符串。允许不带修饰键的单键。</summary>
    public static string? FromKeyEvent(KeyEventArgs e)
    {
        // 按住 Alt 时 WPF 把按键报成"系统键"：e.Key == Key.System，真实主键在 e.SystemKey。
        // 不做这个映射的话，所有带 Alt 的组合键（如 Alt+F5）都会解析失败、无法录入。
        var actualKey = e.Key == Key.System ? e.SystemKey : e.Key;

        var mods = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods.Add("Win");

        string? key = actualKey switch
        {
            Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
                or Key.System => null,   // 纯修饰键本身不能作为主键
            >= Key.D0 and <= Key.D9 => ((int)actualKey - (int)Key.D0).ToString(),
            >= Key.A and <= Key.Z => actualKey.ToString().ToUpperInvariant(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((int)actualKey - (int)Key.NumPad0).ToString(),
            Key.F1 or Key.F2 or Key.F3 or
            Key.F4 or Key.F5 or Key.F6 or
            Key.F7 or Key.F8 or Key.F9 or
            Key.F10 or Key.F11 or Key.F12 =>
                actualKey.ToString().ToUpperInvariant(),
            _ => null
        };
        if (key == null) return null;

        mods.Add(key);
        return string.Join('+', mods);
    }
}
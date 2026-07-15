using System.Globalization;
using System.Windows.Forms;
using static SandS.Native;

namespace SandS;

/// <summary>
/// 1 つのキーの指定。"h" / "Left" / "F12" のような名前指定と、
/// "sc027" のようなスキャンコード指定の両方を扱う。
///
/// スキャンコード指定は日本語配列で重要。例えば sc027 (;) や sc070 (カタカナ/ひらがな) は
/// 仮想キーコードが配列やIME状態で揺れるので、物理位置で指定する方が確実。
/// ByScan の場合は一致判定も送出もスキャンコードで行う。
/// </summary>
internal readonly record struct KeySpec(ushort Vk, ushort Scan, bool ByScan, bool Extended, string Text)
{
    private static readonly Dictionary<string, Keys> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BackSpace"] = Keys.Back,
        ["BS"] = Keys.Back,
        ["Enter"] = Keys.Return,
        ["Esc"] = Keys.Escape,
        ["AppsKey"] = Keys.Apps,       // Keys.Menu は Alt なので別名にしない
        ["ContextMenu"] = Keys.Apps,
        ["LAlt"] = Keys.LMenu,
        ["RAlt"] = Keys.RMenu,
        ["Alt"] = Keys.LMenu,
        ["LCtrl"] = Keys.LControlKey,
        ["RCtrl"] = Keys.RControlKey,
        ["Ctrl"] = Keys.LControlKey,
        ["LShift"] = Keys.LShiftKey,
        ["RShift"] = Keys.RShiftKey,
        ["Shift"] = Keys.LShiftKey,
        ["Del"] = Keys.Delete,
        ["Ins"] = Keys.Insert,
        ["PgUp"] = Keys.PageUp,
        ["PgDn"] = Keys.PageDown,
        // 日本語キーボード
        ["Muhenkan"] = (Keys)0x1D,
        ["Henkan"] = (Keys)0x1C,
        ["KanaMode"] = (Keys)0x15,
        ["Kanji"] = (Keys)0x19,
    };

    private static readonly HashSet<Keys> ExtendedKeys =
    [
        Keys.RControlKey, Keys.RMenu, Keys.LWin, Keys.RWin, Keys.Apps,
        Keys.Insert, Keys.Delete, Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown,
        Keys.Left, Keys.Right, Keys.Up, Keys.Down,
        Keys.NumLock, Keys.Divide, Keys.PrintScreen,
    ];

    public static bool TryParse(string? s, out KeySpec key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();

        // sc027 / sc14B — AHK 互換。0x100 のビットが立っていれば拡張キー。
        if (s.StartsWith("sc", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int sc))
        {
            bool ext = (sc & 0x100) != 0;
            ushort scan = (ushort)(sc & 0xFF);
            key = new KeySpec(0, scan, ByScan: true, ext, s);
            return true;
        }

        // vk1D — 仮想キーコード直接指定
        if (s.StartsWith("vk", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int vkRaw))
            return TryFromVk((Keys)vkRaw, s, out key);

        if (Aliases.TryGetValue(s, out var aliased))
            return TryFromVk(aliased, s, out key);

        // "1" は Enum.TryParse だと数値 1 (=Keys.LButton) として通ってしまうので先に潰す
        if (s.Length == 1 && s[0] is >= '0' and <= '9')
            return TryFromVk(Keys.D0 + (s[0] - '0'), s, out key);

        if (Enum.TryParse<Keys>(s, ignoreCase: true, out var parsed) && parsed != Keys.None &&
            !int.TryParse(s, out _))
            return TryFromVk(parsed, s, out key);

        return false;
    }

    private static bool TryFromVk(Keys vk, string text, out KeySpec key)
    {
        if (vk == Keys.None) { key = default; return false; }
        ushort scan = (ushort)MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        key = new KeySpec((ushort)vk, scan, ByScan: false, ExtendedKeys.Contains(vk), text);
        return true;
    }

    /// <summary>
    /// フックが受け取ったイベントがこのキーか。
    /// sc 指定なら物理位置 (スキャンコード) で、名前指定なら仮想キーコードで判定する。
    /// </summary>
    public bool Matches(in KBDLLHOOKSTRUCT info) =>
        ByScan ? info.scanCode == Scan && ((info.flags & LLKHF_EXTENDED) != 0) == Extended
               : info.vkCode == Vk;

    public override string ToString() => Text;
}

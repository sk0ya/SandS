using System.Globalization;

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
/// 仮想キーコードは Code に入れる。プロパティ名を Vk にすると、この型の中で
/// 列挙型 Vk が名前解決できなくなるため。
internal readonly record struct KeySpec(ushort Code, ushort Scan, bool ByScan, bool Extended, string Text)
{
    private static readonly Dictionary<string, Vk> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BackSpace"] = Vk.Back,
        ["BS"] = Vk.Back,
        ["Enter"] = Vk.Return,
        ["Esc"] = Vk.Escape,
        ["AppsKey"] = Vk.Apps,       // Vk.Menu は Alt なので別名にしない
        ["ContextMenu"] = Vk.Apps,
        ["LAlt"] = Vk.LMenu,
        ["RAlt"] = Vk.RMenu,
        ["Alt"] = Vk.LMenu,
        ["LCtrl"] = Vk.LControlKey,
        ["RCtrl"] = Vk.RControlKey,
        ["Ctrl"] = Vk.LControlKey,
        ["LShift"] = Vk.LShiftKey,
        ["RShift"] = Vk.RShiftKey,
        ["Shift"] = Vk.LShiftKey,
        ["Del"] = Vk.Delete,
        ["Ins"] = Vk.Insert,
        ["PgUp"] = Vk.PageUp,
        ["PgDn"] = Vk.PageDown,
        // 日本語キーボード
        ["Muhenkan"] = (Vk)0x1D,
        ["Henkan"] = (Vk)0x1C,
        ["KanaMode"] = (Vk)0x15,
        ["Kanji"] = (Vk)0x19,
    };

    private static readonly HashSet<Vk> ExtendedKeys =
    [
        Vk.RControlKey, Vk.RMenu, Vk.LWin, Vk.RWin, Vk.Apps,
        Vk.Insert, Vk.Delete, Vk.Home, Vk.End, Vk.PageUp, Vk.PageDown,
        Vk.Left, Vk.Right, Vk.Up, Vk.Down,
        Vk.NumLock, Vk.Divide, Vk.PrintScreen,
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
            return TryFromVk((Vk)vkRaw, s, out key);

        if (Aliases.TryGetValue(s, out var aliased))
            return TryFromVk(aliased, s, out key);

        // "1" は Enum.TryParse だと数値 1 として通ってしまうので先に潰す
        if (s.Length == 1 && s[0] is >= '0' and <= '9')
            return TryFromVk((Vk)((int)Vk.D0 + (s[0] - '0')), s, out key);

        if (Enum.TryParse<Vk>(s, ignoreCase: true, out var parsed) && parsed != Vk.None &&
            !int.TryParse(s, out _))
            return TryFromVk(parsed, s, out key);

        return false;
    }

    private static bool TryFromVk(Vk vk, string text, out KeySpec key)
    {
        if (vk == Vk.None) { key = default; return false; }
        ushort scan = (ushort)MapVirtualKeyW((uint)vk, MAPVK_VK_TO_VSC);
        key = new KeySpec((ushort)vk, scan, ByScan: false, ExtendedKeys.Contains(vk), text);
        return true;
    }

    /// <summary>
    /// フックが受け取ったイベントがこのキーか。
    /// sc 指定なら物理位置 (スキャンコード) で、名前指定なら仮想キーコードで判定する。
    /// </summary>
    public bool Matches(in KBDLLHOOKSTRUCT info) =>
        ByScan ? info.scanCode == Scan && ((info.flags & LLKHF_EXTENDED) != 0) == Extended
               : info.vkCode == Code;

    public override string ToString() => Text;
}



namespace SandS;

[Flags]
internal enum ModGroup
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// 物理的に押されている修飾キーを 1 バイトのビットマスクで持つ。
/// 修飾キーは 8 種しかないので、HashSet を使う理由がない
/// (打鍵ごとに走る経路なので、確保も走査も避けたい)。
/// </summary>
internal static class ModMask
{
    /// <summary>ビット位置と対応する仮想キー。順序がビット番号そのもの。</summary>
    public static readonly Vk[] Vks =
    [
        Vk.LControlKey, Vk.RControlKey,
        Vk.LShiftKey, Vk.RShiftKey,
        Vk.LMenu, Vk.RMenu,
        Vk.LWin, Vk.RWin,
    ];

    public static int BitOf(Vk vk) => vk switch
    {
        Vk.LControlKey => 0,
        Vk.RControlKey => 1,
        Vk.LShiftKey => 2,
        Vk.RShiftKey => 3,
        Vk.LMenu => 4,
        Vk.RMenu => 5,
        Vk.LWin => 6,
        Vk.RWin => 7,
        _ => -1,
    };

    public static ModGroup GroupsOf(byte mask)
    {
        var g = ModGroup.None;
        if ((mask & 0b0000_0011) != 0) g |= ModGroup.Ctrl;
        if ((mask & 0b0000_1100) != 0) g |= ModGroup.Shift;
        if ((mask & 0b0011_0000) != 0) g |= ModGroup.Alt;
        if ((mask & 0b1100_0000) != 0) g |= ModGroup.Win;
        return g;
    }
}

/// <summary>
/// AHK 風のキーコンボ。"^#Left" / "!F4" / "{Blind}Left" / "sc029" / "#1" など。
///
///   ^ = Ctrl, ! = Alt, + = Shift, # = Win
///   {Blind} を先頭に付けると、物理的に押されている修飾キーをそのまま残して送る。
///   付けない場合は AHK の既定と同じく、コンボに含まれない修飾キーを一時的に外して送る。
/// </summary>
internal sealed record Combo(ModGroup Mods, KeySpec Key, bool Blind)
{
    public static bool TryParse(string? s, out Combo? combo, out string? error)
    {
        combo = null;
        error = null;

        if (string.IsNullOrWhiteSpace(s)) { error = "空です"; return false; }
        s = s.Trim();

        bool blind = false;
        if (s.StartsWith("{Blind}", StringComparison.OrdinalIgnoreCase))
        {
            blind = true;
            s = s["{Blind}".Length..].Trim();
        }

        var mods = ModGroup.None;
        int i = 0;
        while (i < s.Length)
        {
            switch (s[i])
            {
                case '^': mods |= ModGroup.Ctrl; break;
                case '!': mods |= ModGroup.Alt; break;
                case '+': mods |= ModGroup.Shift; break;
                case '#': mods |= ModGroup.Win; break;
                // AHK の左右指定。一致判定は左右を区別しないので読み飛ばす。
                case '<' or '>': break;
                default: goto done;
            }
            i++;
        }
    done:

        string keyPart = s[i..].Trim();

        // AHK は Send の中でキー名を {} で括る ("{Left}")。どちらの書き方も受ける。
        if (keyPart.StartsWith('{') && keyPart.EndsWith('}') && keyPart.Length > 2)
            keyPart = keyPart[1..^1].Trim();

        if (!KeySpec.TryParse(keyPart, out var key))
        {
            error = $"キー \"{keyPart}\" を解釈できません";
            return false;
        }

        combo = new Combo(mods, key, blind);
        return true;
    }

    public static ModGroup GroupOf(Vk vk) => vk switch
    {
        Vk.LControlKey or Vk.RControlKey or Vk.ControlKey => ModGroup.Ctrl,
        Vk.LShiftKey or Vk.RShiftKey or Vk.ShiftKey => ModGroup.Shift,
        Vk.LMenu or Vk.RMenu or Vk.Menu => ModGroup.Alt,
        Vk.LWin or Vk.RWin => ModGroup.Win,
        _ => ModGroup.None,
    };

    public override string ToString()
    {
        string m = "";
        if (Mods.HasFlag(ModGroup.Ctrl)) m += "^";
        if (Mods.HasFlag(ModGroup.Alt)) m += "!";
        if (Mods.HasFlag(ModGroup.Shift)) m += "+";
        if (Mods.HasFlag(ModGroup.Win)) m += "#";
        return (Blind ? "{Blind}" : "") + m + Key.Text;
    }
}

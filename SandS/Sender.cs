
using static SandS.Native;

namespace SandS;

/// <summary>
/// SendInput のラッパ。送るイベントには必ず InjectedTag を付けてフック側で読み飛ばせるようにする。
/// この層はフックのコールバックから呼ばれるので、ヒープ確保を一切しない。
/// </summary>
internal static unsafe class Sender
{
    public static void Key(in KeySpec key, bool down)
    {
        uint flags = down ? 0u : KEYEVENTF_KEYUP;
        if (key.Extended) flags |= KEYEVENTF_EXTENDEDKEY;

        ushort vk = key.Code;

        // sc 指定はスキャンコードで送る。仮想キーコードが配列や IME 状態で揺れるキー
        // (sc029 = 半角/全角 など) を、物理位置どおりに届けるため。
        if (key.ByScan)
        {
            flags |= KEYEVENTF_SCANCODE;
            vk = 0;
        }

        SendOne(vk, key.Scan, flags);
    }

    public static void Key(Vk vk, bool down)
    {
        uint scan = MapVirtualKeyW((uint)vk, MAPVK_VK_TO_VSC);
        uint flags = down ? 0u : KEYEVENTF_KEYUP;
        if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        SendOne((ushort)vk, (ushort)scan, flags);
    }

    /// <summary>フックが受け取った生のイベントを、そのまま送り直す。</summary>
    public static void Raw(in KBDLLHOOKSTRUCT info, bool down)
    {
        uint flags = down ? 0u : KEYEVENTF_KEYUP;
        if ((info.flags & LLKHF_EXTENDED) != 0) flags |= KEYEVENTF_EXTENDEDKEY;
        SendOne((ushort)info.vkCode, (ushort)info.scanCode, flags);
    }

    /// <summary>
    /// コンボを送る。physMask はいま物理的に押されている修飾キーのビットマスク。
    ///
    /// Blind でない場合、コンボに含まれない物理修飾キーを一時的に外してから送り、送り終えたら戻す
    /// (AHK の Send の既定と同じ)。これが無いと "!sc027 → ^F12" が Alt+Ctrl+F12 になってしまう。
    /// Blind の場合は物理修飾キーをそのまま残すので、"BackSpace & h → {Blind}Left" が
    /// Shift 押下中なら Shift+Left (選択) になる。
    /// </summary>
    /// <returns>
    /// 外したまま戻さなかった物理修飾キーのビットマスク (Alt/Win)。
    /// 呼び出し側は、これらの物理的な key up を握り潰す必要がある。
    /// </returns>
    public static byte SendCombo(Combo combo, byte physMask)
    {
        var physGroups = ModMask.GroupsOf(physMask);

        Vk* pressed = stackalloc Vk[4];
        int pressedCount = 0;

        // 1) コンボ側の修飾キーを先に押す。
        //    順序が重要。Alt を「単独で押して離した」と Windows に見せるとメニューモードに入り、
        //    後続のキーがメニューに食われて宛先に届かなくなる。先に何か押しておけば単独押しでなくなる。
        //    (ModKeys() を回すとイテレータを確保するので 4 系統べた書き)
        PressMod(ModGroup.Ctrl, Vk.LControlKey, combo, physGroups, pressed, ref pressedCount);
        PressMod(ModGroup.Shift, Vk.LShiftKey, combo, physGroups, pressed, ref pressedCount);
        PressMod(ModGroup.Alt, Vk.LMenu, combo, physGroups, pressed, ref pressedCount);
        PressMod(ModGroup.Win, Vk.LWin, combo, physGroups, pressed, ref pressedCount);

        // 2) コンボに含まれない物理修飾キーを外す
        Vk* released = stackalloc Vk[8];
        int releasedCount = 0;
        byte unrestored = 0;

        if (!combo.Blind && physMask != 0)
        {
            bool masked = pressedCount > 0;   // 1) で何か押していれば既にマスク済み

            for (int bit = 0; bit < 8; bit++)
            {
                if ((physMask & (1 << bit)) == 0) continue;
                var held = ModMask.Vks[bit];
                var group = Combo.GroupOf(held);
                if (combo.Mods.HasFlag(group)) continue;

                bool menuRisk = group is ModGroup.Alt or ModGroup.Win;
                if (menuRisk && !masked)
                {
                    // 単独の Alt/Win 押しを打ち消すためだけの無害な打鍵 (AHK と同じ手)
                    Key(Vk.LControlKey, down: true);
                    Key(Vk.LControlKey, down: false);
                    masked = true;
                }

                Key(held, down: false);

                // Alt/Win は押し直さない。押し直すと、そのあとユーザーが物理的に離した時点で
                // 「単独で押して離した」形になり、やはりメニューが開いてしまう。
                if (menuRisk) unrestored |= (byte)(1 << bit);
                else released[releasedCount++] = held;
            }
        }

        Key(combo.Key, down: true);
        Key(combo.Key, down: false);

        for (int i = pressedCount - 1; i >= 0; i--) Key(pressed[i], down: false);
        for (int i = releasedCount - 1; i >= 0; i--) Key(released[i], down: true);

        return unrestored;
    }

    private static void PressMod(ModGroup group, Vk vk, Combo combo, ModGroup physGroups,
                                 Vk* pressed, ref int count)
    {
        if (!combo.Mods.HasFlag(group)) return;
        // Blind のときは、その系統が既に物理的に押されているなら二重に押さない
        if (combo.Blind && physGroups.HasFlag(group)) return;
        Key(vk, down: true);
        pressed[count++] = vk;
    }

    private static void SendOne(ushort vk, ushort scan, uint flags)
    {
        INPUT input = default;
        input.type = INPUT_KEYBOARD;
        input.U.ki = new KEYBDINPUT
        {
            wVk = vk,
            wScan = scan,
            dwFlags = flags,
            time = 0,
            dwExtraInfo = InjectedTag,
        };
        SendInput(1, &input, sizeof(INPUT));
    }

    private static bool IsExtended(Vk vk) => vk
        is Vk.RControlKey or Vk.RMenu or Vk.LWin or Vk.RWin or Vk.Apps
        or Vk.Insert or Vk.Delete or Vk.Home or Vk.End
        or Vk.PageUp or Vk.PageDown
        or Vk.Left or Vk.Right or Vk.Up or Vk.Down
        or Vk.NumLock or Vk.Divide or Vk.PrintScreen;
}

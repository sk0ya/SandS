using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static SandS.Native;

namespace SandS;

internal sealed record Binding(KeySpec Key, Combo? Combo, string? Command);

internal sealed class CompiledPrefix
{
    public required KeySpec Key { get; init; }
    public Combo? Tap { get; init; }
    public Keys? HoldModifier { get; init; }
    /// <summary>件数が少ないので線形走査。Dictionary だとスキャンコード指定との併用が面倒なだけ。</summary>
    public Binding[] Map { get; init; } = [];

    public int TapTimeoutMs { get; init; }

    public Binding? Find(in KBDLLHOOKSTRUCT info)
    {
        foreach (var b in Map)
            if (b.Key.Matches(in info)) return b;
        return null;
    }
}

internal sealed record CompiledHotkey(ModGroup Mods, Binding Binding);

/// <summary>
/// 低レベルキーボードフックの本体。3 つの機能を 1 つの状態機械で扱う。
///
///   1. プレフィックスキー — Space/BackSpace/Enter のように、単打では本来のキー、
///      押しながらだと別マップ。SandS は「Map に無いキーすべてに Shift を足す」特殊形。
///   2. ホットキー — "!sc027 → ^F12" のような修飾キー付きの置き換え。
///   3. 単純リマップ — "sc070 → sc029"。
/// </summary>
internal sealed class Engine : IDisposable
{
    private readonly List<CompiledPrefix> _prefixes = [];
    private readonly List<CompiledHotkey> _hotkeys = [];

    // GC でデリゲートが回収されるとフックが死ぬので、必ずフィールドで保持する
    private readonly HookProc _proc;
    private IntPtr _hook = IntPtr.Zero;
    private SynchronizationContext? _ui;

    private CompiledPrefix? _active;
    private bool _activeUsed;
    private long _activeDownAt;

    /// <summary>注入した HoldModifier。押した本体を持っておかないと確実に離せない。</summary>
    private Keys? _holdModKey;

    /// <summary>いま物理的に押されている修飾キーのビットマスク。Blind でない送出で外して戻すために必要。</summary>
    private byte _physMask;

    /// <summary>
    /// down を握り潰したキーのスキャンコード。対応する up も握り潰さないと、宛先に up だけ届く。
    /// 同時に押されうる数はたかが知れているので、固定長の線形走査で十分 (確保もハッシュ計算もしない)。
    /// </summary>
    private readonly ushort[] _swallowUp = new ushort[16];
    private int _swallowCount;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>"@reload" などのコマンド。フック内で実行すると危険なので UI スレッドへ投げる。</summary>
    public Action<string>? OnCommand { get; set; }

    private bool _enabled = true;

    /// <summary>
    /// 無効化する瞬間に必ず後始末する。プレフィックスを押している最中 (= 修飾キー注入済み) に
    /// 無効化されると、その修飾キーが押しっぱなしのまま誰も離さなくなるため。
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!value) ResetState();
        }
    }

    public Engine(Config cfg, List<string> problems)
    {
        _proc = HookCallback;
        Compile(cfg, problems);
    }

    private void Compile(Config cfg, List<string> problems)
    {
        foreach (var p in cfg.PrefixKeys)
        {
            if (!KeySpec.TryParse(p.Key, out var key))
            {
                problems.Add($"PrefixKeys: キー \"{p.Key}\" を解釈できません。この定義を飛ばします。");
                continue;
            }

            Combo? tap = null;
            if (p.Tap is not null && !Combo.TryParse(p.Tap, out tap, out var tapErr))
                problems.Add($"{p.Key}.Tap \"{p.Tap}\": {tapErr}");

            Keys? holdMod = null;
            if (p.HoldModifier is not null)
            {
                if (KeySpec.TryParse(p.HoldModifier, out var hm)) holdMod = (Keys)hm.Vk;
                else problems.Add($"{p.Key}.HoldModifier \"{p.HoldModifier}\" を解釈できません。");
            }

            var map = new List<Binding>();
            foreach (var (k, v) in p.Map)
            {
                if (!KeySpec.TryParse(k, out var mk))
                {
                    problems.Add($"{p.Key} & {k}: キー名を解釈できません。");
                    continue;
                }
                if (!TryBinding(mk, v, out var b, out var err)) { problems.Add($"{p.Key} & {k}: {err}"); continue; }
                map.Add(b!);
            }

            _prefixes.Add(new CompiledPrefix
            {
                Key = key,
                Tap = tap,
                HoldModifier = holdMod,
                Map = [.. map],
                TapTimeoutMs = Math.Max(0, p.TapTimeoutMs),
            });
        }

        foreach (var (trigger, action) in cfg.Hotkeys)
        {
            if (!Combo.TryParse(trigger, out var t, out var tErr) || t is null)
            {
                problems.Add($"Hotkeys \"{trigger}\": {tErr}");
                continue;
            }
            if (!TryBinding(t.Key, action, out var b, out var err)) { problems.Add($"Hotkeys \"{trigger}\": {err}"); continue; }
            _hotkeys.Add(new CompiledHotkey(t.Mods, b!));
        }
    }

    private static bool TryBinding(KeySpec key, string action, out Binding? binding, out string? error)
    {
        binding = null;
        error = null;

        if (action.StartsWith('@'))
        {
            binding = new Binding(key, null, action.ToLowerInvariant());
            return true;
        }

        if (!Combo.TryParse(action, out var c, out error)) return false;
        binding = new Binding(key, c, null);
        return true;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _ui = SynchronizationContext.Current;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        DebugLog.Write($"Install: hook=0x{_hook:X} prefixes={_prefixes.Count} hotkeys={_hotkeys.Count}");
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException(
                $"キーボードフックを設定できませんでした (Win32 error {Marshal.GetLastWin32Error()})。");
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        ResetState();
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private void ResetState()
    {
        ReleaseHoldModifier();
        _active = null;
        _activeUsed = false;
        _swallowCount = 0;
    }

    private bool SwallowRemove(ushort scan)
    {
        for (int i = 0; i < _swallowCount; i++)
        {
            if (_swallowUp[i] != scan) continue;
            _swallowUp[i] = _swallowUp[--_swallowCount];
            return true;
        }
        return false;
    }

    private void SwallowAdd(ushort scan)
    {
        for (int i = 0; i < _swallowCount; i++)
            if (_swallowUp[i] == scan) return;          // オートリピート
        if (_swallowCount < _swallowUp.Length) _swallowUp[_swallowCount++] = scan;
    }

    private unsafe IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

        // Marshal.PtrToStructure だと打鍵ごとにマーシャリングが走る。
        // KBDLLHOOKSTRUCT は blittable なので直接読む。
        ref readonly var info = ref *(KBDLLHOOKSTRUCT*)lParam;

        // 打鍵ごとに走る。ログ無効時に文字列を組み立てないよう必ず Enabled で括る。
        if (DebugLog.Enabled)
            DebugLog.Write($"hook msg=0x{(int)wParam:X} vk={(Keys)info.vkCode} scan={info.scanCode} extra=0x{(ulong)info.dwExtraInfo:X}");

        // 自分が送ったイベントは触らない (無限ループ防止)
        if (info.dwExtraInfo == InjectedTag) return CallNextHookEx(_hook, nCode, wParam, lParam);
        if (!_enabled) return CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = (int)wParam;
        bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;

        // 物理修飾キーの追跡は握り潰しより先。ここを取りこぼすと Blind の判定が狂う。
        int bit = ModMask.BitOf((Keys)info.vkCode);
        if (bit >= 0)
        {
            if (isDown) _physMask |= (byte)(1 << bit);
            else if (isUp) _physMask &= (byte)~(1 << bit);
        }

        if (isUp) return OnUp(in info, nCode, wParam, lParam);
        if (isDown) return OnDown(in info, nCode, wParam, lParam);
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private IntPtr OnUp(in KBDLLHOOKSTRUCT info, int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (_active is not null && _active.Key.Matches(info))
            return OnPrefixUp();

        if (SwallowRemove((ushort)info.scanCode)) return (IntPtr)1;

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private IntPtr OnDown(in KBDLLHOOKSTRUCT info, int nCode, IntPtr wParam, IntPtr lParam)
    {
        var vk = (Keys)info.vkCode;

        if (_active is not null)
        {
            // プレフィックス自身のオートリピート
            if (_active.Key.Matches(info)) return (IntPtr)1;

            var bound = _active.Find(in info);
            if (bound is not null)
            {
                _activeUsed = true;
                SwallowAdd((ushort)info.scanCode);
                Execute(bound);
                return (IntPtr)1;
            }

            // 修飾キー自体はプレフィックスを「使った」に数えない。
            // これで Space→Ctrl→C が Ctrl+Shift+C になり、Space+Ctrl だけなら Space が出る。
            if (ModMask.BitOf(vk) >= 0)
                return CallNextHookEx(_hook, nCode, wParam, lParam);

            if (_active.HoldModifier is { } hold)
            {
                _activeUsed = true;
                if (_holdModKey is null) { Sender.Key(hold, down: true); _holdModKey = hold; }
                // 修飾キーより後に届くことを保証するため、元のキーも送り直す
                Sender.Raw(in info, down: true);
                return (IntPtr)1;
            }

            // Map にも無く HoldModifier も無い → 素通しするが、単打の扱いは取り消す
            _activeUsed = true;
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // 修飾キーが押されている間はプレフィックスにしない。
        // これで Shift+Enter / Ctrl+Enter / Ctrl+Space が普通に通る。
        if (_physMask == 0)
        {
            foreach (var p in _prefixes)
            {
                if (!p.Key.Matches(in info)) continue;
                _active = p;
                _activeUsed = false;
                _holdModKey = null;
                _activeDownAt = _clock.ElapsedMilliseconds;
                return (IntPtr)1;
            }
        }

        var mods = ModMask.GroupsOf(_physMask);
        foreach (var hk in _hotkeys)
        {
            if (hk.Mods != mods || !hk.Binding.Key.Matches(in info)) continue;
            SwallowAdd((ushort)info.scanCode);
            Execute(hk.Binding);
            return (IntPtr)1;
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private IntPtr OnPrefixUp()
    {
        var p = _active!;
        bool used = _activeUsed;

        ReleaseHoldModifier();
        _active = null;
        _activeUsed = false;

        if (!used && p.Tap is not null)
        {
            long held = _clock.ElapsedMilliseconds - _activeDownAt;
            if (p.TapTimeoutMs <= 0 || held <= p.TapTimeoutMs)
                SendCombo(p.Tap);
        }

        return (IntPtr)1;
    }

    private void Execute(Binding b)
    {
        if (b.Command is not null)
        {
            var cmd = b.Command;
            // フックの中で設定再読込やダイアログを走らせない (LowLevelHooksTimeout で殺される)
            _ui?.Post(_ => OnCommand?.Invoke(cmd), null);
            return;
        }
        if (b.Combo is not null)
            SendCombo(b.Combo);
    }

    private void SendCombo(Combo combo)
    {
        byte unrestored = Sender.SendCombo(combo, _physMask);
        if (unrestored == 0) return;

        // Sender が外したまま戻さなかった Alt/Win。OS から見れば既に離れているので、
        // ユーザーが実際に指を離したときの key up は捨てる。
        for (int bit = 0; bit < 8; bit++)
        {
            if ((unrestored & (1 << bit)) == 0) continue;
            _physMask &= (byte)~(1 << bit);
            SwallowAdd((ushort)MapVirtualKey((uint)ModMask.Vks[bit], MAPVK_VK_TO_VSC));
        }
    }

    private void ReleaseHoldModifier()
    {
        if (_holdModKey is not { } hold) return;
        Sender.Key(hold, down: false);
        _holdModKey = null;
    }

    public void Dispose() => Uninstall();
}

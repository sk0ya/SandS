using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static SandS.Native;

namespace SandS;

/// <summary>
/// 隠しウィンドウ + タスクトレイ + メッセージループ。
/// WinForms を参照するとトリミングと NativeAOT が SDK に拒否される (NETSDK1175) ので、
/// すべて素の Win32 で組んでいる。
/// </summary>
internal sealed unsafe class TrayApp : IDisposable
{
    private const uint TrayId = 1;

    private const uint CmdEnabled = 1;
    private const uint CmdStartup = 2;
    private const uint CmdReload = 3;
    private const uint CmdEdit = 4;
    private const uint CmdExit = 5;

    private readonly string _cfgPath;
    private IntPtr _hwnd;
    private IntPtr _iconOn, _iconOff;
    private Engine? _engine;
    private bool _enabled = true;

    /// <summary>
    /// メニューを開くたびに schtasks を起動すると重いので、状態は持っておいて
    /// 起動時と変更時にだけ調べ直す。
    /// </summary>
    private StartupMode _startup;

    // GC に回収されるとコールバックが死ぬので、必ずフィールドで保持する
    private readonly WndProc _wndProc;
    private IntPtr _wndProcPtr;

    /// <summary>フックから届いたコマンド。フック内で実行すると危険なので、ここを経由して UI スレッドで処理する。</summary>
    private readonly ConcurrentQueue<string> _commands = new();

    public TrayApp(string cfgPath)
    {
        _cfgPath = cfgPath;
        _wndProc = WindowProc;
    }

    public bool Start()
    {
        if (!CreateHiddenWindow()) return false;

        _iconOn = IconFactory.Create(true);
        _iconOff = IconFactory.Create(false);

        if (!LoadEngine(initial: true)) return false;

        _startup = Startup.Current();
        AddTrayIcon();
        return true;
    }

    public int Run()
    {
        while (true)
        {
            int r = GetMessageW(out var msg, IntPtr.Zero, 0, 0);
            if (r == 0) return (int)msg.wParam;   // WM_QUIT
            if (r == -1) return 1;
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    // ---- ウィンドウ --------------------------------------------------------

    private bool CreateHiddenWindow()
    {
        _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
        IntPtr hInst = GetModuleHandleW(IntPtr.Zero);

        fixed (char* clsName = "SandS.MessageWindow")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = _wndProcPtr,
                hInstance = hInst,
                lpszClassName = (IntPtr)clsName,
            };

            if (RegisterClassExW(ref wc) == 0)
            {
                Error($"ウィンドウクラスを登録できませんでした (Win32 error {Marshal.GetLastWin32Error()})。");
                return false;
            }

            // メッセージ専用ウィンドウ (HWND_MESSAGE) にはしない。
            // TrackPopupMenu はフォアグラウンドウィンドウを必要とするため。
            _hwnd = CreateWindowExW(0, (IntPtr)clsName, IntPtr.Zero, 0,
                                    0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        }

        if (_hwnd == IntPtr.Zero)
        {
            Error($"ウィンドウを作成できませんでした (Win32 error {Marshal.GetLastWin32Error()})。");
            return false;
        }
        return true;
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TRAY:
                switch ((int)lParam)
                {
                    case WM_RBUTTONUP: ShowMenu(); return IntPtr.Zero;
                    case WM_LBUTTONDBLCLK: SetEnabled(!_enabled); return IntPtr.Zero;
                }
                return IntPtr.Zero;

            case WM_RUN_COMMAND:
                while (_commands.TryDequeue(out var cmd)) HandleCommand(cmd);
                return IntPtr.Zero;

            case WM_CLOSE:
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ---- トレイ ------------------------------------------------------------

    private void AddTrayIcon()
    {
        var nid = NewNid(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        Shell_NotifyIconW(NIM_ADD, ref nid);
    }

    private NOTIFYICONDATAW NewNid(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = TrayId,
        uFlags = flags,
        uCallbackMessage = WM_TRAY,
        hIcon = _enabled ? _iconOn : _iconOff,
        // 昇格していないと管理者権限のウィンドウ上でだけ効かない。
        // 気づける場所が無いと原因不明の不具合に見えるので出しておく。
        szTip = $"SandS — {(_enabled ? "有効" : "停止中")}"
                + (Startup.IsElevated() ? " (管理者)" : ""),
        szInfo = "",
        szInfoTitle = "",
    };

    private void UpdateTray()
    {
        var nid = NewNid(NIF_ICON | NIF_TIP);
        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    private void Balloon(string text)
    {
        var nid = NewNid(NIF_INFO);
        nid.szInfo = text;
        nid.szInfoTitle = "SandS";
        nid.uTimeoutOrVersion = 3000;
        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING | (_enabled ? MF_CHECKED : 0), (UIntPtr)CmdEnabled, "有効(&E)");

        // 管理者権限のウィンドウ上で効くかどうかが、ここで分かるようにしておく。
        // 効かないときに理由が分からないのが一番困るため。
        string startupLabel = _startup switch
        {
            StartupMode.Task => "Windows 起動時に開始(&S) — 管理者",
            StartupMode.Registry => "Windows 起動時に開始(&S) — 通常",
            _ => "Windows 起動時に開始(&S)",
        };
        AppendMenuW(menu, MF_STRING | (_startup != StartupMode.None ? MF_CHECKED : 0), (UIntPtr)CmdStartup, startupLabel);
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, "");
        AppendMenuW(menu, MF_STRING, (UIntPtr)CmdReload, "設定を再読み込み(&R)");
        AppendMenuW(menu, MF_STRING, (UIntPtr)CmdEdit, "設定ファイルを編集(&O)");
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, "");
        AppendMenuW(menu, MF_STRING, (UIntPtr)CmdExit, "終了(&X)");

        GetCursorPos(out var pt);
        // これが無いとメニューの外をクリックしても閉じない (Win32 の既知の作法)
        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.x, pt.y, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);

        switch ((uint)cmd)
        {
            case CmdEnabled: SetEnabled(!_enabled); break;
            case CmdStartup: ToggleStartup(); break;
            case CmdReload: Reload(); break;
            case CmdEdit: EditConfig(); break;
            case CmdExit: DestroyWindow(_hwnd); break;
        }
    }

    private void SetEnabled(bool on)
    {
        _enabled = on;
        if (_engine is not null) _engine.Enabled = on;
        UpdateTray();
    }

    // ---- 設定 --------------------------------------------------------------

    private bool LoadEngine(bool initial)
    {
        var cfg = Config.Load(_cfgPath, out var problems);
        var engine = new Engine(cfg, problems);
        engine.OnCommand = QueueCommand;

        try
        {
            engine.Install();
        }
        catch (Exception ex)
        {
            if (initial) Error(ex.Message);
            else Warn($"設定を再読み込みできませんでした。元の設定のまま続けます。\n\n{ex.Message}");
            return false;
        }

        // 新しいフックを張ってから古い方を外す。逆にすると、その隙間の打鍵が素通しになる。
        _engine?.Dispose();
        _engine = engine;
        _engine.Enabled = _enabled;

        if (problems.Count > 0)
            Warn("設定に解釈できない箇所があります。該当分だけ無効にして続けます。\n\n" + string.Join("\n", problems));

        return true;
    }

    /// <summary>フックのコールバックから呼ばれる。ここでは重い処理をせず、UI スレッドへ回すだけ。</summary>
    private void QueueCommand(string cmd)
    {
        _commands.Enqueue(cmd);
        PostMessageW(_hwnd, WM_RUN_COMMAND, IntPtr.Zero, IntPtr.Zero);
    }

    private void HandleCommand(string cmd)
    {
        switch (cmd)
        {
            case "@reload": Reload(); break;
            case "@edit": EditConfig(); break;
            case "@toggle": SetEnabled(!_enabled); break;
            case "@exit": DestroyWindow(_hwnd); break;
            default: Balloon($"知らないコマンドです: {cmd}"); break;
        }
    }

    private void Reload()
    {
        if (LoadEngine(initial: false))
        {
            UpdateTray();
            Balloon("設定を再読み込みしました。");
        }
    }

    private void EditConfig()
    {
        if (!File.Exists(_cfgPath))
        {
            try { Config.Default().Save(_cfgPath); }
            catch (Exception ex) { Warn($"設定ファイルを作成できませんでした。\n{_cfgPath}\n\n{ex.Message}"); return; }
        }

        IntPtr r = ShellExecuteW(IntPtr.Zero, "open", _cfgPath, null, null, SW_SHOWNORMAL);
        if ((long)r <= 32)
            Warn($"設定ファイルを開けませんでした。\n{_cfgPath}");
        else
            Balloon("保存したら「設定を再読み込み」(または BackSpace+R) で反映されます。");
    }

    // ---- スタートアップ ----------------------------------------------------

    private void ToggleStartup()
    {
        string? note;
        if (_startup == StartupMode.None) _startup = Startup.Enable(out note);
        else { Startup.Disable(out note); _startup = Startup.Current(); }

        if (note is not null) Warn(note);
    }

    // ---- ダイアログ --------------------------------------------------------

    public static void Error(string msg) => MessageBoxW(IntPtr.Zero, msg, "SandS", MB_OK | MB_ICONERROR);
    public static void Warn(string msg) => MessageBoxW(IntPtr.Zero, msg, "SandS", MB_OK | MB_ICONWARNING);
    public static void Info(string msg) => MessageBoxW(IntPtr.Zero, msg, "SandS", MB_OK | MB_ICONINFORMATION);

    public void Dispose()
    {
        var nid = NewNid(0);
        Shell_NotifyIconW(NIM_DELETE, ref nid);

        _engine?.Dispose();
        if (_iconOn != IntPtr.Zero) DestroyIcon(_iconOn);
        if (_iconOff != IntPtr.Zero) DestroyIcon(_iconOff);
    }
}

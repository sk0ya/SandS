using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using SandS;

namespace E2E;

/// <summary>
/// SandS.exe を別プロセスで常駐させ、物理キー相当の入力 (dwExtraInfo = 0) を SendInput で
/// 流し込んで、実際にコントロールへ届いたものを観測する。
///
/// 実設定の Win+1 / Alt+F4 / Ctrl+Win+F4 などはテスト中に発火させるとウィンドウが飛んで
/// テスト自体が壊れるので、ここでは無害なキーだけを割り当てたテスト用設定を使う。
/// 実設定は Part 1 でコンパイル検証だけ行う。
/// </summary>
internal static class Program
{
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint uCode, uint uMapType);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    static void Send(ushort vk, ushort scan, uint flags)
    {
        var i = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = UIntPtr.Zero }
            }
        };
        SendInput(1, [i], Marshal.SizeOf<INPUT>());
    }

    static void Key(Keys vk, bool down) =>
        Send((ushort)vk, (ushort)MapVirtualKey((uint)vk, 0), down ? 0u : KEYEVENTF_KEYUP);

    /// <summary>スキャンコードで叩く。sc027 / sc070 のトリガ検証用。</summary>
    static void KeyScan(ushort scan, bool down) =>
        Send(0, scan, KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP));

    static async Task Tap(Keys k) { Key(k, true); await Task.Delay(30); Key(k, false); await Task.Delay(30); }
    static async Task TapScan(ushort sc) { KeyScan(sc, true); await Task.Delay(30); KeyScan(sc, false); await Task.Delay(30); }

    static readonly StringBuilder Log = new();
    static int _pass, _fail;

    static void Check(string name, bool ok, string expected, string actual)
    {
        if (ok) _pass++; else _fail++;
        Log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}\n        expected={expected}  actual={actual}");
    }

    static string Q(string s) => "\"" + s.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

    // ---- 観測 ----------------------------------------------------------------

    record Hit(Keys Code, Keys Mods);
    static readonly List<Hit> Hits = [];

    static string HitsText() => Hits.Count == 0
        ? "(何も来なかった)"
        : string.Join(", ", Hits.Select(h => h.Mods == Keys.None ? $"{h.Code}" : $"{h.Mods}+{h.Code}"));

    /// <summary>テスト用設定。実設定と違い、発火しても害のないキーだけを割り当てる。</summary>
    static string TestConfigJson() => JsonSerializer.Serialize(new
    {
        PrefixKeys = new object[]
        {
            new
            {
                Key = "Space",
                Tap = "Space",
                HoldModifier = "LShift",   // SandS
                Map = new Dictionary<string, string>(),
                TapTimeoutMs = 0,
            },
            new
            {
                Key = "BackSpace",
                Tap = "BackSpace",
                HoldModifier = (string?)null,
                Map = new Dictionary<string, string>
                {
                    ["h"] = "{Blind}Left",
                    ["j"] = "{Blind}Down",
                    ["k"] = "{Blind}Up",
                    ["l"] = "{Blind}Right",
                    ["sc027"] = "BackSpace",   // 実設定と同じ物理位置トリガ
                    ["sc028"] = "Delete",
                },
                TapTimeoutMs = 0,
            },
            new
            {
                Key = "Enter",
                Tap = "Enter",
                HoldModifier = (string?)null,
                Map = new Dictionary<string, string>
                {
                    // 実設定は Win+1 だが、発火するとタスクバー切替が起きてテストが壊れる。
                    // 修飾キー付きコンボの送出経路は F13/F14 で確認する。
                    ["1"] = "^F13",
                    ["h"] = "+F14",
                },
                TapTimeoutMs = 0,
            },
        },
        Hotkeys = new Dictionary<string, string>
        {
            ["!h"] = "{Blind}Left",   // LAlt & H → Alt+Left
            ["!sc027"] = "^F15",      // 実設定の "!sc027 → ^F12" と同じ形。Alt を外せるかの検証
            ["sc070"] = "F16",        // 実設定は sc029 (半角/全角) だが IME を切り替えてしまうので代替
        },
    }, new JsonSerializerOptions { WriteIndented = true });

    [STAThread]
    static void Main()
    {
        // ---- Part 1: 実設定 (既定値) がそのままコンパイルできるか ----
        // Win+1 / Alt+F4 などは実挙動を試せないので、せめてキー名とコンボが
        // 全て解釈できることをここで担保する。
        var problems = new List<string>();
        var real = Config.Default();
        _ = new Engine(real, problems);
        Check("既定設定 (元の AHK スクリプト) が警告なしでコンパイルできる",
              problems.Count == 0, "問題なし", problems.Count == 0 ? "問題なし" : string.Join(" / ", problems));

        int mapped = real.PrefixKeys.Sum(p => p.Map.Count) + real.Hotkeys.Count;
        Check("既定設定の割り当て数", mapped >= 35, ">=35", mapped.ToString());

        // トレイアイコンは System.Drawing を捨てて GDI へのピクセル直描きに置き換えたので、
        // 失敗しても「無地のアイコンが出る」だけで気づけない。ハンドルが取れるかを見ておく。
        IntPtr on = IconFactory.Create(true);
        IntPtr off = IconFactory.Create(false);
        Check("トレイアイコンを GDI で生成できる", on != IntPtr.Zero && off != IntPtr.Zero,
              "有効/停止の両方でハンドルが取れる", $"on=0x{on:X} off=0x{off:X}");

        TestStartup();

        // ---- Part 2: テスト用設定で実挙動 ----
        string cfgPath = Path.Combine(Path.GetTempPath(), "sands.e2e.config.json");
        File.WriteAllText(cfgPath, TestConfigJson());

        // NativeAOT で publish した exe が本番の成果物なので、あればそちらを優先して検証する。
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\SandS\bin\Release\net9.0-windows\win-x64"));
        string exe = Path.Combine(root, "publish", "SandS.exe");
        if (!File.Exists(exe)) exe = Path.Combine(root, "SandS.exe");
        if (!File.Exists(exe))
        {
            Log.AppendLine($"FAIL  SandS.exe が見つからない: {root}");
            _fail++;
            Finish();
            return;
        }
        Log.AppendLine($"対象: {exe}\n");

        var sands = Process.Start(new ProcessStartInfo(exe, $"--config \"{cfgPath}\"") { UseShellExecute = true });
        Thread.Sleep(1500);

        ApplicationConfiguration.Initialize();
        var box = new TextBox { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 12) };
        var form = new Form { Text = "SandS E2E", Width = 640, Height = 320, TopMost = true };
        form.Controls.Add(box);
        box.KeyDown += (_, e) => Hits.Add(new Hit(e.KeyCode, e.Modifiers));

        form.Shown += async (_, _) =>
        {
            try
            {
                SetForegroundWindow(form.Handle);
                box.Focus();
                await Task.Delay(600);
                await RunLive(box);
            }
            finally
            {
                try { sands?.CloseMainWindow(); sands?.Kill(); } catch { }
                Finish();
                Application.Exit();
            }
        };

        Application.Run(form);
    }

    /// <summary>
    /// スタートアップ登録。ユーザーのレジストリを触るので、必ず元の状態へ戻す。
    /// 既に登録済みの環境では、その設定を壊さないよう検証を飛ばす。
    /// </summary>
    static void TestStartup()
    {
        // タスク XML が壊れていても schtasks のエラーになるだけで原因が分かりにくいので、
        // 少なくとも XML として妥当であることは見ておく。
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(Startup.TaskXml(@"C:\dummy\SandS.exe"));
            var ns = (System.Xml.Linq.XNamespace)"http://schemas.microsoft.com/windows/2004/02/mit/task";
            string? runLevel = doc.Descendants(ns + "RunLevel").FirstOrDefault()?.Value;
            string? command = doc.Descendants(ns + "Command").FirstOrDefault()?.Value;
            Check("タスク XML が妥当で、最上位の特権で起動する指定になっている",
                  runLevel == "HighestAvailable" && command == @"C:\dummy\SandS.exe",
                  "RunLevel=HighestAvailable, Command=exe パス", $"RunLevel={runLevel}, Command={command}");
        }
        catch (Exception ex)
        {
            Check("タスク XML が妥当で、最上位の特権で起動する指定になっている", false, "XML として解析できる", ex.Message);
        }

        var before = Startup.Current();
        if (before != StartupMode.None)
        {
            Log.AppendLine($"SKIP  スタートアップ登録の実地検証 (既に {before} で登録済みなので触らない)");
            return;
        }

        bool elevated = Startup.IsElevated();
        try
        {
            var mode = Startup.Enable(out _);
            // 非昇格ならタスクは作れないので HKCU\Run になるはず
            var expected = elevated ? StartupMode.Task : StartupMode.Registry;
            Check($"スタートアップ登録 (昇格={elevated})", mode == expected, expected.ToString(), mode.ToString());
            Check("登録後に Current() が拾える", Startup.Current() == expected, expected.ToString(), Startup.Current().ToString());
        }
        finally
        {
            Startup.Disable(out _);
        }

        Check("解除するとレジストリもタスクも残らない", Startup.Current() == StartupMode.None,
              "None", Startup.Current().ToString());
    }

    static async Task Reset(TextBox box)
    {
        box.Clear();
        Hits.Clear();
        box.Focus();
        await Task.Delay(120);
    }

    static async Task RunLive(TextBox box)
    {
        // --- SandS (回帰確認) ---
        await Reset(box);
        await Tap(Keys.Space);
        await Task.Delay(300);
        Check("Space 単打 → スペース", box.Text == " ", Q(" "), Q(box.Text));

        await Reset(box);
        Key(Keys.Space, true); await Task.Delay(40);
        await Tap(Keys.A);
        Key(Keys.Space, false); await Task.Delay(300);
        Check("Space+A → 大文字 A", box.Text == "A", Q("A"), Q(box.Text));

        // --- BackSpace プレフィックス ---
        await Reset(box);
        box.Text = "abc"; box.SelectionStart = 3; await Task.Delay(80);
        Hits.Clear();
        await Tap(Keys.Back);
        await Task.Delay(300);
        Check("BackSpace 単打 → BackSpace", box.Text == "ab", Q("ab"), Q(box.Text));

        await Reset(box);
        Key(Keys.Back, true); await Task.Delay(40);
        await Tap(Keys.H);
        Key(Keys.Back, false); await Task.Delay(300);
        Check("BackSpace+H → Left", Hits.Count == 1 && Hits[0] == new Hit(Keys.Left, Keys.None),
              "Left", HitsText());

        await Reset(box);
        Key(Keys.Back, true); await Task.Delay(40);
        await Tap(Keys.J); await Tap(Keys.K); await Tap(Keys.L);
        Key(Keys.Back, false); await Task.Delay(300);
        Check("BackSpace+J/K/L → Down/Up/Right",
              Hits.Count == 3 && Hits[0].Code == Keys.Down && Hits[1].Code == Keys.Up && Hits[2].Code == Keys.Right,
              "Down, Up, Right", HitsText());

        // {Blind} — Shift を押したままなら Shift+Left (選択) になるべき
        await Reset(box);
        box.Text = "abc"; box.SelectionStart = 3; await Task.Delay(80);
        Hits.Clear();
        Key(Keys.Back, true); await Task.Delay(40);
        Key(Keys.ShiftKey, true); await Task.Delay(40);
        await Tap(Keys.H);
        Key(Keys.ShiftKey, false); await Task.Delay(40);
        Key(Keys.Back, false); await Task.Delay(300);
        Check("{Blind}: Shift+BackSpace+H → Shift+Left (選択される)",
              box.SelectionLength == 1, "sel:1", $"sel:{box.SelectionLength} hits:{HitsText()}");

        // スキャンコードトリガ
        await Reset(box);
        box.Text = "abc"; box.SelectionStart = 3; await Task.Delay(80);
        Hits.Clear();
        Key(Keys.Back, true); await Task.Delay(40);
        await TapScan(0x27);
        Key(Keys.Back, false); await Task.Delay(300);
        Check("BackSpace+sc027 → BackSpace", box.Text == "ab", Q("ab"), Q(box.Text));

        await Reset(box);
        box.Text = "abc"; box.SelectionStart = 0; await Task.Delay(80);
        Hits.Clear();
        Key(Keys.Back, true); await Task.Delay(40);
        await TapScan(0x28);
        Key(Keys.Back, false); await Task.Delay(300);
        Check("BackSpace+sc028 → Delete", box.Text == "bc", Q("bc"), Q(box.Text));

        // --- Enter プレフィックス ---
        await Reset(box);
        await Tap(Keys.Enter);
        await Task.Delay(300);
        Check("Enter 単打 → 改行", box.Text == "\r\n", Q("\r\n"), Q(box.Text));

        await Reset(box);
        Key(Keys.ShiftKey, true); await Task.Delay(40);
        await Tap(Keys.Enter);
        Key(Keys.ShiftKey, false); await Task.Delay(300);
        Check("Shift+Enter → 改行 (プレフィックスにしない)", box.Text == "\r\n", Q("\r\n"), Q(box.Text));

        await Reset(box);
        Key(Keys.ControlKey, true); await Task.Delay(40);
        await Tap(Keys.Enter);
        Key(Keys.ControlKey, false); await Task.Delay(300);
        Check("Ctrl+Enter が Ctrl+Enter として届く",
              Hits.Any(h => h.Code == Keys.Return && h.Mods == Keys.Control),
              "Control+Return", HitsText());

        await Reset(box);
        Key(Keys.Enter, true); await Task.Delay(40);
        await Tap(Keys.D1);
        Key(Keys.Enter, false); await Task.Delay(300);
        // 注入した Ctrl 自身の KeyDown も届くので、狙いのキーが含まれるかで見る
        Check("Enter+1 → Ctrl+F13 (修飾キー付きコンボの送出)",
              Hits.Any(h => h.Code == Keys.F13 && h.Mods == Keys.Control), "Control+F13 を含む", HitsText());

        await Reset(box);
        Key(Keys.Enter, true); await Task.Delay(40);
        await Tap(Keys.H);
        Key(Keys.Enter, false); await Task.Delay(300);
        Check("Enter+H → Shift+F14", Hits.Any(h => h.Code == Keys.F14 && h.Mods == Keys.Shift),
              "Shift+F14 を含む", HitsText());

        // --- ホットキー ---
        await Reset(box);
        Key(Keys.LMenu, true); await Task.Delay(40);
        await Tap(Keys.H);
        Key(Keys.LMenu, false); await Task.Delay(300);
        Check("Alt+H → Alt+Left ({Blind} で Alt が残る)",
              Hits.Any(h => h.Code == Keys.Left && h.Mods == Keys.Alt), "Alt+Left", HitsText());

        // 非 Blind の要: Alt を外してから Ctrl+F15 を送れているか
        await Reset(box);
        Key(Keys.LMenu, true); await Task.Delay(40);
        await TapScan(0x27);
        Key(Keys.LMenu, false); await Task.Delay(300);
        Check("Alt+sc027 → Ctrl+F15 (Alt が外れて Ctrl だけになる)",
              Hits.Any(h => h.Code == Keys.F15 && h.Mods == Keys.Control), "Control+F15", HitsText());

        // --- 単純リマップ ---
        await Reset(box);
        await TapScan(0x70);
        await Task.Delay(300);
        Check("sc070 → F16 (単純リマップ)",
              Hits.Any(h => h.Code == Keys.F16 && h.Mods == Keys.None), "F16", HitsText());

        // --- 素通し回帰 ---
        await Reset(box);
        await Tap(Keys.A); await Tap(Keys.B);
        await Task.Delay(300);
        Check("普通のタイプが壊れていない", box.Text == "ab", Q("ab"), Q(box.Text));
    }

    static void Finish()
    {
        Log.AppendLine($"\n==== {_pass} passed, {_fail} failed ====");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "results.txt"), Log.ToString());
    }
}

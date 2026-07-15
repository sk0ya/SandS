using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SandS;

internal sealed class TrayApp : ApplicationContext
{
    private const string StartupRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "SandS";

    private readonly string _cfgPath;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _startupItem;

    private Engine? _engine;

    // 有効/無効でしか変わらないので、都度描かずに使い回す
    private static readonly Icon IconOn = BuildIcon(true);
    private static readonly Icon IconOff = BuildIcon(false);

    public TrayApp(string cfgPath)
    {
        _cfgPath = cfgPath;

        _enabledItem = new ToolStripMenuItem("有効 (&E)") { Checked = true, CheckOnClick = true };
        _enabledItem.CheckedChanged += (_, _) =>
        {
            if (_engine is not null) _engine.Enabled = _enabledItem.Checked;
            UpdateTrayLook();
        };

        _startupItem = new ToolStripMenuItem("Windows 起動時に開始 (&S)")
        {
            CheckOnClick = true,
            Checked = IsStartupRegistered(),
        };
        _startupItem.CheckedChanged += (_, _) => SetStartup(_startupItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("設定を再読み込み (&R)", null, (_, _) => Reload());
        menu.Items.Add("設定ファイルを編集 (&O)", null, (_, _) => EditConfig());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了 (&X)", null, (_, _) => ExitApp());

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Icon = IconOn };
        _tray.DoubleClick += (_, _) => _enabledItem.Checked = !_enabledItem.Checked;
    }

    /// <summary>設定を読んでフックを張る。失敗したら false。</summary>
    public bool Start()
    {
        var cfg = Config.Load(_cfgPath, out var problems);
        var engine = new Engine(cfg, problems);
        engine.OnCommand = HandleCommand;

        try
        {
            engine.Install();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "SandS", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        _engine = engine;
        _engine.Enabled = _enabledItem.Checked;
        UpdateTrayLook();
        ReportProblems(problems);
        return true;
    }

    private void HandleCommand(string cmd)
    {
        switch (cmd)
        {
            case "@reload": Reload(); break;
            case "@edit": EditConfig(); break;
            case "@toggle": _enabledItem.Checked = !_enabledItem.Checked; break;
            case "@exit": ExitApp(); break;
            default:
                _tray.ShowBalloonTip(4000, "SandS", $"知らないコマンドです: {cmd}", ToolTipIcon.Warning);
                break;
        }
    }

    private void Reload()
    {
        var cfg = Config.Load(_cfgPath, out var problems);
        var engine = new Engine(cfg, problems);
        engine.OnCommand = HandleCommand;

        // 新しいフックを張ってから古い方を外す。逆にすると、その隙間の打鍵が素通しになる。
        try
        {
            engine.Install();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"設定を再読み込みできませんでした。元の設定のまま続けます。\n\n{ex.Message}",
                "SandS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _engine?.Dispose();
        _engine = engine;
        _engine.Enabled = _enabledItem.Checked;

        UpdateTrayLook();
        ReportProblems(problems);
        if (problems.Count == 0)
            _tray.ShowBalloonTip(2000, "SandS", "設定を再読み込みしました。", ToolTipIcon.Info);
    }

    private void ReportProblems(List<string> problems)
    {
        if (problems.Count == 0) return;
        MessageBox.Show(
            "設定に解釈できない箇所があります。該当分だけ無効にして続けます。\n\n" +
            string.Join("\n", problems),
            "SandS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void UpdateTrayLook()
    {
        bool on = _engine?.Enabled ?? false;
        _tray.Icon = on ? IconOn : IconOff;
        _tray.Text = $"SandS — {(on ? "有効" : "停止中")}";
    }

    /// <summary>外部アイコンファイルを持たずに済むよう、トレイアイコンをその場で描く。</summary>
    private static Icon BuildIcon(bool enabled)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var fill = new SolidBrush(enabled
                ? Color.FromArgb(0x2E, 0x7D, 0x32)
                : Color.FromArgb(0x75, 0x75, 0x75));
            g.FillRectangle(fill, 2, 9, 28, 14);

            using var font = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.White);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("S&S", font, fg, new RectangleF(2, 9, 28, 14), fmt);
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            // FromHandle はハンドルを所有しないので、複製してから元を破棄する
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            Native.DestroyIcon(h);
        }
    }

    private void EditConfig()
    {
        try
        {
            if (!File.Exists(_cfgPath)) Config.Default().Save(_cfgPath);
            Process.Start(new ProcessStartInfo(_cfgPath) { UseShellExecute = true });
            _tray.ShowBalloonTip(4000, "SandS",
                "保存したら「設定を再読み込み」(または BackSpace+R) で反映されます。", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"設定ファイルを開けませんでした。\n{_cfgPath}\n\n{ex.Message}",
                "SandS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static bool IsStartupRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegKey, writable: false);
        return key?.GetValue(StartupValueName) is not null;
    }

    private void SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(StartupRegKey);
            if (enable) key.SetValue(StartupValueName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"スタートアップ設定を変更できませんでした。\n\n{ex.Message}",
                "SandS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _startupItem.Checked = IsStartupRegistered();
        }
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        _engine?.Uninstall();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _engine?.Dispose();
        }
        base.Dispose(disposing);
    }
}

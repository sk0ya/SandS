using System.Text;
using static SandS.Native;

namespace SandS;

internal enum StartupMode
{
    /// <summary>自動起動しない。</summary>
    None,
    /// <summary>HKCU\Run。非昇格で起動するので、管理者権限のウィンドウ上では SandS が効かない。</summary>
    Registry,
    /// <summary>タスクスケジューラ。最上位の特権で起動するので、管理者権限のウィンドウ上でも効く。</summary>
    Task,
}

/// <summary>
/// 自動起動の登録。
///
/// 非昇格プロセスのフックは、昇格したアプリへの入力に介入できない。つまり HKCU\Run で
/// 登録すると、管理者として実行しているウィンドウの上でだけ SandS が効かなくなる。
/// これを避けるには「最上位の特権で実行する」タスクとして登録する必要がある
/// (HKCU\Run には昇格して起動する手段が無い。あれば毎回 UAC が出てしまう)。
///
/// タスクの登録自体に管理者権限が要るので、一度だけ SandS を管理者として実行してもらう。
/// </summary>
internal static class Startup
{
    private const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SandS";
    private const string TaskName = "SandS";

    public static bool IsElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out IntPtr token)) return false;
        try
        {
            return GetTokenInformation(token, TokenElevation, out uint elevated, sizeof(uint), out _)
                   && elevated != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    public static StartupMode Current()
    {
        if (TaskExists()) return StartupMode.Task;
        return RegistryHasValue() ? StartupMode.Registry : StartupMode.None;
    }

    // ---- レジストリ (Microsoft.Win32.Registry を参照しないよう直接叩く) -----

    private static bool RegistryHasValue()
    {
        if (RegOpenKeyExW(HKEY_CURRENT_USER, RegKey, 0, KEY_READ, out IntPtr h) != ERROR_SUCCESS) return false;
        try
        {
            return RegQueryValueExW(h, ValueName, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == ERROR_SUCCESS;
        }
        finally { RegCloseKey(h); }
    }

    private static bool RegistrySet(string exe, out string? error)
    {
        error = null;
        int rc = RegCreateKeyExW(HKEY_CURRENT_USER, RegKey, 0, IntPtr.Zero, 0, KEY_WRITE, IntPtr.Zero,
                                 out IntPtr h, out _);
        if (rc != ERROR_SUCCESS) { error = $"レジストリキーを開けませんでした (error {rc})。"; return false; }
        try
        {
            string val = $"\"{exe}\"";
            // cbData は終端の NUL を含むバイト数
            rc = RegSetValueExW(h, ValueName, 0, REG_SZ, val, (uint)((val.Length + 1) * 2));
            if (rc != ERROR_SUCCESS) { error = $"レジストリに書けませんでした (error {rc})。"; return false; }
            return true;
        }
        finally { RegCloseKey(h); }
    }

    private static void RemoveRegistry()
    {
        if (RegOpenKeyExW(HKEY_CURRENT_USER, RegKey, 0, KEY_WRITE, out IntPtr h) != ERROR_SUCCESS) return;
        try { RegDeleteValueW(h, ValueName); }
        finally { RegCloseKey(h); }
    }

    /// <summary>登録する。昇格していればタスクとして、していなければ HKCU\Run に。</summary>
    public static StartupMode Enable(out string? note)
    {
        note = null;
        string exe = Environment.ProcessPath ?? "";

        if (IsElevated())
        {
            if (TryCreateTask(exe, out string? err))
            {
                RemoveRegistry();   // 二重起動しないよう、もう一方は消しておく
                return StartupMode.Task;
            }
            note = $"タスクとして登録できなかったので HKCU\\Run に登録します。\n\n{err}";
        }
        else
        {
            note = "SandS は管理者として実行されていないので、HKCU\\Run に登録しました。\n\n" +
                   "この場合、管理者権限で動いているウィンドウの上では SandS が効きません。\n" +
                   "そこでも効かせたい場合は、SandS を一度「管理者として実行」してから、\n" +
                   "このメニューで登録し直してください (タスクとして登録されます)。";
        }

        if (!RegistrySet(exe, out string? regErr))
        {
            note = $"スタートアップに登録できませんでした。\n\n{regErr}";
            return StartupMode.None;
        }
        RemoveTask();
        return StartupMode.Registry;
    }

    public static void Disable(out string? note)
    {
        note = null;
        RemoveRegistry();

        if (!TaskExists()) return;

        if (!IsElevated())
        {
            note = "タスクとして登録されていますが、解除には管理者権限が必要です。\n" +
                   "SandS を「管理者として実行」してから解除してください。";
            return;
        }
        RemoveTask();
    }

    // ---- タスクスケジューラ ------------------------------------------------

    private static bool TaskExists() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    private static void RemoveTask() => RunSchtasks($"/Delete /TN \"{TaskName}\" /F");

    private static bool TryCreateTask(string exe, out string? error)
    {
        error = null;
        string xmlPath = Path.Combine(Path.GetTempPath(), "sands.task.xml");

        try
        {
            // schtasks /XML は UTF-16 の XML しか受け付けない
            File.WriteAllText(xmlPath, TaskXml(exe), Encoding.Unicode);

            int rc = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (rc == 0) return true;

            error = rc == 1
                ? "アクセスが拒否されました。タスクの登録には管理者権限が必要です。"
                : $"schtasks が {rc} で終了しました。";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    /// <summary>XML が壊れていても schtasks のエラーになるだけで気づきにくいので、テストから検証できるようにしてある。</summary>
    internal static string TaskXml(string exe)
    {
        string user = Environment.UserDomainName + "\\" + Environment.UserName;
        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>SandS — キーカスタマイズ常駐ソフト。管理者権限のウィンドウ上でも効くよう、最上位の特権で起動する。</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{Esc(user)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{Esc(user)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{Esc(exe)}</Command>
            </Exec>
          </Actions>
        </Task>
        """;
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>
    /// schtasks を起動して終了コードを返す。
    /// System.Diagnostics.Process は AOT で 200KB 以上を持ち込むうえ、ここでは
    /// 終了コードしか要らないので CreateProcessW を直接使う。
    /// </summary>
    private static unsafe int RunSchtasks(string args)
    {
        // CreateProcessW は lpCommandLine を書き換えることがあるので、書き込める配列を渡す
        char[] cmd = $"schtasks.exe {args}\0".ToCharArray();

        var si = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW) };
        PROCESS_INFORMATION pi = default;

        fixed (char* pCmd = cmd)
        {
            if (!CreateProcessW(null, pCmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_NO_WINDOW,
                                IntPtr.Zero, null, &si, &pi))
                return -1;
        }

        try
        {
            if (WaitForSingleObject(pi.hProcess, 15000) != WAIT_OBJECT_0) return -1;
            return GetExitCodeProcess(pi.hProcess, out uint code) ? (int)code : -1;
        }
        finally
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
    }
}

using System.Diagnostics;

namespace SandS;

/// <summary>
/// SANDS_DEBUG_LOG に書き込み先パスが入っているときだけ動く診断ログ。
/// 低レベルフックは遅延に厳しい (LowLevelHooksTimeout) ので、既定では完全に無効。
/// </summary>
internal static class DebugLog
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("SANDS_DEBUG_LOG");
    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static bool Enabled => Path is not null;

    public static void Write(string line)
    {
        if (Path is null) return;
        lock (Gate)
        {
            try { File.AppendAllText(Path, $"[{Clock.ElapsedMilliseconds,6}] {line}\n"); }
            catch { /* 診断が本体を壊さないように握り潰す */ }
        }
    }
}

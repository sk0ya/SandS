namespace SandS;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string cfgPath = Config.DefaultPath;
        string mutexName = "Local\\SandS.SingleInstance";

        // --config <path>: 別の設定で起動する。E2E テストが実設定を壊さずに走るためにも使う。
        int i = Array.FindIndex(args, a => a is "--config" or "-c");
        if (i >= 0 && i + 1 < args.Length)
        {
            cfgPath = Path.GetFullPath(args[i + 1]);
            // 設定が違えば別インスタンスとして起動できてよい
            mutexName += "." + cfgPath.GetHashCode().ToString("X8");
        }

        // 多重起動すると同じキーを二重に握り潰して壊れるので防ぐ
        using var mutex = new Mutex(initiallyOwned: true, mutexName, out bool isFirst);
        if (!isFirst)
        {
            TrayApp.Info("SandS はすでに起動しています。");
            return 0;
        }

        using var app = new TrayApp(cfgPath);
        if (!app.Start()) return 1;
        return app.Run();
    }
}

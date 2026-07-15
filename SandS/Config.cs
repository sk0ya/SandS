using System.Text.Json;
using System.Text.Json.Serialization;

namespace SandS;

/// <summary>プレフィックスキー (押しながら他キーで別機能、単打では本来のキー) の定義。</summary>
internal sealed class PrefixKeyConfig
{
    /// <summary>プレフィックスにするキー。例: "Space", "BackSpace", "Enter"</summary>
    public string Key { get; set; } = "";

    /// <summary>単打したときに送るもの。null なら単打では何も送らない。</summary>
    public string? Tap { get; set; }

    /// <summary>
    /// 押しながら他キーを打ったとき、Map に無いキーすべてに付ける修飾キー。
    /// SandS はこれを "LShift" にしたもの。null ならこの動作をしない。
    /// </summary>
    public string? HoldModifier { get; set; }

    /// <summary>押しながらのキー別マッピング。キー名 → 送るコンボ。</summary>
    public Dictionary<string, string> Map { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>単打とみなす最大押下時間 (ms)。0 で無制限。</summary>
    public int TapTimeoutMs { get; set; } = 0;
}

internal sealed class Config
{
    public List<PrefixKeyConfig> PrefixKeys { get; set; } = [];

    /// <summary>
    /// プレフィックスを介さないホットキー / 単純リマップ。
    /// "!sc027": "^F12" や "sc070": "sc029" のように書く。
    /// </summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // リフレクションを使わない。NativeAOT で動かすための前提であり、
        // 通常ビルドでも起動時のリフレクション一式を読み込まずに済む。
        TypeInfoResolver = ConfigJsonContext.Default,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "sands.config.json");

    public static Config Load(string path, out List<string> problems)
    {
        problems = [];

        if (!File.Exists(path))
        {
            var def = Default();
            try { File.WriteAllText(path, JsonSerializer.Serialize(def, JsonOpts)); }
            catch { /* 書けなくても動作には支障がない */ }
            return def;
        }

        try
        {
            return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), JsonOpts) ?? Default();
        }
        catch (Exception ex)
        {
            problems.Add($"設定ファイルを読めないので既定値で起動します: {ex.Message}");
            return Default();
        }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));

    /// <summary>元の AutoHotkey スクリプトをそのまま移した既定値。</summary>
    public static Config Default() => new()
    {
        PrefixKeys =
        [
            new PrefixKeyConfig
            {
                Key = "Space",
                Tap = "Space",
                HoldModifier = "LShift",   // SandS
            },
            new PrefixKeyConfig
            {
                Key = "BackSpace",
                Tap = "BackSpace",
                Map = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["h"] = "{Blind}Left",
                    ["j"] = "{Blind}Down",
                    ["k"] = "{Blind}Up",
                    ["l"] = "{Blind}Right",
                    ["sc027"] = "BackSpace",
                    ["sc028"] = "Delete",
                    ["w"] = "!F4",
                    ["m"] = "AppsKey",
                    ["r"] = "@reload",
                    ["e"] = "@edit",
                },
            },
            new PrefixKeyConfig
            {
                Key = "Enter",
                Tap = "Enter",
                Map = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["1"] = "#1",
                    ["2"] = "#2",
                    ["3"] = "#3",
                    ["4"] = "#4",
                    ["5"] = "#5",
                    ["6"] = "#6",
                    ["7"] = "#7",
                    ["8"] = "#8",
                    ["9"] = "#9",
                    ["q"] = "#1",
                    ["BackSpace"] = "!Tab",
                    ["r"] = "#r",
                    ["e"] = "#7",
                    ["t"] = "#3",
                    ["h"] = "#Left",
                    ["j"] = "#Down",
                    ["k"] = "#Up",
                    ["l"] = "#Right",
                    ["Left"] = "^#Left",
                    ["Right"] = "^#Right",
                    ["Up"] = "^#d",
                    ["Down"] = "^#F4",
                },
            },
        ],
        Hotkeys = new(StringComparer.OrdinalIgnoreCase)
        {
            // LAlt & H::Send "{Blind}{LAlt}{Left}" — Alt は押されたままなので Blind で Left を送れば Alt+Left
            ["!h"] = "{Blind}Left",
            ["!j"] = "{Blind}Down",
            ["!k"] = "{Blind}Up",
            ["!l"] = "{Blind}Right",
            ["!sc027"] = "^F12",
            ["sc070"] = "sc029",
        },
    };
}

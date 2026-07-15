using System.Text.Json.Serialization;

namespace SandS;

/// <summary>
/// Config のシリアライザをソース生成する。リフレクション版を避けることで
/// NativeAOT で動かせるようになり、通常ビルドでも起動時の負荷が下がる。
/// </summary>
[JsonSerializable(typeof(Config))]
[JsonSerializable(typeof(PrefixKeyConfig))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class ConfigJsonContext : JsonSerializerContext;

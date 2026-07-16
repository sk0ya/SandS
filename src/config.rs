//! 設定ファイル。C# 版と同じスキーマなので、既存の sands.config.json がそのまま読める。

use indexmap::IndexMap;
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

/// プレフィックスキー (押しながら他キーで別機能、単打では本来のキー) の定義。
#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(default, rename_all = "PascalCase")]
pub struct PrefixKeyConfig {
    /// プレフィックスにするキー。例: "Space", "BackSpace", "Enter"
    pub key: String,
    /// 単打したときに送るもの。null なら単打では何も送らない。
    pub tap: Option<String>,
    /// 押しながら他キーを打ったとき、Map に無いキーすべてに付ける修飾キー。
    /// SandS はこれを "LShift" にしたもの。null ならこの動作をしない。
    pub hold_modifier: Option<String>,
    /// 押しながらのキー別マッピング。キー名 → 送るコンボ。
    pub map: IndexMap<String, String>,
    /// 単打とみなす最大押下時間 (ms)。0 で無制限。
    pub tap_timeout_ms: i64,
}

impl Default for PrefixKeyConfig {
    fn default() -> Self {
        Self {
            key: String::new(),
            tap: None,
            hold_modifier: None,
            map: IndexMap::new(),
            tap_timeout_ms: 0,
        }
    }
}

#[derive(Serialize, Deserialize, Clone, Debug, Default)]
#[serde(default, rename_all = "PascalCase")]
pub struct Config {
    pub prefix_keys: Vec<PrefixKeyConfig>,
    /// プレフィックスを介さないホットキー / 単純リマップ。
    /// "!sc027": "^F12" や "sc070": "sc029" のように書く。
    pub hotkeys: IndexMap<String, String>,
    /// 特定のアプリがフォアグラウンドのときだけ効くホットキー。
    /// 実行ファイル名 (大文字小文字は区別しない) → Hotkeys と同じ書式。
    /// AHK の #IfWinActive ahk_exe EXCEL.EXE に相当し、全体の Hotkeys より優先される。
    /// 送出側は "+{Space}^{x}{Down 2}" のような連続送出も書ける。
    pub app_hotkeys: IndexMap<String, IndexMap<String, String>>,
}

pub fn default_path() -> PathBuf {
    std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.join("sands.config.json")))
        .unwrap_or_else(|| PathBuf::from("sands.config.json"))
}

impl Config {
    pub fn load(path: &Path, problems: &mut Vec<String>) -> Config {
        if !path.exists() {
            let def = Config::default_config();
            // 書けなくても動作には支障がない
            let _ = def.save(path);
            return def;
        }

        match std::fs::read_to_string(path) {
            Ok(text) => match serde_json::from_str::<Config>(&text) {
                Ok(c) => c,
                Err(e) => {
                    problems.push(format!("設定ファイルを読めないので既定値で起動します: {e}"));
                    Config::default_config()
                }
            },
            Err(e) => {
                problems.push(format!("設定ファイルを読めないので既定値で起動します: {e}"));
                Config::default_config()
            }
        }
    }

    pub fn save(&self, path: &Path) -> std::io::Result<()> {
        let text = serde_json::to_string_pretty(self)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
        std::fs::write(path, text)
    }

    /// 元の AutoHotkey スクリプトをそのまま移した既定値。
    pub fn default_config() -> Config {
        let m = |pairs: &[(&str, &str)]| -> IndexMap<String, String> {
            pairs
                .iter()
                .map(|(k, v)| (k.to_string(), v.to_string()))
                .collect()
        };

        Config {
            prefix_keys: vec![
                PrefixKeyConfig {
                    key: "Space".into(),
                    tap: Some("Space".into()),
                    hold_modifier: Some("LShift".into()), // SandS
                    ..Default::default()
                },
                PrefixKeyConfig {
                    key: "BackSpace".into(),
                    tap: Some("BackSpace".into()),
                    map: m(&[
                        ("h", "{Blind}Left"),
                        ("j", "{Blind}Down"),
                        ("k", "{Blind}Up"),
                        ("l", "{Blind}Right"),
                        ("sc027", "BackSpace"),
                        ("sc028", "Delete"),
                        ("w", "!F4"),
                        ("m", "AppsKey"),
                        ("r", "@reload"),
                        ("e", "@edit"),
                    ]),
                    ..Default::default()
                },
                PrefixKeyConfig {
                    key: "Enter".into(),
                    tap: Some("Enter".into()),
                    map: m(&[
                        ("1", "#1"),
                        ("2", "#2"),
                        ("3", "#3"),
                        ("4", "#4"),
                        ("5", "#5"),
                        ("6", "#6"),
                        ("7", "#7"),
                        ("8", "#8"),
                        ("9", "#9"),
                        ("q", "#1"),
                        ("BackSpace", "!Tab"),
                        ("r", "#r"),
                        ("e", "#7"),
                        ("t", "#3"),
                        ("h", "#Left"),
                        ("j", "#Down"),
                        ("k", "#Up"),
                        ("l", "#Right"),
                        ("Left", "^#Left"),
                        ("Right", "^#Right"),
                        ("Up", "^#d"),
                        ("Down", "^#F4"),
                    ]),
                    ..Default::default()
                },
            ],
            hotkeys: m(&[
                // LAlt & H::Send "{Blind}{LAlt}{Left}" — Alt は押されたままなので Blind で Left を送れば Alt+Left
                ("!h", "{Blind}Left"),
                ("!j", "{Blind}Down"),
                ("!k", "{Blind}Up"),
                ("!l", "{Blind}Right"),
                ("!sc027", "^F12"),
                ("sc070", "sc029"),
            ]),
            // 元の AHK スクリプトの #IfWinActive ahk_exe EXCEL.EXE ブロック
            app_hotkeys: [(
                "EXCEL.EXE".to_string(),
                m(&[
                    ("+Space", "+{Space}"),                                // 行選択
                    ("+^sc027", "+{Space}+^{sc027}"),                      // 行選択して…
                    ("+^!sc027", "^{Space}+^{sc027}"),                     // 列選択して…
                    ("^l", "+{Space}^{-}"),                                // 行削除
                    ("Tab", "^{PgDn}"),                                    // 次のシート
                    ("+Tab", "^{PgUp}"),                                   // 前のシート
                    ("!Up", "+{Space}^{x}{Up}+{Space}+^{sc027}"),          // 行を上へ移動
                    ("!Down", "+{Space}^{x}{Down 2}+{Space}+^{sc027}"),    // 行を下へ移動
                    ("!+Down", "+{Space}^{c}{Down}+{Space}+^{sc027}"),     // 行を下へ複製
                    ("!Right", "^{x}{Right 2}{AppsKey}{e}{Enter}"),        // セルを右へ移動
                    ("!Left", "^{x}{Left}{AppsKey}{e}{Enter}"),            // セルを左へ移動
                    ("^q", "^{_}^{&}"),                                    // 罫線を引き直す
                ]),
            )]
            .into_iter()
            .collect(),
        }
    }
}

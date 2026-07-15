//! パーサ・設定・アイコン・タスク XML の検証。
//! 実キー挙動は tests/e2e が exe をブラックボックスとして受け持つ。

use crate::config::Config;
use crate::engine::Engine;
use crate::keyspec::{Combo, KeySpec, ModGroup};
use crate::{icon, startup, vk};

#[test]
fn 既定設定が警告なしでコンパイルできる() {
    // Win+1 / Alt+F4 などは実挙動を試せないので、せめてキー名とコンボが
    // 全て解釈できることをここで担保する。
    let cfg = Config::default_config();
    let mut problems = Vec::new();
    let _ = Engine::new(&cfg, &mut problems);
    assert!(problems.is_empty(), "問題あり: {problems:?}");
}

#[test]
fn 既定設定の割り当て数() {
    let cfg = Config::default_config();
    let n: usize = cfg.prefix_keys.iter().map(|p| p.map.len()).sum::<usize>() + cfg.hotkeys.len();
    assert!(n >= 35, "割り当てが {n} 件しかない");
}

#[test]
fn 設定はjsonを往復しても壊れない() {
    // 既存の sands.config.json がそのまま読める必要がある。
    let cfg = Config::default_config();
    let json = serde_json::to_string_pretty(&cfg).unwrap();

    // C# 版が書く PascalCase のキー名であること
    assert!(json.contains("\"PrefixKeys\""), "{json}");
    assert!(json.contains("\"HoldModifier\""), "{json}");
    assert!(json.contains("\"TapTimeoutMs\""), "{json}");

    let back: Config = serde_json::from_str(&json).unwrap();
    assert_eq!(back.prefix_keys.len(), cfg.prefix_keys.len());
    assert_eq!(back.hotkeys.len(), cfg.hotkeys.len());
    assert_eq!(back.prefix_keys[0].hold_modifier.as_deref(), Some("LShift"));
    // Map の順序が保たれること (IndexMap を使っている理由)
    assert_eq!(
        back.prefix_keys[1].map.keys().collect::<Vec<_>>(),
        cfg.prefix_keys[1].map.keys().collect::<Vec<_>>()
    );
}

#[test]
fn c_sharp_版が書いた設定を読める() {
    // 実際に C# 版が吐く形。フィールドが欠けていても既定値で埋まること。
    let json = r#"{
      "PrefixKeys": [
        { "Key": "Space", "Tap": "Space", "HoldModifier": "LShift", "Map": {}, "TapTimeoutMs": 0 },
        { "Key": "BackSpace", "Tap": "BackSpace", "HoldModifier": null,
          "Map": { "h": "{Blind}Left" }, "TapTimeoutMs": 0 }
      ],
      "Hotkeys": { "!sc027": "^F12", "sc070": "sc029" }
    }"#;
    let cfg: Config = serde_json::from_str(json).unwrap();
    assert_eq!(cfg.prefix_keys.len(), 2);
    assert_eq!(cfg.hotkeys.get("!sc027").map(|s| s.as_str()), Some("^F12"));

    let mut problems = Vec::new();
    let _ = Engine::new(&cfg, &mut problems);
    assert!(problems.is_empty(), "問題あり: {problems:?}");
}

#[test]
fn トレイアイコンを生成できる() {
    // 失敗しても「無地のアイコンが出る」だけで気づけないので見ておく。
    assert!(!icon::create(true).is_null());
    assert!(!icon::create(false).is_null());
}

#[test]
fn タスクxmlが妥当で最上位の特権になっている() {
    // XML が壊れていても schtasks のエラーになるだけで原因が分かりにくい。
    let xml = startup::task_xml(r"C:\dummy\SandS.exe");
    assert!(xml.contains("<RunLevel>HighestAvailable</RunLevel>"), "{xml}");
    assert!(xml.contains(r"<Command>C:\dummy\SandS.exe</Command>"), "{xml}");
    assert!(xml.contains("<LogonTrigger>"), "{xml}");
    // タグの釣り合い。まともな XML パーサを足すほどではない。
    assert_eq!(xml.matches("<Task ").count(), 1);
    assert_eq!(xml.matches("</Task>").count(), 1);
}

#[test]
fn タスクxmlはexeパスをエスケープする() {
    let xml = startup::task_xml(r"C:\a & b\<x>.exe");
    assert!(xml.contains("<Command>C:\\a &amp; b\\&lt;x&gt;.exe</Command>"), "{xml}");
}

// ---- パーサ (C# 版には無かったが、ここが全ての土台なので固めておく) ----

#[test]
fn キー名を解釈できる() {
    // 設定ファイル互換のため、WinForms の Keys と同じ名前が引けること
    assert_eq!(KeySpec::parse("h").unwrap().code, vk::by_name("H").unwrap());
    assert_eq!(KeySpec::parse("BackSpace").unwrap().code, vk::BACK);
    assert_eq!(KeySpec::parse("Enter").unwrap().code, vk::RETURN);
    assert_eq!(KeySpec::parse("AppsKey").unwrap().code, vk::APPS);
    assert_eq!(KeySpec::parse("LShift").unwrap().code, vk::L_SHIFT);
    assert_eq!(KeySpec::parse("F12").unwrap().code, 123);
    assert_eq!(KeySpec::parse("F24").unwrap().code, 135);
    assert_eq!(KeySpec::parse("NumPad0").unwrap().code, 96);
    assert_eq!(KeySpec::parse("Muhenkan").unwrap().code, 29);
    assert_eq!(KeySpec::parse("Henkan").unwrap().code, 28);
    // "1" は数値 1 ではなく D1 になること
    assert_eq!(KeySpec::parse("1").unwrap().code, vk::D0 + 1);
    assert!(KeySpec::parse("そんなキーはない").is_none());
}

#[test]
fn スキャンコード指定を解釈できる() {
    let k = KeySpec::parse("sc027").unwrap();
    assert!(k.by_scan && k.scan == 0x27 && !k.extended);

    // AHK と同じく 0x100 のビットで拡張キー
    let e = KeySpec::parse("sc14B").unwrap();
    assert!(e.by_scan && e.scan == 0x4B && e.extended);
}

#[test]
fn 壊れた設定でも落ちない() {
    // panic = abort なので、パーサが panic すると設定のタイプミス 1 つでアプリが即死する。
    // 特に非 ASCII は文字境界を割って落ちやすい (実際に落ちていた)。
    let json = r#"{
      "PrefixKeys": [
        { "Key": "そんなキー", "Tap": "Space" },
        { "Key": "Space", "Tap": "ｽﾍﾟｰｽ", "HoldModifier": "しふと" },
        { "Key": "Enter", "Map": { "あ": "Left", "h": "{ブラインド}Left", "j": "^" } }
      ],
      "Hotkeys": { "!あ": "^F12", "sc": "x", "vk": "y", "!h": "" }
    }"#;
    let cfg: Config = serde_json::from_str(json).unwrap();
    let mut problems = Vec::new();
    let _ = Engine::new(&cfg, &mut problems);
    // 落ちずに、解釈できなかった分を問題として報告できていればよい
    assert!(!problems.is_empty());
}

#[test]
fn 非asciiのキー名でパーサが落ちない() {
    for s in ["そ", "そんなキー", "あい", "ｽ", "🎹", "s🎹", "{Blind}あ", "^🎹"] {
        let _ = KeySpec::parse(s);
        let _ = Combo::parse(s);
        let _ = vk::by_name(s);
    }
}

#[test]
fn コンボを解釈できる() {
    let c = Combo::parse("^#Left").unwrap();
    assert!(c.mods.contains(ModGroup::CTRL) && c.mods.contains(ModGroup::WIN));
    assert_eq!(c.key.code, vk::LEFT);
    assert!(!c.blind);

    let b = Combo::parse("{Blind}Left").unwrap();
    assert!(b.blind && b.mods == ModGroup::NONE);

    // AHK 風に {} で括った書き方も受ける
    assert_eq!(Combo::parse("!{F4}").unwrap().key.code, 115);
    assert_eq!(Combo::parse("#1").unwrap().key.code, vk::D0 + 1);

    assert!(Combo::parse("^そんなキー").is_err());
    assert!(Combo::parse("").is_err());
}

#[test]
fn 元のahkスクリプトの割り当てが全部解釈できる() {
    // 実挙動を流せない Win+1 / Alt+F4 / Ctrl+Win+F4 も、ここで形だけは担保する
    for s in [
        "{Blind}Left", "{Blind}Down", "{Blind}Up", "{Blind}Right",
        "BackSpace", "Delete", "!F4", "AppsKey",
        "#1", "#9", "!Tab", "#r", "#Left", "^#Left", "^#d", "^#F4",
        "^F12", "sc029",
    ] {
        assert!(Combo::parse(s).is_ok(), "解釈できない: {s}");
    }
}

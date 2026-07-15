//! SANDS_DEBUG_LOG に書き込み先パスが入っているときだけ動く診断ログ。
//! 低レベルフックは遅延に厳しい (LowLevelHooksTimeout) ので、既定では完全に無効。

use std::io::Write;
use std::sync::OnceLock;

fn path() -> Option<&'static String> {
    static P: OnceLock<Option<String>> = OnceLock::new();
    P.get_or_init(|| std::env::var("SANDS_DEBUG_LOG").ok())
        .as_ref()
}

pub fn enabled() -> bool {
    path().is_some()
}

pub fn write(line: &str) {
    let Some(p) = path() else { return };
    // 診断が本体を壊さないように、失敗は握り潰す
    if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(p) {
        let _ = writeln!(f, "{line}");
    }
}

//! 仮想キーコードと、その名前。
//!
//! 名前は WinForms の System.Windows.Forms.Keys に合わせてある。既存の設定ファイルが
//! そのまま読めなくなるため。C# 版は Enum.TryParse がこの表をタダでくれていたが、
//! Rust では明示的に持つ必要がある。

pub const NONE: u16 = 0;

pub const BACK: u16 = 8;
pub const RETURN: u16 = 13;
pub const SHIFT_KEY: u16 = 16;
pub const CONTROL_KEY: u16 = 17;
pub const MENU: u16 = 18;
pub const ESCAPE: u16 = 27;
pub const SPACE: u16 = 32;
pub const LEFT: u16 = 37;
pub const UP: u16 = 38;
pub const RIGHT: u16 = 39;
pub const DOWN: u16 = 40;
pub const D0: u16 = 48;
pub const APPS: u16 = 93;
pub const L_SHIFT: u16 = 160;
pub const R_SHIFT: u16 = 161;
pub const L_CONTROL: u16 = 162;
pub const R_CONTROL: u16 = 163;
pub const L_MENU: u16 = 164;
pub const R_MENU: u16 = 165;
pub const L_WIN: u16 = 91;
pub const R_WIN: u16 = 92;

/// 名前 → 仮想キーコード。大文字小文字は区別しない。
/// 別名 (BackSpace/BS, Enter, AppsKey, Muhenkan など) も C# 版と揃えてある。
static NAMES: &[(&str, u16)] = &[
    ("Back", BACK),
    ("BackSpace", BACK),
    ("BS", BACK),
    ("Tab", 9),
    ("Clear", 12),
    ("Return", RETURN),
    ("Enter", RETURN),
    ("ShiftKey", SHIFT_KEY),
    ("ControlKey", CONTROL_KEY),
    ("Menu", MENU),
    ("Pause", 19),
    ("Capital", 20),
    ("CapsLock", 20),
    // 日本語・東アジア入力
    ("KanaMode", 21),
    ("HangulMode", 21),
    ("JunjaMode", 23),
    ("FinalMode", 24),
    ("HanjaMode", 25),
    ("KanjiMode", 25),
    ("Kanji", 25),
    ("IMEConvert", 28),
    ("Henkan", 28),
    ("IMENonconvert", 29),
    ("Muhenkan", 29),
    ("IMEAccept", 30),
    ("IMEModeChange", 31),
    ("Escape", ESCAPE),
    ("Esc", ESCAPE),
    ("Space", SPACE),
    ("PageUp", 33),
    ("PgUp", 33),
    ("PageDown", 34),
    ("PgDn", 34),
    ("End", 35),
    ("Home", 36),
    ("Left", LEFT),
    ("Up", UP),
    ("Right", RIGHT),
    ("Down", DOWN),
    ("Select", 41),
    ("Print", 42),
    ("Execute", 43),
    ("PrintScreen", 44),
    ("Insert", 45),
    ("Ins", 45),
    ("Delete", 46),
    ("Del", 46),
    ("Help", 47),
    ("LWin", L_WIN),
    ("RWin", R_WIN),
    ("Apps", APPS),
    ("AppsKey", APPS),
    ("ContextMenu", APPS),
    ("Sleep", 95),
    ("Multiply", 106),
    ("Add", 107),
    ("Separator", 108),
    ("Subtract", 109),
    ("Decimal", 110),
    ("Divide", 111),
    ("NumLock", 144),
    ("Scroll", 145),
    ("LShiftKey", L_SHIFT),
    ("LShift", L_SHIFT),
    ("RShiftKey", R_SHIFT),
    ("RShift", R_SHIFT),
    ("Shift", L_SHIFT),
    ("LControlKey", L_CONTROL),
    ("LCtrl", L_CONTROL),
    ("RControlKey", R_CONTROL),
    ("RCtrl", R_CONTROL),
    ("Ctrl", L_CONTROL),
    ("Control", L_CONTROL),
    ("LMenu", L_MENU),
    ("LAlt", L_MENU),
    ("RMenu", R_MENU),
    ("RAlt", R_MENU),
    ("Alt", L_MENU),
    ("BrowserBack", 166),
    ("BrowserForward", 167),
    ("BrowserRefresh", 168),
    ("BrowserStop", 169),
    ("BrowserSearch", 170),
    ("BrowserFavorites", 171),
    ("BrowserHome", 172),
    ("VolumeMute", 173),
    ("VolumeDown", 174),
    ("VolumeUp", 175),
    ("MediaNextTrack", 176),
    ("MediaPreviousTrack", 177),
    ("MediaStop", 178),
    ("MediaPlayPause", 179),
    ("LaunchMail", 180),
    ("SelectMedia", 181),
    ("LaunchApplication1", 182),
    ("LaunchApplication2", 183),
    ("Oem1", 186),
    ("OemSemicolon", 186),
    ("Oemplus", 187),
    ("Oemcomma", 188),
    ("OemMinus", 189),
    ("OemPeriod", 190),
    ("Oem2", 191),
    ("OemQuestion", 191),
    ("Oem3", 192),
    ("Oemtilde", 192),
    ("Oem4", 219),
    ("OemOpenBrackets", 219),
    ("Oem5", 220),
    ("OemPipe", 220),
    ("Oem6", 221),
    ("OemCloseBrackets", 221),
    ("Oem7", 222),
    ("OemQuotes", 222),
    ("Oem8", 223),
    ("Oem102", 226),
    ("OemBackslash", 226),
    ("ProcessKey", 229),
    ("Packet", 231),
    ("Attn", 246),
    ("Crsel", 247),
    ("Exsel", 248),
    ("EraseEof", 249),
    ("Play", 250),
    ("Zoom", 251),
    ("NoName", 252),
    ("Pa1", 253),
    ("OemClear", 254),
];

pub fn by_name(s: &str) -> Option<u16> {
    // A-Z / 0-9 の 1 文字。表に 36 個並べるより素直。
    let b = s.as_bytes();
    if b.len() == 1 {
        let c = b[0];
        if c.is_ascii_alphabetic() {
            return Some(c.to_ascii_uppercase() as u16);
        }
        if c.is_ascii_digit() {
            return Some(D0 + (c - b'0') as u16);
        }
    }

    // F1..F24 と NumPad0..9 も規則的なので表に入れない
    if let Some(n) = s.strip_prefix_ci("F") {
        if let Ok(n) = n.parse::<u16>() {
            if (1..=24).contains(&n) {
                return Some(111 + n);
            }
        }
    }
    if let Some(n) = s.strip_prefix_ci("NumPad") {
        if let Ok(n) = n.parse::<u16>() {
            if n <= 9 {
                return Some(96 + n);
            }
        }
    }

    NAMES
        .iter()
        .find(|(name, _)| name.eq_ignore_ascii_case(s))
        .map(|(_, vk)| *vk)
}

/// 表示用。ログを読むときだけ使うので線形走査でよい。
pub fn name_of(vk: u16) -> String {
    if (b'A' as u16..=b'Z' as u16).contains(&vk) {
        return (vk as u8 as char).to_string();
    }
    if (D0..=D0 + 9).contains(&vk) {
        return ((b'0' + (vk - D0) as u8) as char).to_string();
    }
    NAMES
        .iter()
        .find(|(_, v)| *v == vk)
        .map(|(n, _)| n.to_string())
        .unwrap_or_else(|| format!("vk{vk:02X}"))
}

trait StripCi {
    fn strip_prefix_ci(&self, p: &str) -> Option<&str>;
}

impl StripCi for str {
    fn strip_prefix_ci(&self, p: &str) -> Option<&str> {
        // get で文字境界を確かめる。self[..n] だと非 ASCII のキー名で panic する。
        match self.get(..p.len()) {
            Some(head) if head.eq_ignore_ascii_case(p) && self.len() > p.len() => {
                Some(&self[p.len()..])
            }
            _ => None,
        }
    }
}

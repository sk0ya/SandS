//! 1 つのキーの指定と、AHK 風のキーコンボ。

use crate::vk;
use windows_sys::Win32::UI::Input::KeyboardAndMouse::{MapVirtualKeyW, MAPVK_VK_TO_VSC};
use windows_sys::Win32::UI::WindowsAndMessaging::KBDLLHOOKSTRUCT;

const LLKHF_EXTENDED: u32 = 0x01;

/// "h" / "Left" / "F12" のような名前指定と、"sc027" のようなスキャンコード指定の両方を扱う。
///
/// スキャンコード指定は日本語配列で重要。sc027 (;) や sc070 (カタカナ/ひらがな) は
/// 仮想キーコードが配列や IME 状態で揺れるので、物理位置で指定する方が確実。
/// by_scan の場合は一致判定も送出もスキャンコードで行う。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct KeySpec {
    pub code: u16,
    pub scan: u16,
    pub by_scan: bool,
    pub extended: bool,
    pub text: String,
}

fn is_extended(vk: u16) -> bool {
    matches!(
        vk,
        vk::R_CONTROL
            | vk::R_MENU
            | vk::L_WIN
            | vk::R_WIN
            | vk::APPS
            | 45 // Insert
            | 46 // Delete
            | 36 // Home
            | 35 // End
            | 33 // PageUp
            | 34 // PageDown
            | vk::LEFT
            | vk::RIGHT
            | vk::UP
            | vk::DOWN
            | 144 // NumLock
            | 111 // Divide
            | 44 // PrintScreen
    )
}

impl KeySpec {
    pub fn parse(s: &str) -> Option<KeySpec> {
        let s = s.trim();
        if s.is_empty() {
            return None;
        }

        // 先頭 2 バイトを直接スライスすると、非 ASCII のキー名で文字境界を割って panic する。
        // panic = abort なので、設定のタイプミス 1 つでアプリが落ちてしまう。
        // get なら境界を割るときに None が返る。
        let prefix2 = s.get(..2);

        // sc027 / sc14B — AHK 互換。0x100 のビットが立っていれば拡張キー。
        if prefix2.is_some_and(|p| p.eq_ignore_ascii_case("sc")) {
            if let Ok(raw) = u32::from_str_radix(&s[2..], 16) {
                return Some(KeySpec {
                    code: 0,
                    scan: (raw & 0xFF) as u16,
                    by_scan: true,
                    extended: raw & 0x100 != 0,
                    text: s.to_string(),
                });
            }
        }

        // vk1D — 仮想キーコード直接指定
        if prefix2.is_some_and(|p| p.eq_ignore_ascii_case("vk")) {
            if let Ok(raw) = u16::from_str_radix(&s[2..], 16) {
                return KeySpec::from_vk(raw, s);
            }
        }

        vk::by_name(s).and_then(|v| KeySpec::from_vk(v, s))
    }

    fn from_vk(code: u16, text: &str) -> Option<KeySpec> {
        if code == vk::NONE {
            return None;
        }
        let scan = unsafe { MapVirtualKeyW(code as u32, MAPVK_VK_TO_VSC) } as u16;
        Some(KeySpec {
            code,
            scan,
            by_scan: false,
            extended: is_extended(code),
            text: text.to_string(),
        })
    }

    /// フックが受け取ったイベントがこのキーか。
    /// sc 指定なら物理位置 (スキャンコード) で、名前指定なら仮想キーコードで判定する。
    #[inline]
    pub fn matches(&self, info: &KBDLLHOOKSTRUCT) -> bool {
        if self.by_scan {
            info.scanCode as u16 == self.scan
                && ((info.flags & LLKHF_EXTENDED) != 0) == self.extended
        } else {
            info.vkCode as u16 == self.code
        }
    }
}

bitflags_lite! {
    /// 修飾キーの系統。左右は区別しない。
    pub struct ModGroup: u8 {
        const CTRL  = 1;
        const ALT   = 2;
        const SHIFT = 4;
        const WIN   = 8;
    }
}

pub fn group_of(vk: u16) -> ModGroup {
    match vk {
        vk::L_CONTROL | vk::R_CONTROL | vk::CONTROL_KEY => ModGroup::CTRL,
        vk::L_SHIFT | vk::R_SHIFT | vk::SHIFT_KEY => ModGroup::SHIFT,
        vk::L_MENU | vk::R_MENU | vk::MENU => ModGroup::ALT,
        vk::L_WIN | vk::R_WIN => ModGroup::WIN,
        _ => ModGroup::NONE,
    }
}

/// 物理的に押されている修飾キーを 1 バイトのビットマスクで持つ。
/// 修飾キーは 8 種しかないので、集合を使う理由がない
/// (打鍵ごとに走る経路なので、確保も走査も避けたい)。
pub mod modmask {
    use super::*;

    /// ビット位置と対応する仮想キー。順序がビット番号そのもの。
    pub const VKS: [u16; 8] = [
        vk::L_CONTROL,
        vk::R_CONTROL,
        vk::L_SHIFT,
        vk::R_SHIFT,
        vk::L_MENU,
        vk::R_MENU,
        vk::L_WIN,
        vk::R_WIN,
    ];

    #[inline]
    pub fn bit_of(vk_code: u16) -> Option<u32> {
        match vk_code {
            vk::L_CONTROL => Some(0),
            vk::R_CONTROL => Some(1),
            vk::L_SHIFT => Some(2),
            vk::R_SHIFT => Some(3),
            vk::L_MENU => Some(4),
            vk::R_MENU => Some(5),
            vk::L_WIN => Some(6),
            vk::R_WIN => Some(7),
            _ => None,
        }
    }

    pub fn groups_of(mask: u8) -> ModGroup {
        let mut g = ModGroup::NONE;
        if mask & 0b0000_0011 != 0 {
            g |= ModGroup::CTRL;
        }
        if mask & 0b0000_1100 != 0 {
            g |= ModGroup::SHIFT;
        }
        if mask & 0b0011_0000 != 0 {
            g |= ModGroup::ALT;
        }
        if mask & 0b1100_0000 != 0 {
            g |= ModGroup::WIN;
        }
        g
    }
}

/// AHK 風のキーコンボ。"^#Left" / "!F4" / "{Blind}Left" / "sc029" / "#1" など。
///
///   ^ = Ctrl, ! = Alt, + = Shift, # = Win
///   {Blind} を先頭に付けると、物理的に押されている修飾キーをそのまま残して送る。
///   付けない場合は AHK の既定と同じく、コンボに含まれない修飾キーを一時的に外して送る。
#[derive(Clone, Debug)]
pub struct Combo {
    pub mods: ModGroup,
    pub key: KeySpec,
    pub blind: bool,
}

impl Combo {
    pub fn parse(s: &str) -> Result<Combo, String> {
        let mut s = s.trim();
        if s.is_empty() {
            return Err("空です".into());
        }

        // ここも同じ。s[..7] だと非 ASCII で文字境界を割って panic する。
        let mut blind = false;
        if s.get(..7).is_some_and(|p| p.eq_ignore_ascii_case("{Blind}")) {
            blind = true;
            s = s[7..].trim();
        }

        let mut mods = ModGroup::NONE;
        let mut rest = s;
        loop {
            let c = match rest.chars().next() {
                Some(c) => c,
                None => break,
            };
            match c {
                '^' => mods |= ModGroup::CTRL,
                '!' => mods |= ModGroup::ALT,
                '+' => mods |= ModGroup::SHIFT,
                '#' => mods |= ModGroup::WIN,
                // AHK の左右指定。一致判定は左右を区別しないので読み飛ばす。
                '<' | '>' => {}
                _ => break,
            }
            rest = &rest[c.len_utf8()..];
        }

        let mut key_part = rest.trim();
        // AHK は Send の中でキー名を {} で括る ("{Left}")。どちらの書き方も受ける。
        if key_part.len() > 2 && key_part.starts_with('{') && key_part.ends_with('}') {
            key_part = key_part[1..key_part.len() - 1].trim();
        }

        match KeySpec::parse(key_part) {
            Some(key) => Ok(Combo { mods, key, blind }),
            None => Err(format!("キー \"{key_part}\" を解釈できません")),
        }
    }
}

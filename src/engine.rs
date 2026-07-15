//! 低レベルキーボードフックの本体。3 つの機能を 1 つの状態機械で扱う。
//!
//!   1. プレフィックスキー — Space/BackSpace/Enter のように、単打では本来のキー、
//!      押しながらだと別マップ。SandS は「Map に無いキーすべてに Shift を足す」特殊形。
//!   2. ホットキー — "!sc027 → ^F12" のような修飾キー付きの置き換え。
//!   3. 単純リマップ — "sc070 → sc029"。

use crate::config::Config;
use crate::keyspec::{modmask, Combo, KeySpec, ModGroup};
use crate::log;
use crate::sender::{self, INJECTED_TAG};
use crate::vk;

use windows_sys::Win32::Foundation::{LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::Input::KeyboardAndMouse::{MapVirtualKeyW, MAPVK_VK_TO_VSC};
use windows_sys::Win32::UI::WindowsAndMessaging::*;

pub enum Action {
    Send(Combo),
    /// "@reload" など。フック内で実行すると危険なので UI スレッドへ回す。
    Command(String),
}

pub struct Binding {
    pub key: KeySpec,
    pub action: Action,
}

pub struct CompiledPrefix {
    pub key: KeySpec,
    pub tap: Option<Combo>,
    pub hold_modifier: Option<u16>,
    /// 件数が少ないので線形走査。ハッシュだとスキャンコード指定との併用が面倒なだけ。
    pub map: Vec<Binding>,
    pub tap_timeout_ms: i64,
}

pub struct CompiledHotkey {
    pub mods: ModGroup,
    pub binding: Binding,
}

pub struct Engine {
    prefixes: Vec<CompiledPrefix>,
    hotkeys: Vec<CompiledHotkey>,

    pub enabled: bool,

    active: Option<usize>,
    active_used: bool,
    active_down_at: u64,
    /// 注入した hold_modifier。押した本体を持っておかないと確実に離せない。
    hold_mod_key: Option<u16>,

    /// いま物理的に押されている修飾キーのビットマスク。Blind でない送出で外して戻すために必要。
    phys_mask: u8,

    /// down を握り潰したキーのスキャンコード。対応する up も握り潰さないと、宛先に up だけ届く。
    /// 同時に押されうる数はたかが知れているので、固定長の線形走査で十分。
    swallow: [u16; 16],
    swallow_n: usize,

    /// フックから拾ったコマンド。UI スレッドが引き取る。
    pub pending: Vec<String>,
}

fn now_ms() -> u64 {
    // Instant より軽く、ここでは単調な ms が取れれば十分
    unsafe { windows_sys::Win32::System::SystemInformation::GetTickCount64() }
}

impl Engine {
    pub fn new(cfg: &Config, problems: &mut Vec<String>) -> Engine {
        let mut prefixes = Vec::new();
        for p in &cfg.prefix_keys {
            let key = match KeySpec::parse(&p.key) {
                Some(k) => k,
                None => {
                    problems.push(format!(
                        "PrefixKeys: キー \"{}\" を解釈できません。この定義を飛ばします。",
                        p.key
                    ));
                    continue;
                }
            };

            let tap = match &p.tap {
                Some(t) => match Combo::parse(t) {
                    Ok(c) => Some(c),
                    Err(e) => {
                        problems.push(format!("{}.Tap \"{t}\": {e}", p.key));
                        None
                    }
                },
                None => None,
            };

            let hold_modifier = match &p.hold_modifier {
                Some(h) => match KeySpec::parse(h) {
                    Some(k) => Some(k.code),
                    None => {
                        problems
                            .push(format!("{}.HoldModifier \"{h}\" を解釈できません。", p.key));
                        None
                    }
                },
                None => None,
            };

            let mut map = Vec::new();
            for (k, v) in &p.map {
                let mk = match KeySpec::parse(k) {
                    Some(x) => x,
                    None => {
                        problems.push(format!("{} & {k}: キー名を解釈できません。", p.key));
                        continue;
                    }
                };
                match parse_action(v) {
                    Ok(action) => map.push(Binding { key: mk, action }),
                    Err(e) => problems.push(format!("{} & {k}: {e}", p.key)),
                }
            }

            prefixes.push(CompiledPrefix {
                key,
                tap,
                hold_modifier,
                map,
                tap_timeout_ms: p.tap_timeout_ms.max(0),
            });
        }

        let mut hotkeys = Vec::new();
        for (trigger, action) in &cfg.hotkeys {
            let t = match Combo::parse(trigger) {
                Ok(t) => t,
                Err(e) => {
                    problems.push(format!("Hotkeys \"{trigger}\": {e}"));
                    continue;
                }
            };
            match parse_action(action) {
                Ok(a) => hotkeys.push(CompiledHotkey {
                    mods: t.mods,
                    binding: Binding {
                        key: t.key,
                        action: a,
                    },
                }),
                Err(e) => problems.push(format!("Hotkeys \"{trigger}\": {e}")),
            }
        }

        Engine {
            prefixes,
            hotkeys,
            enabled: true,
            active: None,
            active_used: false,
            active_down_at: 0,
            hold_mod_key: None,
            phys_mask: 0,
            swallow: [0; 16],
            swallow_n: 0,
            pending: Vec::new(),
        }
    }

    pub fn counts(&self) -> (usize, usize) {
        (self.prefixes.len(), self.hotkeys.len())
    }

    /// 無効化する瞬間に必ず後始末する。プレフィックスを押している最中 (= 修飾キー注入済み) に
    /// 無効化されると、その修飾キーが押しっぱなしのまま誰も離さなくなるため。
    pub fn set_enabled(&mut self, on: bool) {
        if self.enabled == on {
            return;
        }
        self.enabled = on;
        if !on {
            self.reset();
        }
    }

    pub fn reset(&mut self) {
        self.release_hold_modifier();
        self.active = None;
        self.active_used = false;
        self.swallow_n = 0;
    }

    fn release_hold_modifier(&mut self) {
        if let Some(hold) = self.hold_mod_key.take() {
            sender::key(hold, false);
        }
    }

    fn swallow_add(&mut self, scan: u16) {
        for i in 0..self.swallow_n {
            if self.swallow[i] == scan {
                return; // オートリピート
            }
        }
        if self.swallow_n < self.swallow.len() {
            self.swallow[self.swallow_n] = scan;
            self.swallow_n += 1;
        }
    }

    fn swallow_remove(&mut self, scan: u16) -> bool {
        for i in 0..self.swallow_n {
            if self.swallow[i] == scan {
                self.swallow_n -= 1;
                self.swallow[i] = self.swallow[self.swallow_n];
                return true;
            }
        }
        false
    }

    fn send_combo(&mut self, combo: &Combo) {
        let unrestored = sender::send_combo(combo, self.phys_mask);
        if unrestored == 0 {
            return;
        }
        // Sender が外したまま戻さなかった Alt/Win。OS から見れば既に離れているので、
        // ユーザーが実際に指を離したときの key up は捨てる。
        for bit in 0..8u32 {
            if unrestored & (1 << bit) == 0 {
                continue;
            }
            self.phys_mask &= !(1u8 << bit);
            let scan =
                unsafe { MapVirtualKeyW(modmask::VKS[bit as usize] as u32, MAPVK_VK_TO_VSC) } as u16;
            self.swallow_add(scan);
        }
    }

    fn execute(&mut self, prefix_idx: Option<usize>, hotkey_idx: Option<usize>, map_idx: usize) {
        // 借用を切るためにいったん複製する。発火は打鍵のたびではないので許容範囲。
        let action: &Action = match (prefix_idx, hotkey_idx) {
            (Some(p), _) => &self.prefixes[p].map[map_idx].action,
            (_, Some(h)) => &self.hotkeys[h].binding.action,
            _ => return,
        };
        match action {
            Action::Command(c) => {
                let c = c.clone();
                self.pending.push(c);
                // 積むだけでは誰も拾わない。UI スレッドを起こす。
                // ここで直接実行してはいけない (LowLevelHooksTimeout でフックごと殺される)。
                crate::tray::poke_commands();
            }
            Action::Send(combo) => {
                let combo = combo.clone();
                self.send_combo(&combo);
            }
        }
    }

    /// フックのコールバック本体。true を返したらそのイベントを握り潰す。
    pub fn on_key(&mut self, w_param: WPARAM, info: &KBDLLHOOKSTRUCT) -> bool {
        // 打鍵ごとに走る。ログ無効時に文字列を組み立てないよう必ず enabled で括る。
        if log::enabled() {
            log::write(&format!(
                "hook msg=0x{:X} vk={} scan={} extra=0x{:X}",
                w_param,
                vk::name_of(info.vkCode as u16),
                info.scanCode,
                info.dwExtraInfo
            ));
        }

        // 自分が送ったイベントは触らない (無限ループ防止)
        if info.dwExtraInfo == INJECTED_TAG {
            return false;
        }
        if !self.enabled {
            return false;
        }

        let msg = w_param as u32;
        let is_down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        let is_up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
        let vk_code = info.vkCode as u16;

        // 物理修飾キーの追跡は握り潰しより先。ここを取りこぼすと Blind の判定が狂う。
        if let Some(bit) = modmask::bit_of(vk_code) {
            if is_down {
                self.phys_mask |= 1 << bit;
            } else if is_up {
                self.phys_mask &= !(1u8 << bit);
            }
        }

        if is_up {
            return self.on_up(info);
        }
        if is_down {
            return self.on_down(info);
        }
        false
    }

    fn on_up(&mut self, info: &KBDLLHOOKSTRUCT) -> bool {
        if let Some(idx) = self.active {
            if self.prefixes[idx].key.matches(info) {
                return self.on_prefix_up(idx);
            }
        }
        self.swallow_remove(info.scanCode as u16)
    }

    fn on_prefix_up(&mut self, idx: usize) -> bool {
        let used = self.active_used;
        self.release_hold_modifier();
        self.active = None;
        self.active_used = false;

        if !used {
            let (tap, timeout) = {
                let p = &self.prefixes[idx];
                (p.tap.clone(), p.tap_timeout_ms)
            };
            if let Some(tap) = tap {
                let held = now_ms().saturating_sub(self.active_down_at) as i64;
                if timeout <= 0 || held <= timeout {
                    self.send_combo(&tap);
                }
            }
        }
        true
    }

    fn on_down(&mut self, info: &KBDLLHOOKSTRUCT) -> bool {
        let vk_code = info.vkCode as u16;

        if let Some(idx) = self.active {
            // プレフィックス自身のオートリピート
            if self.prefixes[idx].key.matches(info) {
                return true;
            }

            if let Some(m) = self.prefixes[idx].map.iter().position(|b| b.key.matches(info)) {
                self.active_used = true;
                self.swallow_add(info.scanCode as u16);
                self.execute(Some(idx), None, m);
                return true;
            }

            // 修飾キー自体はプレフィックスを「使った」に数えない。
            // これで Space→Ctrl→C が Ctrl+Shift+C になり、Space+Ctrl だけなら Space が出る。
            if modmask::bit_of(vk_code).is_some() {
                return false;
            }

            if let Some(hold) = self.prefixes[idx].hold_modifier {
                self.active_used = true;
                if self.hold_mod_key.is_none() {
                    sender::key(hold, true);
                    self.hold_mod_key = Some(hold);
                }
                // 修飾キーより後に届くことを保証するため、元のキーも送り直す
                sender::raw(info, true);
                return true;
            }

            // Map にも無く hold_modifier も無い → 素通しするが、単打の扱いは取り消す
            self.active_used = true;
            return false;
        }

        // 修飾キーが押されている間はプレフィックスにしない。
        // これで Shift+Enter / Ctrl+Enter / Ctrl+Space が普通に通る。
        if self.phys_mask == 0 {
            if let Some(i) = self.prefixes.iter().position(|p| p.key.matches(info)) {
                self.active = Some(i);
                self.active_used = false;
                self.hold_mod_key = None;
                self.active_down_at = now_ms();
                return true;
            }
        }

        let mods = modmask::groups_of(self.phys_mask);
        if let Some(i) = self
            .hotkeys
            .iter()
            .position(|h| h.mods == mods && h.binding.key.matches(info))
        {
            self.swallow_add(info.scanCode as u16);
            self.execute(None, Some(i), 0);
            return true;
        }

        false
    }
}

fn parse_action(s: &str) -> Result<Action, String> {
    if s.starts_with('@') {
        return Ok(Action::Command(s.to_ascii_lowercase()));
    }
    Combo::parse(s).map(Action::Send)
}

// ---- フックの設置 -----------------------------------------------------------
//
// フックのコールバックは extern "system" fn で、コンテキストを渡す引数が無い。
// このスレッド (メッセージループのスレッド) からしか触らないので TLS に置く。

thread_local! {
    pub static ENGINE: core::cell::RefCell<Option<Engine>> = const { core::cell::RefCell::new(None) };
}

static mut HOOK: HHOOK = std::ptr::null_mut();

unsafe extern "system" fn hook_proc(code: i32, w_param: WPARAM, l_param: LPARAM) -> LRESULT {
    let hook = HOOK;
    if code < 0 {
        return CallNextHookEx(hook, code, w_param, l_param);
    }

    let info = &*(l_param as *const KBDLLHOOKSTRUCT);
    let swallow = ENGINE.with(|e| match e.try_borrow_mut() {
        Ok(mut g) => match g.as_mut() {
            Some(engine) => engine.on_key(w_param, info),
            None => false,
        },
        Err(_) => false,
    });

    if swallow {
        return 1;
    }
    CallNextHookEx(hook, code, w_param, l_param)
}

pub fn install() -> Result<(), String> {
    unsafe {
        if !HOOK.is_null() {
            return Ok(());
        }
        let h = SetWindowsHookExW(
            WH_KEYBOARD_LL,
            Some(hook_proc),
            GetModuleHandleW(std::ptr::null()),
            0,
        );
        if h.is_null() {
            return Err(format!(
                "キーボードフックを設定できませんでした (Win32 error {})。",
                windows_sys::Win32::Foundation::GetLastError()
            ));
        }
        HOOK = h;
    }
    Ok(())
}

pub fn uninstall() {
    unsafe {
        if HOOK.is_null() {
            return;
        }
        ENGINE.with(|e| {
            if let Ok(mut g) = e.try_borrow_mut() {
                if let Some(engine) = g.as_mut() {
                    engine.reset();
                }
            }
        });
        UnhookWindowsHookEx(HOOK);
        HOOK = std::ptr::null_mut();
    }
}

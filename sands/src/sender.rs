//! SendInput のラッパ。送るイベントには必ず INJECTED_TAG を付けてフック側で読み飛ばせるようにする。
//! この層はフックのコールバックから呼ばれるので、ヒープ確保をしない。

use crate::keyspec::{group_of, modmask, Combo, KeySpec, ModGroup};
use crate::vk;
use windows_sys::Win32::UI::Input::KeyboardAndMouse::*;
use windows_sys::Win32::UI::WindowsAndMessaging::KBDLLHOOKSTRUCT;

/// SendInput で自分が送ったイベントの目印。フック側でこの値を見たら素通しする。
pub const INJECTED_TAG: usize = 0x5A4D_5344; // "ZMSD"

const LLKHF_EXTENDED: u32 = 0x01;

fn send_one(vk_code: u16, scan: u16, flags: KEYBD_EVENT_FLAGS) {
    let input = INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: vk_code,
                wScan: scan,
                dwFlags: flags,
                time: 0,
                dwExtraInfo: INJECTED_TAG,
            },
        },
    };
    unsafe {
        SendInput(1, &input, core::mem::size_of::<INPUT>() as i32);
    }
}

fn is_extended(vk_code: u16) -> bool {
    matches!(
        vk_code,
        vk::R_CONTROL | vk::R_MENU | vk::L_WIN | vk::R_WIN | vk::APPS
            | 45 | 46 | 36 | 35 | 33 | 34
            | vk::LEFT | vk::RIGHT | vk::UP | vk::DOWN
            | 144 | 111 | 44
    )
}

pub fn key_spec(key: &KeySpec, down: bool) {
    let mut flags = if down { 0 } else { KEYEVENTF_KEYUP };
    if key.extended {
        flags |= KEYEVENTF_EXTENDEDKEY;
    }

    // sc 指定はスキャンコードで送る。仮想キーコードが配列や IME 状態で揺れるキー
    // (sc029 = 半角/全角 など) を、物理位置どおりに届けるため。
    let code = if key.by_scan {
        flags |= KEYEVENTF_SCANCODE;
        0
    } else {
        key.code
    };

    send_one(code, key.scan, flags);
}

pub fn key(vk_code: u16, down: bool) {
    let scan = unsafe { MapVirtualKeyW(vk_code as u32, MAPVK_VK_TO_VSC) } as u16;
    let mut flags = if down { 0 } else { KEYEVENTF_KEYUP };
    if is_extended(vk_code) {
        flags |= KEYEVENTF_EXTENDEDKEY;
    }
    send_one(vk_code, scan, flags);
}

/// フックが受け取った生のイベントを、そのまま送り直す。
pub fn raw(info: &KBDLLHOOKSTRUCT, down: bool) {
    let mut flags = if down { 0 } else { KEYEVENTF_KEYUP };
    if info.flags & LLKHF_EXTENDED != 0 {
        flags |= KEYEVENTF_EXTENDEDKEY;
    }
    send_one(info.vkCode as u16, info.scanCode as u16, flags);
}

/// コンボを送る。phys_mask はいま物理的に押されている修飾キーのビットマスク。
///
/// Blind でない場合、コンボに含まれない物理修飾キーを一時的に外してから送る
/// (AHK の Send の既定と同じ)。これが無いと "!sc027 → ^F12" が Alt+Ctrl+F12 になってしまう。
/// Blind の場合は物理修飾キーをそのまま残すので、"BackSpace & h → {Blind}Left" が
/// Shift 押下中なら Shift+Left (選択) になる。
///
/// 戻り値は「外したまま戻さなかった物理修飾キー (Alt/Win)」のビットマスク。
/// 呼び出し側は、これらの物理的な key up を握り潰す必要がある。
pub fn send_combo(combo: &Combo, phys_mask: u8) -> u8 {
    let phys_groups = modmask::groups_of(phys_mask);

    let mut pressed = [0u16; 4];
    let mut pressed_n = 0usize;

    // 1) コンボ側の修飾キーを先に押す。
    //    順序が重要。Alt を「単独で押して離した」と Windows に見せるとメニューモードに入り、
    //    後続のキーがメニューに食われて宛先に届かなくなる。先に何か押しておけば単独押しでなくなる。
    let mut press = |group: ModGroup, vk_code: u16| {
        if !combo.mods.contains(group) {
            return;
        }
        // Blind のときは、その系統が既に物理的に押されているなら二重に押さない
        if combo.blind && phys_groups.contains(group) {
            return;
        }
        key(vk_code, true);
        pressed[pressed_n] = vk_code;
        pressed_n += 1;
    };
    press(ModGroup::CTRL, vk::L_CONTROL);
    press(ModGroup::SHIFT, vk::L_SHIFT);
    press(ModGroup::ALT, vk::L_MENU);
    press(ModGroup::WIN, vk::L_WIN);
    drop(press);

    // 2) コンボに含まれない物理修飾キーを外す
    let mut released = [0u16; 8];
    let mut released_n = 0usize;
    let mut unrestored: u8 = 0;

    if !combo.blind && phys_mask != 0 {
        let mut masked = pressed_n > 0; // 1) で何か押していれば既にマスク済み

        for bit in 0..8u32 {
            if phys_mask & (1 << bit) == 0 {
                continue;
            }
            let held = modmask::VKS[bit as usize];
            let group = group_of(held);
            if combo.mods.contains(group) {
                continue;
            }

            let menu_risk = group == ModGroup::ALT || group == ModGroup::WIN;
            if menu_risk && !masked {
                // 単独の Alt/Win 押しを打ち消すためだけの無害な打鍵 (AHK と同じ手)
                key(vk::L_CONTROL, true);
                key(vk::L_CONTROL, false);
                masked = true;
            }

            key(held, false);

            // Alt/Win は押し直さない。押し直すと、そのあとユーザーが物理的に離した時点で
            // 「単独で押して離した」形になり、やはりメニューが開いてしまう。
            if menu_risk {
                unrestored |= 1 << bit;
            } else {
                released[released_n] = held;
                released_n += 1;
            }
        }
    }

    key_spec(&combo.key, true);
    key_spec(&combo.key, false);

    for i in (0..pressed_n).rev() {
        key(pressed[i], false);
    }
    for i in (0..released_n).rev() {
        key(released[i], true);
    }

    unrestored
}

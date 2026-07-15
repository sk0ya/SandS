//! 隠しウィンドウ + タスクトレイ + メッセージループ。

use crate::config::Config;
use crate::engine::{self, Engine, ENGINE};
use crate::icon;
use crate::startup::{self, Mode};
use crate::wide::wide;

use std::path::PathBuf;
use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, POINT, WPARAM};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::Shell::*;
use windows_sys::Win32::UI::WindowsAndMessaging::*;

const TRAY_ID: u32 = 1;
const WM_TRAY: u32 = WM_APP + 1;
const WM_RUN_COMMAND: u32 = WM_APP + 2;

const CMD_ENABLED: u32 = 1;
const CMD_STARTUP: u32 = 2;
const CMD_RELOAD: u32 = 3;
const CMD_EDIT: u32 = 4;
const CMD_EXIT: u32 = 5;

pub struct Tray {
    cfg_path: PathBuf,
    hwnd: HWND,
    icon_on: HICON,
    icon_off: HICON,
    enabled: bool,
    /// メニューを開くたびに schtasks を起動すると重いので、状態は持っておいて
    /// 起動時と変更時にだけ調べ直す。
    startup: Mode,
}

// WndProc からしか触らないので TLS に置く。
thread_local! {
    static TRAY: core::cell::RefCell<Option<Tray>> = const { core::cell::RefCell::new(None) };
}

pub fn msgbox(text: &str, icon: MESSAGEBOX_STYLE) {
    unsafe {
        MessageBoxW(
            std::ptr::null_mut(),
            wide(text).as_ptr(),
            wide("SandS").as_ptr(),
            MB_OK | icon,
        );
    }
}

pub fn error(t: &str) {
    msgbox(t, MB_ICONERROR)
}
pub fn warn(t: &str) {
    msgbox(t, MB_ICONWARNING)
}
pub fn info(t: &str) {
    msgbox(t, MB_ICONINFORMATION)
}

pub fn run(cfg_path: PathBuf) -> i32 {
    let hwnd = match create_window() {
        Ok(h) => h,
        Err(e) => {
            error(&e);
            return 1;
        }
    };

    let tray = Tray {
        cfg_path,
        hwnd,
        icon_on: icon::create(true),
        icon_off: icon::create(false),
        enabled: true,
        startup: Mode::None,
    };
    TRAY.with(|t| *t.borrow_mut() = Some(tray));

    if !load_engine(true) {
        return 1;
    }

    TRAY.with(|t| {
        let mut b = t.borrow_mut();
        let tr = b.as_mut().unwrap();
        tr.startup = startup::current();
    });

    update_tray(NIM_ADD);

    // メッセージループ
    unsafe {
        let mut msg: MSG = std::mem::zeroed();
        loop {
            let r = GetMessageW(&mut msg, std::ptr::null_mut(), 0, 0);
            if r == 0 {
                break;
            }
            if r == -1 {
                return 1;
            }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    // 後始末
    update_tray(NIM_DELETE);
    engine::uninstall();
    TRAY.with(|t| {
        if let Some(tr) = t.borrow().as_ref() {
            unsafe {
                DestroyIcon(tr.icon_on);
                DestroyIcon(tr.icon_off);
            }
        }
    });
    0
}

fn create_window() -> Result<HWND, String> {
    unsafe {
        let cls = wide("SandS.MessageWindow");
        let hinst = GetModuleHandleW(std::ptr::null());

        let wc = WNDCLASSEXW {
            cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
            style: 0,
            lpfnWndProc: Some(wnd_proc),
            cbClsExtra: 0,
            cbWndExtra: 0,
            hInstance: hinst,
            hIcon: std::ptr::null_mut(),
            hCursor: std::ptr::null_mut(),
            hbrBackground: std::ptr::null_mut(),
            lpszMenuName: std::ptr::null(),
            lpszClassName: cls.as_ptr(),
            hIconSm: std::ptr::null_mut(),
        };
        if RegisterClassExW(&wc) == 0 {
            return Err("ウィンドウクラスを登録できませんでした。".into());
        }

        // メッセージ専用ウィンドウ (HWND_MESSAGE) にはしない。
        // TrackPopupMenu はフォアグラウンドウィンドウを必要とするため。
        let h = CreateWindowExW(
            0,
            cls.as_ptr(),
            std::ptr::null(),
            0,
            0,
            0,
            0,
            0,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            hinst,
            std::ptr::null(),
        );
        if h.is_null() {
            return Err("ウィンドウを作成できませんでした。".into());
        }
        Ok(h)
    }
}

unsafe extern "system" fn wnd_proc(hwnd: HWND, msg: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    match msg {
        WM_TRAY => {
            match l as u32 {
                WM_RBUTTONUP => show_menu(),
                WM_LBUTTONDBLCLK => set_enabled(!is_enabled()),
                _ => {}
            }
            0
        }
        WM_RUN_COMMAND => {
            // フックが溜めたコマンドを UI スレッドで実行する
            let cmds: Vec<String> = ENGINE.with(|e| {
                e.borrow_mut()
                    .as_mut()
                    .map(|eng| std::mem::take(&mut eng.pending))
                    .unwrap_or_default()
            });
            for c in cmds {
                handle_command(&c);
            }
            0
        }
        WM_CLOSE => {
            DestroyWindow(hwnd);
            0
        }
        WM_DESTROY => {
            PostQuitMessage(0);
            0
        }
        _ => DefWindowProcW(hwnd, msg, w, l),
    }
}

/// フックはコマンドを Engine.pending に積むだけ。実行はここで拾って UI スレッドで行う。
/// フックの中で設定再読込やダイアログを走らせると LowLevelHooksTimeout でフックごと殺される。
pub fn poke_commands() {
    TRAY.with(|t| {
        if let Some(tr) = t.borrow().as_ref() {
            unsafe {
                PostMessageW(tr.hwnd, WM_RUN_COMMAND, 0, 0);
            }
        }
    });
}

fn is_enabled() -> bool {
    TRAY.with(|t| t.borrow().as_ref().map(|x| x.enabled).unwrap_or(false))
}

fn set_enabled(on: bool) {
    TRAY.with(|t| {
        if let Some(tr) = t.borrow_mut().as_mut() {
            tr.enabled = on;
        }
    });
    ENGINE.with(|e| {
        if let Some(eng) = e.borrow_mut().as_mut() {
            eng.set_enabled(on);
        }
    });
    update_tray(NIM_MODIFY);
}

fn nid(flags: NOTIFY_ICON_DATA_FLAGS) -> NOTIFYICONDATAW {
    TRAY.with(|t| {
        let b = t.borrow();
        let tr = b.as_ref().unwrap();

        let mut d: NOTIFYICONDATAW = unsafe { std::mem::zeroed() };
        d.cbSize = std::mem::size_of::<NOTIFYICONDATAW>() as u32;
        d.hWnd = tr.hwnd;
        d.uID = TRAY_ID;
        d.uFlags = flags;
        d.uCallbackMessage = WM_TRAY;
        d.hIcon = if tr.enabled { tr.icon_on } else { tr.icon_off };

        // 昇格していないと管理者権限のウィンドウ上でだけ効かない。
        // 気づける場所が無いと原因不明の不具合に見えるので出しておく。
        let tip = format!(
            "SandS — {}{}",
            if tr.enabled { "有効" } else { "停止中" },
            if startup::is_elevated() { " (管理者)" } else { "" }
        );
        for (i, c) in wide(&tip).iter().take(127).enumerate() {
            d.szTip[i] = *c;
        }
        d
    })
}

fn update_tray(op: NOTIFY_ICON_MESSAGE) {
    let mut d = nid(NIF_MESSAGE | NIF_ICON | NIF_TIP);
    unsafe {
        Shell_NotifyIconW(op, &mut d);
    }
}

fn balloon(text: &str) {
    let mut d = nid(NIF_INFO);
    for (i, c) in wide(text).iter().take(255).enumerate() {
        d.szInfo[i] = *c;
    }
    for (i, c) in wide("SandS").iter().take(63).enumerate() {
        d.szInfoTitle[i] = *c;
    }
    d.Anonymous.uTimeout = 3000;
    unsafe {
        Shell_NotifyIconW(NIM_MODIFY, &mut d);
    }
}

fn show_menu() {
    let (hwnd, enabled, startup_mode) = TRAY.with(|t| {
        let b = t.borrow();
        let tr = b.as_ref().unwrap();
        (tr.hwnd, tr.enabled, tr.startup)
    });

    unsafe {
        let menu = CreatePopupMenu();
        let chk = |on: bool| if on { MF_CHECKED } else { 0 };

        AppendMenuW(menu, MF_STRING | chk(enabled), CMD_ENABLED as usize, wide("有効(&E)").as_ptr());

        // 管理者権限のウィンドウ上で効くかどうかが、ここで分かるようにしておく。
        // 効かないときに理由が分からないのが一番困るため。
        let label = match startup_mode {
            Mode::Task => "Windows 起動時に開始(&S) — 管理者",
            Mode::Registry => "Windows 起動時に開始(&S) — 通常",
            Mode::None => "Windows 起動時に開始(&S)",
        };
        AppendMenuW(
            menu,
            MF_STRING | chk(startup_mode != Mode::None),
            CMD_STARTUP as usize,
            wide(label).as_ptr(),
        );
        AppendMenuW(menu, MF_SEPARATOR, 0, std::ptr::null());
        AppendMenuW(menu, MF_STRING, CMD_RELOAD as usize, wide("設定を再読み込み(&R)").as_ptr());
        AppendMenuW(menu, MF_STRING, CMD_EDIT as usize, wide("設定ファイルを編集(&O)").as_ptr());
        AppendMenuW(menu, MF_SEPARATOR, 0, std::ptr::null());
        AppendMenuW(menu, MF_STRING, CMD_EXIT as usize, wide("終了(&X)").as_ptr());

        let mut pt = POINT { x: 0, y: 0 };
        GetCursorPos(&mut pt);
        // これが無いとメニューの外をクリックしても閉じない (Win32 の既知の作法)
        SetForegroundWindow(hwnd);
        let cmd = TrackPopupMenuEx(
            menu,
            (TPM_RIGHTBUTTON | TPM_RETURNCMD) as u32,
            pt.x,
            pt.y,
            hwnd,
            std::ptr::null(),
        );
        DestroyMenu(menu);

        match cmd as u32 {
            CMD_ENABLED => set_enabled(!enabled),
            CMD_STARTUP => toggle_startup(),
            CMD_RELOAD => reload(),
            CMD_EDIT => edit_config(),
            CMD_EXIT => {
                DestroyWindow(hwnd);
            }
            _ => {}
        }
    }
}

fn handle_command(cmd: &str) {
    match cmd {
        "@reload" => reload(),
        "@edit" => edit_config(),
        "@toggle" => set_enabled(!is_enabled()),
        "@exit" => TRAY.with(|t| {
            if let Some(tr) = t.borrow().as_ref() {
                unsafe {
                    DestroyWindow(tr.hwnd);
                }
            }
        }),
        other => balloon(&format!("知らないコマンドです: {other}")),
    }
}

fn cfg_path() -> PathBuf {
    TRAY.with(|t| t.borrow().as_ref().unwrap().cfg_path.clone())
}

fn load_engine(initial: bool) -> bool {
    let path = cfg_path();
    let mut problems = Vec::new();
    let cfg = Config::load(&path, &mut problems);
    let mut eng = Engine::new(&cfg, &mut problems);

    if let Err(e) = engine::install() {
        if initial {
            error(&e);
        } else {
            warn(&format!("設定を再読み込みできませんでした。元の設定のまま続けます。\n\n{e}"));
        }
        return false;
    }

    eng.set_enabled(is_enabled());
    let (np, nh) = eng.counts();
    crate::log::write(&format!("Install: prefixes={np} hotkeys={nh}"));
    ENGINE.with(|e| *e.borrow_mut() = Some(eng));

    if !problems.is_empty() {
        warn(&format!(
            "設定に解釈できない箇所があります。該当分だけ無効にして続けます。\n\n{}",
            problems.join("\n")
        ));
    }
    true
}

fn reload() {
    if load_engine(false) {
        update_tray(NIM_MODIFY);
        balloon("設定を再読み込みしました。");
    }
}

fn edit_config() {
    let path = cfg_path();
    if !path.exists() {
        if let Err(e) = Config::default_config().save(&path) {
            warn(&format!("設定ファイルを作成できませんでした。\n{}\n\n{e}", path.display()));
            return;
        }
    }
    let r = unsafe {
        ShellExecuteW(
            std::ptr::null_mut(),
            wide("open").as_ptr(),
            wide(&path.to_string_lossy()).as_ptr(),
            std::ptr::null(),
            std::ptr::null(),
            SW_SHOWNORMAL,
        )
    };
    if (r as isize) <= 32 {
        warn(&format!("設定ファイルを開けませんでした。\n{}", path.display()));
    } else {
        balloon("保存したら「設定を再読み込み」(または BackSpace+R) で反映されます。");
    }
}

fn toggle_startup() {
    let cur = TRAY.with(|t| t.borrow().as_ref().unwrap().startup);
    let note = if cur == Mode::None {
        let (m, n) = startup::enable();
        TRAY.with(|t| t.borrow_mut().as_mut().unwrap().startup = m);
        n
    } else {
        let n = startup::disable();
        TRAY.with(|t| t.borrow_mut().as_mut().unwrap().startup = startup::current());
        n
    };
    if let Some(n) = note {
        warn(&n);
    }
}

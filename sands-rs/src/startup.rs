//! 自動起動の登録。
//!
//! 非昇格プロセスのフックは、昇格したアプリへの入力に介入できない。つまり HKCU\Run で
//! 登録すると、管理者として実行しているウィンドウの上でだけ SandS が効かなくなる。
//! これを避けるには「最上位の特権で実行する」タスクとして登録する必要がある
//! (HKCU\Run には昇格して起動する手段が無い。あれば毎回 UAC が出てしまう)。
//!
//! タスクの登録自体に管理者権限が要るので、一度だけ SandS を管理者として実行してもらう。

use crate::wide::wide;
use std::process::Command;
use windows_sys::Win32::Foundation::{CloseHandle, ERROR_SUCCESS, HANDLE};
use windows_sys::Win32::Security::{GetTokenInformation, TokenElevation, TOKEN_ELEVATION, TOKEN_QUERY};
use windows_sys::Win32::System::Registry::*;
use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

const REG_PATH: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";
const VALUE_NAME: &str = "SandS";
const TASK_NAME: &str = "SandS";

#[derive(PartialEq, Eq, Clone, Copy, Debug)]
pub enum Mode {
    /// 自動起動しない。
    None,
    /// HKCU\Run。非昇格で起動するので、管理者権限のウィンドウ上では SandS が効かない。
    Registry,
    /// タスクスケジューラ。最上位の特権で起動するので、管理者権限のウィンドウ上でも効く。
    Task,
}

pub fn is_elevated() -> bool {
    unsafe {
        let mut token: HANDLE = std::ptr::null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return false;
        }
        let mut elev = TOKEN_ELEVATION { TokenIsElevated: 0 };
        let mut len = 0u32;
        let ok = GetTokenInformation(
            token,
            TokenElevation,
            &mut elev as *mut _ as *mut _,
            std::mem::size_of::<TOKEN_ELEVATION>() as u32,
            &mut len,
        );
        CloseHandle(token);
        ok != 0 && elev.TokenIsElevated != 0
    }
}

pub fn current() -> Mode {
    if task_exists() {
        return Mode::Task;
    }
    if registry_has_value() {
        Mode::Registry
    } else {
        Mode::None
    }
}

/// 登録する。昇格していればタスクとして、していなければ HKCU\Run に。
pub fn enable() -> (Mode, Option<String>) {
    let exe = std::env::current_exe()
        .map(|p| p.to_string_lossy().into_owned())
        .unwrap_or_default();

    let note: Option<String>;

    if is_elevated() {
        match create_task(&exe) {
            Ok(()) => {
                remove_registry(); // 二重起動しないよう、もう一方は消しておく
                return (Mode::Task, None);
            }
            Err(e) => {
                note = Some(format!("タスクとして登録できなかったので HKCU\\Run に登録します。\n\n{e}"));
            }
        }
    } else {
        note = Some(
            "SandS は管理者として実行されていないので、HKCU\\Run に登録しました。\n\n\
             この場合、管理者権限で動いているウィンドウの上では SandS が効きません。\n\
             そこでも効かせたい場合は、SandS を一度「管理者として実行」してから、\n\
             このメニューで登録し直してください (タスクとして登録されます)。"
                .to_string(),
        );
    }

    match registry_set(&exe) {
        Ok(()) => {
            remove_task();
            (Mode::Registry, note)
        }
        Err(e) => (Mode::None, Some(format!("スタートアップに登録できませんでした。\n\n{e}"))),
    }
}

pub fn disable() -> Option<String> {
    remove_registry();
    if !task_exists() {
        return None;
    }
    if !is_elevated() {
        return Some(
            "タスクとして登録されていますが、解除には管理者権限が必要です。\n\
             SandS を「管理者として実行」してから解除してください。"
                .to_string(),
        );
    }
    remove_task();
    None
}

// ---- レジストリ ------------------------------------------------------------

fn registry_has_value() -> bool {
    unsafe {
        let mut h: HKEY = std::ptr::null_mut();
        if RegOpenKeyExW(HKEY_CURRENT_USER, wide(REG_PATH).as_ptr(), 0, KEY_READ, &mut h)
            != ERROR_SUCCESS
        {
            return false;
        }
        let rc = RegQueryValueExW(
            h,
            wide(VALUE_NAME).as_ptr(),
            std::ptr::null(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
        );
        RegCloseKey(h);
        rc == ERROR_SUCCESS
    }
}

fn registry_set(exe: &str) -> Result<(), String> {
    unsafe {
        let mut h: HKEY = std::ptr::null_mut();
        let rc = RegCreateKeyExW(
            HKEY_CURRENT_USER,
            wide(REG_PATH).as_ptr(),
            0,
            std::ptr::null(),
            0,
            KEY_WRITE,
            std::ptr::null(),
            &mut h,
            std::ptr::null_mut(),
        );
        if rc != ERROR_SUCCESS {
            return Err(format!("レジストリキーを開けませんでした (error {rc})。"));
        }

        let val = wide(&format!("\"{exe}\""));
        let rc = RegSetValueExW(
            h,
            wide(VALUE_NAME).as_ptr(),
            0,
            REG_SZ,
            val.as_ptr() as *const u8,
            (val.len() * 2) as u32, // 終端の NUL を含むバイト数
        );
        RegCloseKey(h);

        if rc != ERROR_SUCCESS {
            return Err(format!("レジストリに書けませんでした (error {rc})。"));
        }
        Ok(())
    }
}

fn remove_registry() {
    unsafe {
        let mut h: HKEY = std::ptr::null_mut();
        if RegOpenKeyExW(HKEY_CURRENT_USER, wide(REG_PATH).as_ptr(), 0, KEY_WRITE, &mut h)
            != ERROR_SUCCESS
        {
            return;
        }
        RegDeleteValueW(h, wide(VALUE_NAME).as_ptr());
        RegCloseKey(h);
    }
}

// ---- タスクスケジューラ ----------------------------------------------------

fn schtasks(args: &[&str]) -> i32 {
    Command::new("schtasks.exe")
        .args(args)
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .status()
        .ok()
        .and_then(|s| s.code())
        .unwrap_or(-1)
}

fn task_exists() -> bool {
    schtasks(&["/Query", "/TN", TASK_NAME]) == 0
}

fn remove_task() {
    schtasks(&["/Delete", "/TN", TASK_NAME, "/F"]);
}

fn create_task(exe: &str) -> Result<(), String> {
    let xml_path = std::env::temp_dir().join("sands.task.xml");

    // schtasks /XML は UTF-16 の XML しか受け付けない
    let xml = task_xml(exe);
    let mut bytes: Vec<u8> = vec![0xFF, 0xFE]; // BOM
    for u in xml.encode_utf16() {
        bytes.extend_from_slice(&u.to_le_bytes());
    }
    std::fs::write(&xml_path, &bytes).map_err(|e| e.to_string())?;

    let rc = schtasks(&[
        "/Create",
        "/TN",
        TASK_NAME,
        "/XML",
        &xml_path.to_string_lossy(),
        "/F",
    ]);
    let _ = std::fs::remove_file(&xml_path);

    match rc {
        0 => Ok(()),
        1 => Err("アクセスが拒否されました。タスクの登録には管理者権限が必要です。".into()),
        _ => Err(format!("schtasks が {rc} で終了しました。")),
    }
}

fn esc(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
}

/// XML が壊れていても schtasks のエラーになるだけで気づきにくいので、テストから検証できるようにしてある。
pub fn task_xml(exe: &str) -> String {
    let user = format!(
        "{}\\{}",
        std::env::var("USERDOMAIN").unwrap_or_default(),
        std::env::var("USERNAME").unwrap_or_default()
    );
    format!(
        r#"<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>SandS — キーカスタマイズ常駐ソフト。管理者権限のウィンドウ上でも効くよう、最上位の特権で起動する。</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{cmd}</Command>
    </Exec>
  </Actions>
</Task>"#,
        user = esc(&user),
        cmd = esc(exe)
    )
}

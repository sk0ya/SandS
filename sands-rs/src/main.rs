// コンソールウィンドウを出さない
#![windows_subsystem = "windows"]

#[macro_use]
mod bits;

mod config;
mod engine;
mod icon;
mod keyspec;
mod log;
mod sender;
mod startup;
mod tray;
mod vk;
mod wide;

#[cfg(test)]
mod tests;

use std::path::PathBuf;
use wide::wide;
use windows_sys::Win32::Foundation::{GetLastError, ERROR_ALREADY_EXISTS};
use windows_sys::Win32::System::Threading::CreateMutexW;

fn main() {
    let args: Vec<String> = std::env::args().collect();

    // --config <path>: 別の設定で起動する。E2E テストが実設定を壊さずに走るためにも使う。
    let mut cfg_path: PathBuf = config::default_path();
    if let Some(i) = args.iter().position(|a| a == "--config" || a == "-c") {
        if let Some(p) = args.get(i + 1) {
            cfg_path = std::fs::canonicalize(p)
                .unwrap_or_else(|_| PathBuf::from(p))
                // canonicalize は \\?\ 付きのパスを返すので落とす
                .to_string_lossy()
                .trim_start_matches(r"\\?\")
                .into();
        }
    }

    // 多重起動すると同じキーを二重に握り潰して壊れるので防ぐ。
    // 設定が違えば別インスタンスとして起動できてよい。
    let mutex_name = format!("Local\\SandS.SingleInstance.{:08X}", hash(&cfg_path.to_string_lossy()));
    let _mutex = unsafe { CreateMutexW(std::ptr::null(), 1, wide(&mutex_name).as_ptr()) };
    if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
        tray::info("SandS はすでに起動しています。");
        return;
    }

    std::process::exit(tray::run(cfg_path));
}

fn hash(s: &str) -> u32 {
    // 名前を一意にできれば十分なので FNV-1a で足りる
    let mut h: u32 = 2166136261;
    for b in s.as_bytes() {
        h ^= *b as u32;
        h = h.wrapping_mul(16777619);
    }
    h
}

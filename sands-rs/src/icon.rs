//! トレイアイコンをピクセルから作る。
//! 絵柄は Shift の山記号 + スペースバー。有効なら緑、停止中は灰色。

use windows_sys::Win32::Foundation::HANDLE;
use windows_sys::Win32::Graphics::Gdi::{CreateBitmap, DeleteObject};
use windows_sys::Win32::UI::WindowsAndMessaging::{CreateIconIndirect, HICON, ICONINFO};

const SIZE: i32 = 32;
const N: usize = (SIZE * SIZE) as usize;

pub fn create(enabled: bool) -> HICON {
    let fg: u32 = if enabled { 0xFF2E7D32 } else { 0xFF757575 }; // 0xAARRGGBB
    let mut px = [0u32; N];

    let put = |px: &mut [u32; N], x: i32, y: i32, c: u32| {
        if (0..SIZE).contains(&x) && (0..SIZE).contains(&y) {
            px[(y * SIZE + x) as usize] = c;
        }
    };

    // Shift の山 (上向き三角)
    for y in 4..=16 {
        let half = y - 4;
        for x in (16 - half)..=(15 + half) {
            put(&mut px, x, y, fg);
        }
    }
    // 三角の足
    for y in 17..=20 {
        for x in 12..=19 {
            put(&mut px, x, y, fg);
        }
    }
    // スペースバー
    for y in 24..=28 {
        for x in 3..=28 {
            put(&mut px, x, y, fg);
        }
    }

    unsafe {
        let color = CreateBitmap(SIZE, SIZE, 1, 32, px.as_ptr() as *const _);
        // 32bpp のアルファを使うのでマスクは全 0 (不透明) でよい
        let mask = CreateBitmap(SIZE, SIZE, 1, 1, std::ptr::null());

        let ii = ICONINFO {
            fIcon: 1,
            xHotspot: 0,
            yHotspot: 0,
            hbmMask: mask,
            hbmColor: color,
        };
        let icon = CreateIconIndirect(&ii);

        DeleteObject(color as HANDLE);
        DeleteObject(mask as HANDLE);
        icon
    }
}

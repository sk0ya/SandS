//! トレイ/exe アイコンは assets/SandS.ico を build.rs でリソース埋め込みしたものを使う。
//! 有効: 埋め込みアイコンそのまま。停止中: グレースケール化して色で状態を示す。

use windows_sys::Win32::Foundation::HANDLE;
use windows_sys::Win32::Graphics::Gdi::{
    CreateBitmap, DeleteObject, GetDC, GetDIBits, GetObjectW, ReleaseDC, BITMAP, BITMAPINFO,
    BITMAPINFOHEADER, BI_RGB, DIB_RGB_COLORS,
};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CreateIconIndirect, DestroyIcon, GetIconInfo, LoadImageW, HICON, ICONINFO, IMAGE_ICON,
    LR_DEFAULTCOLOR,
};

const RESOURCE_ID: usize = 1;
const SIZE: i32 = 32;

pub fn create(enabled: bool) -> HICON {
    let icon = load_resource_icon();
    if enabled {
        icon
    } else {
        let gray = to_grayscale(icon);
        unsafe {
            DestroyIcon(icon);
        }
        gray
    }
}

fn load_resource_icon() -> HICON {
    unsafe {
        let hinst = GetModuleHandleW(std::ptr::null());
        LoadImageW(
            hinst,
            RESOURCE_ID as *const u16,
            IMAGE_ICON,
            SIZE,
            SIZE,
            LR_DEFAULTCOLOR,
        ) as HICON
    }
}

/// 色付きアイコンの RGB を輝度でグレースケール化する (アルファは維持)。
fn to_grayscale(icon: HICON) -> HICON {
    unsafe {
        let mut info: ICONINFO = std::mem::zeroed();
        if GetIconInfo(icon, &mut info) == 0 {
            return icon;
        }

        let mut bmp: BITMAP = std::mem::zeroed();
        GetObjectW(
            info.hbmColor,
            std::mem::size_of::<BITMAP>() as i32,
            &mut bmp as *mut _ as *mut _,
        );
        let w = bmp.bmWidth;
        let h = bmp.bmHeight;
        let mut px = vec![0u32; (w * h) as usize];

        let mut bmi: BITMAPINFO = std::mem::zeroed();
        bmi.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
        bmi.bmiHeader.biWidth = w;
        bmi.bmiHeader.biHeight = -h; // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;

        let dc = GetDC(std::ptr::null_mut());
        GetDIBits(
            dc,
            info.hbmColor,
            0,
            h as u32,
            px.as_mut_ptr() as *mut _,
            &mut bmi,
            DIB_RGB_COLORS,
        );
        ReleaseDC(std::ptr::null_mut(), dc);

        for p in px.iter_mut() {
            let a = (*p >> 24) & 0xFF;
            let r = (*p >> 16) & 0xFF;
            let g = (*p >> 8) & 0xFF;
            let b = *p & 0xFF;
            let l = (r * 30 + g * 59 + b * 11) / 100;
            *p = (a << 24) | (l << 16) | (l << 8) | l;
        }

        let gray_color = CreateBitmap(w, h, 1, 32, px.as_ptr() as *const _);
        let ii = ICONINFO {
            fIcon: 1,
            xHotspot: 0,
            yHotspot: 0,
            hbmMask: info.hbmMask,
            hbmColor: gray_color,
        };
        let new_icon = CreateIconIndirect(&ii);

        DeleteObject(info.hbmColor as HANDLE);
        DeleteObject(info.hbmMask as HANDLE);
        DeleteObject(gray_color as HANDLE);

        new_icon
    }
}

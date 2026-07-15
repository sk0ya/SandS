//! Win32 に渡す UTF-16 文字列。

/// NUL 終端した UTF-16 バッファ。ポインタを使う間、戻り値を生かしておくこと。
pub fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

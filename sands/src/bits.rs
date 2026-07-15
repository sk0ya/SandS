//! ビットフラグ用の最小マクロ。
//!
//! bitflags クレートを足すほどの用途ではない (欲しいのは 4 ビット分の or と contains だけ)。

#[macro_export]
macro_rules! bitflags_lite {
    (
        $(#[$outer:meta])*
        pub struct $name:ident: $ty:ty {
            $(const $flag:ident = $value:expr;)*
        }
    ) => {
        $(#[$outer])*
        #[derive(Copy, Clone, PartialEq, Eq, Debug, Default)]
        pub struct $name(pub $ty);

        impl $name {
            pub const NONE: $name = $name(0);
            $(pub const $flag: $name = $name($value);)*

            #[inline]
            pub fn contains(self, other: $name) -> bool {
                self.0 & other.0 == other.0
            }
        }

        impl core::ops::BitOr for $name {
            type Output = $name;
            #[inline]
            fn bitor(self, rhs: $name) -> $name { $name(self.0 | rhs.0) }
        }

        impl core::ops::BitOrAssign for $name {
            #[inline]
            fn bitor_assign(&mut self, rhs: $name) { self.0 |= rhs.0; }
        }
    };
}

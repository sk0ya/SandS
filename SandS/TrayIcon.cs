using static SandS.Native;

namespace SandS;

/// <summary>
/// トレイアイコンを System.Drawing 抜きで用意する。
/// WinForms/System.Drawing を参照した時点でトリミングと NativeAOT が使えなくなるので、
/// 32bpp のピクセルを自前で塗って GDI に渡す。
///
/// 絵柄は Shift の山記号 + スペースバー。有効なら緑、停止中は灰色。
/// </summary>
internal static unsafe class IconFactory
{
    private const int Size = 32;

    public static IntPtr Create(bool enabled)
    {
        uint fg = enabled ? 0xFF2E7D32u : 0xFF757575u;   // 0xAARRGGBB
        uint* px = stackalloc uint[Size * Size];
        for (int i = 0; i < Size * Size; i++) px[i] = 0x00000000u;

        // Shift の山 (上向き三角)
        for (int y = 4; y <= 16; y++)
        {
            int half = (y - 4);
            for (int x = 16 - half; x <= 15 + half; x++)
                if (x >= 0 && x < Size) px[y * Size + x] = fg;
        }
        // 三角の足
        FillRect(px, 12, 17, 19, 20, fg);

        // スペースバー
        FillRect(px, 3, 24, 28, 28, fg);

        IntPtr color = CreateBitmap(Size, Size, 1, 32, px);
        // 32bpp のアルファを使うのでマスクは全 0 (不透明) でよい
        IntPtr mask = CreateBitmap(Size, Size, 1, 1, null);

        var ii = new ICONINFO { fIcon = 1, xHotspot = 0, yHotspot = 0, hbmMask = mask, hbmColor = color };
        IntPtr icon = CreateIconIndirect(ref ii);

        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    private static void FillRect(uint* px, int x0, int y0, int x1, int y1, uint c)
    {
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if ((uint)x < Size && (uint)y < Size) px[y * Size + x] = c;
    }
}

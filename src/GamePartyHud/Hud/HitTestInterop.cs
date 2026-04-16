using System;
using System.Runtime.InteropServices;

namespace GamePartyHud.Hud;

internal static class HitTestInterop
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;

    public const int WM_NCHITTEST = 0x0084;
    public const int HTCLIENT = 1;
    public const int HTTRANSPARENT = -1;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static IntPtr GetWindowLongCompat(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static void SetWindowLongCompat(IntPtr hWnd, int nIndex, IntPtr value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, nIndex, value);
        else SetWindowLong32(hWnd, nIndex, value.ToInt32());
    }

    public static void ApplyExtendedStyles(IntPtr hwnd)
    {
        long current = GetWindowLongCompat(hwnd, GWL_EXSTYLE).ToInt64();
        long applied = current | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
        SetWindowLongCompat(hwnd, GWL_EXSTYLE, new IntPtr(applied));
    }

    // LOWORD/HIWORD of an LPARAM carrying (x, y) screen coordinates (signed 16-bit).
    public static short LoWord(IntPtr l) => unchecked((short)(l.ToInt64() & 0xFFFF));
    public static short HiWord(IntPtr l) => unchecked((short)((l.ToInt64() >> 16) & 0xFFFF));
}

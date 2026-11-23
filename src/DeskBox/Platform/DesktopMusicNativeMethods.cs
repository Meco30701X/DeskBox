using System.Runtime.InteropServices;

namespace DeskBox.Platform;

internal static class DesktopMusicNativeMethods
{
    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int GetWindowTextLengthW(nint hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern unsafe int GetWindowTextW(nint hWnd, char* lpString, int nMaxCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern unsafe int GetClassNameW(nint hWnd, char* lpClassName, int nMaxCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);
}
